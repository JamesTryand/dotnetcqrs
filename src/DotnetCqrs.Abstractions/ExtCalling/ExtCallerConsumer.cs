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
    /// gateway — another dotnetcqrs instance or a pocketcqrs one.</summary>
    public required IFollowUpDispatcher Dispatcher { get; init; }

    /// <summary>Records permanent per-event failures. Point this at a store this
    /// component owns outright.</summary>
    public required IDeadLetterStore DeadLetters { get; init; }

    /// <summary>Where the consumer records the calls to make, for <see cref="ExtCallOutboxWorker"/>: the event
    /// stores implement it, beside their dead letters.</summary>
    public required IExtCallOutbox Outbox { get; init; }

    /// <summary>Bounds the attempts per event (a failed call, or a follow-up still conflicting after its immediate
    /// re-dispatches), <see cref="RetryPolicy.Backoff"/> apart. Defaults to exactly one attempt.</summary>
    public RetryPolicy Retry { get; init; } = new();

    /// <summary>The clock the worker schedules retries by.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

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
/// (an HTTP POST to a remote command gateway,
/// matching pocketcqrs's own <c>extcaller.Gateway</c>).
///
/// <para><see cref="ApplyAsync"/> makes no call: it records the event in the
/// <see cref="ExtCallerConfig.Outbox"/> and wakes <see cref="Worker"/>, which calls out, retries and dispatches outside
/// the engine. So one slow or failing integration never holds up the rest of the log, and a retry survives a restart.
/// Run the worker beside the engine (<c>_ = consumer.Worker.RunAsync(ct)</c>). A permanently failing rule dead-letters,
/// as before.</para>
///
/// Known gap, deliberately deferred (it would need <c>HandleWithMeta</c> to carry the key):
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

    public ExtCallerConsumer(ExtCallerConfig config)
    {
        _config = config;

        _rules = new Dictionary<string, Rule>();
        foreach (var rule in config.Rules)
        {
            if (string.IsNullOrEmpty(rule.EventType))
                throw new ArgumentException("extcaller: a Rule has an empty EventType");
            if (!_rules.TryAdd(rule.EventType, rule))
                throw new ArgumentException($"extcaller: more than one Rule claims event type '{rule.EventType}'");
        }
        Worker = new ExtCallOutboxWorker(config, Name, _rules, DeriveCommandId);
    }

    public string Name => $"extcall:{_config.Name}";

    /// <summary>Makes this consumer's calls: run it beside the engine.</summary>
    public ExtCallOutboxWorker Worker { get; }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (!_rules.ContainsKey(ev.Type)) return;
        await _config.Outbox.EnqueueAsync(Name, ev, _config.Time.GetUtcNow(), ct);
        Worker.Nudge();
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
