using System.Globalization;
using System.Text.Json;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.ExtCalling;

/// <summary>
/// Makes the external calls <see cref="ExtCallerConsumer"/> records in its outbox, outside the consumer engine (D40,
/// timesheets' paradigm audit R4: the consumer used to call out, and sleep between retries, inside the engine, holding
/// up every consumer after it). For each due entry:
/// <list type="bullet">
/// <item>It makes the call, once. A failed call is tried again after <see cref="RetryPolicy.Backoff"/>, and
/// dead-lettered once <see cref="RetryPolicy.MaxAttempts"/> are used.</item>
/// <item>A successful response's follow-ups are kept on the entry before any is dispatched, so retrying a dispatch never
/// calls the outside service again. They are dispatched in order, and the entry records each one done.</item>
/// <item>A follow-up refused for a concurrency conflict (another write landed on its target stream) is dispatched again
/// at once, up to three times, since its decider decides afresh against the stream as it now is; still conflicting, it
/// is an attempt like a failed call. Any other refusal dead-letters the source event, as before.</item>
/// </list>
/// <see cref="RunAsync"/> processes what is due, then waits until it is woken (<see cref="Nudge"/>, called as an entry is
/// recorded) or the next retry falls due. That wait is a back-off timer for a failing outside service, never a wait
/// for data to appear. One worker per consumer name (a single writer); <see cref="ProcessDueAsync"/> is one pass, for
/// tests.
/// </summary>
public sealed class ExtCallOutboxWorker
{
    /// <summary>How many times a follow-up meeting a concurrency conflict is dispatched at once before it counts as a
    /// failed attempt.</summary>
    public const int ConflictRetries = 3;

    private const int Batch = 100;

    private readonly ExtCallerConfig _config;
    private readonly string _consumer;
    private readonly IReadOnlyDictionary<string, Rule> _rules;
    private readonly Func<string, int, string> _commandId;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);

    internal ExtCallOutboxWorker(ExtCallerConfig config, string consumer, IReadOnlyDictionary<string, Rule> rules, Func<string, int, string> commandId)
    {
        _config = config;
        _consumer = consumer;
        _rules = rules;
        _commandId = commandId;
        _log = config.Logger ?? (_ => { });
    }

    private TimeProvider Time => _config.Time;

    /// <summary>Wakes <see cref="RunAsync"/>: an entry has been recorded.</summary>
    public void Nudge()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already woken
    }

    /// <summary>Processes what is due, then waits to be woken or for the next retry, until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (await ProcessDueAsync(ct) > 0) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log($"extcall outbox pass failed: consumer={_consumer} error={ex.Message}"); }

            var next = await _config.Outbox.NextDueAsync(_consumer, ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var woken = _wake.WaitAsync(wait.Token);
            var waits = new List<Task> { woken };
            if (next is { } due)
            {
                var delay = due - Time.GetUtcNow();
                waits.Add(Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Time, wait.Token));
            }
            try { await Task.WhenAny(waits); }
            finally { wait.Cancel(); }
            if (ct.IsCancellationRequested) return;
        }
    }

    /// <summary>One pass over the entries due now. Returns how many it processed.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var due = await _config.Outbox.DueAsync(_consumer, Time.GetUtcNow(), Batch, ct);
        foreach (var entry in due) await ProcessAsync(entry, ct);
        return due.Count;
    }

    private async Task ProcessAsync(ExtCallOutboxEntry entry, CancellationToken ct)
    {
        var ev = entry.Event;
        if (!_rules.TryGetValue(ev.Type, out var rule))
        {
            // The rule is gone from the configuration: nothing is to be called for it any more.
            await _config.Outbox.RemoveAsync(entry.Id, ct);
            return;
        }

        var followUpsJson = entry.FollowUps;
        if (followUpsJson is null)
        {
            HttpResponseMessage response;
            try
            {
                using var request = rule.BuildRequest(ev);
                response = await _config.Http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"answered {status}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await FailedAttemptAsync(entry, null, 0, $"calling out: {ex.Message}", ct);
                return;
            }

            try
            {
                followUpsJson = JsonSerializer.Serialize(await rule.HandleResponse(ev, response));
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(entry, $"handling response: {ex.Message}", ct);
                return;
            }
            finally
            {
                response.Dispose();
            }
            await _config.Outbox.UpdateAsync(entry.Id, entry.Attempts, entry.NextAttemptAt, followUpsJson, 0, ct);
        }

        var followUps = JsonSerializer.Deserialize<List<FollowUp>>(followUpsJson)!;
        for (var i = entry.Dispatched; i < followUps.Count; i++)
        {
            var followUp = followUps[i];
            var dispatch = new FollowUpDispatch(
                followUp.Aggregate, followUp.Id, followUp.Name, followUp.Payload,
                Actor: _consumer,
                CausationId: ev.Id,
                CorrelationId: EventMeta.CorrelationId(ev),
                CommandId: _commandId(ev.Id, i));
            var where = $"follow-up {i + 1}/{followUps.Count} ({followUp.Name} {followUp.Aggregate}/{followUp.Id})";
            for (var conflicts = 0; ; conflicts++)
            {
                try
                {
                    await _config.Dispatcher.DispatchAsync(dispatch, ct);
                    _log($"follow-up dispatched: consumer={_consumer} event={ev.Id} command={followUp.Name} target={followUp.Aggregate}/{followUp.Id}");
                    break;
                }
                catch (Exception ex) when (IsConflict(ex) && conflicts + 1 < ConflictRetries)
                {
                    // Decided again against the stream as it now is: the conflict says only that another write landed.
                }
                catch (Exception ex) when (IsConflict(ex))
                {
                    await FailedAttemptAsync(entry, followUpsJson, i, $"dispatching {where}: {ex.Message}", ct);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // A refusal by the target decider, or the gateway unreachable: the source event is dead-lettered, as
                    // before (an earlier follow-up may have been applied, which needs an operator's eye).
                    await DeadLetterAsync(entry, $"dispatching {where}: {ex.Message}", ct);
                    return;
                }
            }
            await _config.Outbox.UpdateAsync(entry.Id, entry.Attempts, entry.NextAttemptAt, followUpsJson, i + 1, ct);
        }
        await _config.Outbox.RemoveAsync(entry.Id, ct);
    }

    private static bool IsConflict(Exception ex) =>
        ex is ConcurrencyException or GatewayDispatchException { StatusCode: 409 };

    private async Task FailedAttemptAsync(ExtCallOutboxEntry entry, string? followUps, int dispatched, string error, CancellationToken ct)
    {
        var attempts = entry.Attempts + 1;
        if (attempts >= Math.Max(_config.Retry.MaxAttempts, 1))
        {
            await DeadLetterAsync(entry, error, ct);
            return;
        }
        var next = Time.GetUtcNow() + _config.Retry.Backoff;
        _log($"extcall attempt {attempts} failed, next at {next.ToString("O", CultureInfo.InvariantCulture)}: consumer={_consumer} event={entry.Event.Id} error={error}");
        await _config.Outbox.UpdateAsync(entry.Id, attempts, next, followUps, dispatched, ct);
    }

    private async Task DeadLetterAsync(ExtCallOutboxEntry entry, string error, CancellationToken ct)
    {
        _log($"event delivery failed, dead-lettered: consumer={_consumer} event={entry.Event.Id} position={entry.Event.Position} error={error}");
        await _config.DeadLetters.AddDeadLetterAsync(_consumer, entry.Event, error, ct);
        await _config.Outbox.RemoveAsync(entry.Id, ct);
    }
}
