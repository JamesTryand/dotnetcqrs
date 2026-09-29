using DotnetCqrs.EventStore;

namespace DotnetCqrs.Host;

/// <summary>A reader's replication freshness, as the health/telemetry contract names it
/// (<c>STATE-MACHINES.md</c>, machine 4, <c>ReplicationFreshness</c>). A writer has none.</summary>
public enum ReplicationState
{
    /// <summary>No heartbeat row visible yet, or it could not be read: nothing trustworthy to serve.</summary>
    Unknown,

    /// <summary>The heartbeat is within the stale threshold.</summary>
    Fresh,

    /// <summary>Stale while the writer answers: this reader's own replication is behind.</summary>
    StaleWriterUp,

    /// <summary>Stale and the writer does not answer: a shared cause, so the reader stays in the pool.</summary>
    StaleWriterDown,
}

/// <summary>A reader's latest measurement: its state and the heartbeat's age (0 when unknown).</summary>
public sealed record ReplicationStatus(ReplicationState State, double WriteLagSeconds);

/// <summary>
/// The writer's side of the heartbeat (health/telemetry contract section 5): upserts the one row
/// every <c>interval</c> until cancelled. A failed write is logged once per run of failures and
/// retried on the next beat; it never stops the loop.
/// </summary>
public static class WriterHeartbeatLoop
{
    public static async Task RunAsync(
        IHeartbeatStore store, string writerNodeId, string writerOpsUrl, TimeSpan interval,
        Action<string>? log = null, TimeProvider? timeProvider = null, CancellationToken ct = default)
    {
        var time = timeProvider ?? TimeProvider.System;
        var failing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await store.WriteHeartbeatAsync(writerNodeId, writerOpsUrl, time.GetUtcNow(), ct);
                if (failing) log?.Invoke("writer heartbeat: writing again");
                failing = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!failing) log?.Invoke($"writer heartbeat: write failed, readers will see this writer as stale: {ex.Message}");
                failing = true;
            }
            try
            {
                await Task.Delay(interval, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// A reader's side of the heartbeat (machine 4): reads the row the writer upserts, and when it is
/// older than the stale threshold asks the writer's <c>/healthz</c> (at the row's
/// <c>writer_ops_url</c>) whether the writer is up, which splits stale into a local problem
/// (<see cref="ReplicationState.StaleWriterUp"/>) and a shared one
/// (<see cref="ReplicationState.StaleWriterDown"/>). Clocks are assumed to agree (NTP); a heartbeat
/// from the future counts as age 0.
/// </summary>
public sealed class ReplicationMonitor(
    IHeartbeatStore store, HttpClient probe, TimeSpan staleThreshold, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private volatile ReplicationStatus _current = new(ReplicationState.Unknown, 0);

    /// <summary>The latest measurement; <see cref="ReplicationState.Unknown"/> until the first.</summary>
    public ReplicationStatus Current => _current;

    /// <summary>Measures once and records the result.</summary>
    public async Task<ReplicationStatus> MeasureAsync(CancellationToken ct = default)
    {
        WriterHeartbeat? row;
        try
        {
            row = await store.ReadHeartbeatAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            row = null;
        }
        if (row is null || HeartbeatTimestamp.Parse(row.WrittenAt) is not { } writtenAt)
            return _current = new ReplicationStatus(ReplicationState.Unknown, 0);

        var lag = Math.Max(0, (_time.GetUtcNow() - writtenAt).TotalSeconds);
        if (lag <= staleThreshold.TotalSeconds)
            return _current = new ReplicationStatus(ReplicationState.Fresh, lag);
        var up = await WriterAnswersAsync(row.WriterOpsUrl, ct);
        return _current = new ReplicationStatus(up ? ReplicationState.StaleWriterUp : ReplicationState.StaleWriterDown, lag);
    }

    /// <summary>The <c>writer</c> dependency's check (machine 3, on a reader): throws unless the
    /// heartbeat names a writer and that writer's <c>/healthz</c> answers.</summary>
    public async Task CheckWriterAsync(CancellationToken ct = default)
    {
        var row = await store.ReadHeartbeatAsync(ct)
            ?? throw new InvalidOperationException("no writer heartbeat visible, so no writer to check");
        if (!await WriterAnswersAsync(row.WriterOpsUrl, ct))
            throw new HttpRequestException($"the writer at {row.WriterOpsUrl} did not answer /healthz");
    }

    /// <summary>Measures every <paramref name="interval"/> until cancelled.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MeasureAsync(ct);
                await Task.Delay(interval, _time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> WriterAnswersAsync(string opsUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(opsUrl.TrimEnd('/') + "/healthz", UriKind.Absolute, out var healthz))
            return false;
        try
        {
            using var response = await probe.GetAsync(healthz, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreachable, refused or timed out: the writer is down as far as this reader can tell.
            return false;
        }
    }
}
