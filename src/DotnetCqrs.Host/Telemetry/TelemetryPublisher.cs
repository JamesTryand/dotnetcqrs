namespace DotnetCqrs.Host.Telemetry;

/// <summary>
/// The optional telemetry push (health/telemetry contract section 8; <c>STATE-MACHINES.md</c>,
/// machine 5): a snapshot as soon as the node has an identity, then every
/// <see cref="TelemetrySettings.Interval"/>, and once more when the node begins draining, so a
/// monitor sees <c>not_ready</c>/<c>draining</c> rather than a node that went quiet.
///
/// <para><b>Best-effort, and never a dependency.</b> A snapshot that cannot be sent within
/// <see cref="PublishTimeout"/> is dropped: never retried, never queued while the bus is away. A
/// failure is logged once when it starts and once when it ends, and changes nothing else: it never
/// reaches <c>/healthz</c> or <c>/readyz</c>, and never slows a command or an event, since it runs
/// on its own loop and only reads.</para>
/// </summary>
public sealed class TelemetryPublisher : IAsyncDisposable
{
    /// <summary>Contract section 8.5: a publish not completed within a second is dropped.</summary>
    public static readonly TimeSpan DefaultPublishTimeout = TimeSpan.FromSeconds(1);

    private readonly NodeHealth _health;
    private readonly ITelemetryTransport _transport;
    private readonly TimeSpan _interval;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private Task? _final;
    private bool _failing;

    public TelemetryPublisher(
        NodeHealth health, ITelemetryTransport transport, TimeSpan interval, Action<string>? log = null,
        TimeSpan? publishTimeout = null, TimeProvider? timeProvider = null)
    {
        _health = health;
        _transport = transport;
        _interval = interval;
        _log = log ?? (_ => { });
        PublishTimeout = publishTimeout ?? DefaultPublishTimeout;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan PublishTimeout { get; }

    /// <summary>Snapshots published so far, and dropped so far (for tests and for logs).</summary>
    public long Published { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>Starts the loop in the background. Returns at once.</summary>
    public void Start()
    {
        _loop ??= Task.Run(RunAsync);
    }

    /// <summary>The node has begun draining: publish one more snapshot now (its readiness already
    /// says draining), without waiting for the interval. Returns at once; <see cref="StopAsync"/>
    /// waits for it.</summary>
    public void NotifyDraining()
    {
        _final ??= Task.Run(() => PublishOnceAsync(CancellationToken.None));
    }

    /// <summary>Stops the loop, waits for a final snapshot still going out (bounded by
    /// <see cref="PublishTimeout"/>), and releases the transport.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is { } loop)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }
        if (_final is { } final)
            await final;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _transport.DisposeAsync();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            // The key is the node id, known once identity has resolved during boot.
            while (_health.Identity is null)
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time, ct);
            while (!ct.IsCancellationRequested)
            {
                await PublishOnceAsync(ct);
                await Task.Delay(_interval, _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Builds and sends one snapshot, or drops it. Never throws.</summary>
    private async Task PublishOnceAsync(CancellationToken stop)
    {
        if (_health.Identity is not { } identity)
            return;
        await _one.WaitAsync(stop);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(PublishTimeout);
            var snapshot = await _health.Metrics.SnapshotAsync(_health, timeout.Token);
            var payload = TelemetryPayload.Serialize(snapshot, identity.NodeId, identity.Role, _time.GetUtcNow());
            await _transport.PublishAsync(identity.NodeId, payload, timeout.Token);
            Published++;
            if (_failing)
            {
                _failing = false;
                _log("telemetry: publishing resumed");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Dropped++;
            if (!_failing)
            {
                _failing = true;
                _log($"telemetry: snapshot dropped, the bus is unreachable ({Describe(ex)}); further drops are silent until it recovers");
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private static string Describe(Exception ex) =>
        ex is OperationCanceledException ? "timed out" : ex.GetType().Name + ": " + ex.Message;
}
