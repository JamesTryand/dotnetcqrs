using System.Security.Cryptography;
using System.Text;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders; // referenced only from doc-comment crefs now
using DotnetCqrs.EventStore;

namespace DotnetCqrs.ExtCalling;

/// <summary>Configures one <see cref="ExtCallerConsumer"/>.</summary>
public sealed class ExtCallerConfig
{
    /// <summary>Identifies this consumer instance: its durable checkpoint key is
    /// "extcall:&lt;Name&gt;" and its dead letters are recorded under the same string.</summary>
    public required string Name { get; init; }

    /// <summary>Matched by event type; at most one <see cref="Rule"/> may claim a given
    /// <see cref="Rule.EventType"/>.</summary>
    public required IReadOnlyList<Rule> Rules { get; init; }

    /// <summary>Makes the outbound HTTP call.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>Applies the follow-up commands a <see cref="Rule"/> produces. Use
    /// <see cref="InProcessFollowUpDispatcher"/> to keep dispatching straight into a
    /// local <see cref="Deciders.DeciderRegistry"/> (this baseline's original mode), or
    /// <see cref="GatewayFollowUpDispatcher"/> to POST them to a configured command
    /// gateway — another dotnetcqrs instance or a pocketcqrs one
    /// (dotnetcqrs-multi-node Milestone 4).</summary>
    public required IFollowUpDispatcher Dispatcher { get; init; }

    /// <summary>Records permanent per-event failures. Point this at a store this
    /// component owns outright.</summary>
    public required SqliteEventStore DeadLetters { get; init; }

    /// <summary>Bounds the outbound call attempts. Defaults to exactly one attempt.</summary>
    public RetryPolicy Retry { get; init; } = new();

    /// <summary>Receives operational log lines; null is a valid no-op.</summary>
    public Action<string>? Logger { get; init; }
}

/// <summary>
/// A <see cref="IConsumer"/> that matches committed events against configured
/// <see cref="Rule"/>s, calls a third-party REST API, and dispatches the response as a
/// follow-up command through an <see cref="IFollowUpDispatcher"/> — never appends a raw
/// event, so the target decider keeps authority to accept or reject the result. Porting
/// pocketcqrs's <c>extcaller</c> package. The dispatcher is either
/// <see cref="InProcessFollowUpDispatcher"/> (this baseline's original mode, straight
/// into a local <see cref="DeciderRegistry"/>) or <see cref="GatewayFollowUpDispatcher"/>
/// (dotnetcqrs-multi-node Milestone 4 — an HTTP POST to a remote command gateway,
/// matching pocketcqrs's own <c>extcaller.Gateway</c>).
///
/// <see cref="ApplyAsync"/> never throws: a permanently failing rule dead-letters and
/// the checkpoint still advances, so one stuck integration can never block the rest of
/// the log — matching pocketcqrs's own design point exactly (see its package doc
/// comment).
///
/// Known gap, deliberately deferred (see Milestone 5's scope note on <c>HandleWithMeta</c>):
/// pocketcqrs additionally derives a stable idempotency key per follow-up and has its
/// remote gateway de-duplicate by it, so a redelivered source event's follow-up never
/// even reaches the target decider a second time. This baseline has no such dedup index
/// yet (no <c>CommandApplied</c> equivalent), so a redelivered follow-up here relies on
/// the target decider's own rejection for idempotency, same as <see cref="Reactors.ReactorConsumer"/>
/// — and unlike a reactor, that rejection currently dead-letters the (otherwise
/// harmless, already-applied) redelivery rather than being recognized as expected. The
/// derived commandId is still stamped into dispatch metadata below so a future dedup
/// index has something to key off — and <see cref="GatewayFollowUpDispatcher"/> sends
/// it as an <c>Idempotency-Key</c> header, which a pocketcqrs target already honours.
/// </summary>
public sealed class ExtCallerConsumer : IConsumer
{
    private readonly ExtCallerConfig _config;
    private readonly Dictionary<string, Rule> _rules;
    private readonly Action<string> _log;

