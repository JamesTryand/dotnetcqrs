namespace DotnetCqrs.Outbox;

/// <summary>Does one entry's work. Return <see cref="OutboxOutcome.Done"/> or <see cref="OutboxOutcome.Retry"/>; an
/// exception is a failed attempt, tried again after a growing wait.</summary>
public delegate Task<OutboxOutcome> OutboxHandler(OutboxEntry entry, CancellationToken ct);

/// <summary>
/// Does one consumer's outbox work as it falls due. <see cref="RunAsync"/> does what is due, then waits until it is
/// woken (<see cref="Nudge"/>, called when work is recorded) or the next entry falls due. That wait is the clock, never
/// a poll for new work. One worker per consumer (a single writer); <see cref="ProcessDueAsync"/> is one pass, for
/// tests.
/// </summary>
public sealed class OutboxWorker(IOutbox outbox, string consumer, OutboxHandler handler, TimeProvider? time = null, Action<string>? log = null)
{
    /// <summary>The longest single wait. A timer accepts at most about 49 days, and work can be due years ahead, so a
    /// longer wait is taken in steps of this.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    private const int Batch = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Action<string> _log = log ?? (_ => { });
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>The consumer whose entries this worker does.</summary>
    public string Consumer => consumer;

    /// <summary>Wakes <see cref="RunAsync"/>: work has been recorded.</summary>
    public void Nudge()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already woken
    }

    /// <summary>Does what is due, then waits to be woken or for the next entry, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? next = null;
            try
            {
                while (await ProcessDueAsync(ct).ConfigureAwait(false) > 0) { }
                next = await outbox.NextDueAsync(consumer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log($"outbox pass failed: consumer={consumer} error={ex.Message}"); }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var waits = new List<Task> { _wake.WaitAsync(wait.Token) };
            if (next is { } due) // nothing recorded: wait only to be woken
            {
                var delay = due - _time.GetUtcNow();
                if (delay > MaxWait) delay = MaxWait;
                waits.Add(Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _time, wait.Token));
            }
            try { await Task.WhenAny(waits).ConfigureAwait(false); }
            finally { wait.Cancel(); }
        }
    }

    /// <summary>One pass over the entries due now. Returns how many it handled.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var due = await outbox.DueAsync(consumer, _time.GetUtcNow(), Batch, ct).ConfigureAwait(false);
        foreach (var entry in due)
        {
            OutboxOutcome outcome;
            try
            {
                outcome = await handler(entry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 1s, 2s, 4s ... at most an hour; never dropped
                var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(entry.Attempts, 12)), 3600));
                outcome = OutboxOutcome.Retry(_time.GetUtcNow() + wait, ex.Message);
            }
            if (outcome.IsDone)
            {
                await outbox.RemoveAsync(consumer, entry.Key, ct).ConfigureAwait(false);
                continue;
            }
            if (outcome.Error is not null) _log($"outbox entry not done: consumer={consumer} key={entry.Key} error={outcome.Error}");
            await outbox.RetryAsync(consumer, entry.Key, entry.Attempts + 1, outcome.RetryAt, ct).ConfigureAwait(false);
        }
        return due.Count;
    }
}