    public ExtCallerConsumer(ExtCallerConfig config)
    {
        _config = config;
        _log = config.Logger ?? (_ => { });

        _rules = new Dictionary<string, Rule>();
        foreach (var rule in config.Rules)
        {
            if (string.IsNullOrEmpty(rule.EventType))
                throw new ArgumentException("extcaller: a Rule has an empty EventType");
            if (!_rules.TryAdd(rule.EventType, rule))
                throw new ArgumentException($"extcaller: more than one Rule claims event type '{rule.EventType}'");
        }
    }

    public string Name => $"extcall:{_config.Name}";

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (!_rules.TryGetValue(ev.Type, out var rule)) return;

        HttpResponseMessage response;
        try
        {
            response = await CallWithRetryAsync(rule, ev, ct);
        }
        catch (Exception ex)
        {
            await DeadLetterAsync(ev, $"calling out: {ex.Message}", ct);
            return;
        }

        try
        {
            IReadOnlyList<FollowUp> followUps;
            try
            {
                followUps = await rule.HandleResponse(ev, response);
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(ev, $"handling response: {ex.Message}", ct);
                return;
            }

            for (var i = 0; i < followUps.Count; i++)
            {
                var followUp = followUps[i];
                var dispatch = new FollowUpDispatch(
                    followUp.Aggregate, followUp.Id, followUp.Name, followUp.Payload,
                    Actor: Name,
                    CausationId: ev.Id,
                    CorrelationId: EventMeta.CorrelationId(ev),
                    CommandId: DeriveCommandId(ev.Id, i));
                try
                {
                    await _config.Dispatcher.DispatchAsync(dispatch, ct);
                    _log($"follow-up dispatched: consumer={Name} event={ev.Id} " +
                         $"command={followUp.Name} target={followUp.Aggregate}/{followUp.Id}");
                }
                catch (Exception ex)
                {
                    // A follow-up dispatch failure -- a domain rejection or concurrency
                    // conflict from the target decider, or (remote dispatch, Milestone 4)
                    // the target gateway unreachable or answering non-2xx -- dead-letters
                    // the SOURCE event rather than retrying just this follow-up:
                    // RetryPolicy bounds the outbound third-party call, not the dispatch,
                    // and partial application (an earlier follow-up in this batch already
                    // succeeded) needs operator visibility, not silent papering-over.
                    await DeadLetterAsync(ev,
                        $"dispatching follow-up {i + 1}/{followUps.Count} ({followUp.Name} {followUp.Aggregate}/{followUp.Id}): {ex.Message}", ct);
                    return;
                }
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    // BuildRequest is called fresh on every attempt (rather than caching one
    // HttpRequestMessage across retries): an HttpRequestMessage cannot be sent more
    // than once, and cloning one safely (in particular its Content) is not worth the
    // complexity for what is, by default, a single-attempt call.
    private async Task<HttpResponseMessage> CallWithRetryAsync(Rule rule, Event ev, CancellationToken ct)
    {
        var maxAttempts = Math.Max(_config.Retry.MaxAttempts, 1);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = rule.BuildRequest(ev);
                var response = await _config.Http.SendAsync(request, ct);
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
                return response;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == maxAttempts) break;
                if (_config.Retry.Backoff > TimeSpan.Zero)
                    await Task.Delay(_config.Retry.Backoff, ct);
            }
        }
        throw lastError!;
    }

    private async Task DeadLetterAsync(Event ev, string error, CancellationToken ct)
    {
        _log($"event delivery failed, dead-lettered: consumer={Name} event={ev.Id} position={ev.Position} error={error}");
        await _config.DeadLetters.AddDeadLetterAsync(Name, ev, error, ct);
    }

    // Deterministic by construction: the same cause redelivered derives the same id.
    // Hashed rather than concatenated so a consumer name containing the separator
    // cannot collide with a different one -- mirrors pocketcqrs's reactionCommandID.
    private string DeriveCommandId(string causeId, int index)
    {
        using var sha256 = SHA256.Create();
        var input = string.Join('\0', "extcall", Name, causeId, index.ToString());
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return "extcall-" + Convert.ToHexStringLower(hash)[..32];
    }
}
