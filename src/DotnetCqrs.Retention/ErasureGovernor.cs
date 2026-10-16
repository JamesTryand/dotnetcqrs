using System.Globalization;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Outbox;

namespace DotnetCqrs.Retention;

/// <summary>Drives an erasure request to a conclusion. A consumer on the subject's own stream:
/// <list type="bullet">
/// <item><c>ErasureRequested</c> (and <c>LegalHoldReleased</c>): ask the <see cref="IRetentionPolicy"/>, then dispatch
/// <c>ApproveErasure</c> or <c>HoldErasure</c>. A legal hold overrides the policy.</item>
/// <item><c>ErasureApproved</c>: dispatch <c>EraseSubject</c>, which <see cref="SubjectKeyDestroyer"/> then turns into
/// a destroyed key.</item>
/// <item><c>ErasureHeld</c> with a review date: record a review in the outbox, due on that date. <see cref="Reviews"/>
/// asks the policy again then, so a held request does not wait forever. The due date itself, never a timer looking
/// for work.</item>
/// </list>
/// Everything it dispatches is idempotent, so the consumer engine's at-least-once delivery is safe, and every command
/// is attributed to <see cref="SystemActor"/>. It reads the subject's own stream to decide, never a read model.</summary>
public sealed class ErasureGovernor : IConsumer
{
    /// <summary>The governor's durable checkpoint name.</summary>
    public const string ConsumerName = "retention:governor";

    /// <summary>The outbox consumer a held request's review is recorded under.</summary>
    public const string ReviewConsumer = "retention:review";

    /// <summary>The actor recorded on everything the governor decides.</summary>
    public const string SystemActor = "system:retention-policy";

    private readonly DeciderRegistry _registry;
    private readonly IEventStore _events;
    private readonly IRetentionPolicy _policy;
    private readonly IOutbox? _outbox;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;

    /// <param name="reviews">Where held requests' reviews are recorded. Null reviews none: a held request then waits
    /// for a new request or a legal hold's release.</param>
    public ErasureGovernor(DeciderRegistry registry, IEventStore events, IRetentionPolicy policy,
        IOutbox? reviews = null, TimeProvider? time = null, Action<string>? log = null)
    {
        _registry = registry;
        _events = events;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy), "a governor needs a retention policy; a system with nothing to retain uses AllowAllRetentionPolicy");
        _outbox = reviews;
        _time = time ?? TimeProvider.System;
        _log = log;
        if (reviews is not null) Reviews = new OutboxWorker(reviews, ReviewConsumer, ReviewAsync, _time, log);
    }

    /// <summary>The worker that reviews held requests on their date, or null with no outbox. Run it
    /// (<see cref="OutboxWorker.RunAsync"/>) on the writer for as long as the host runs.</summary>
    public OutboxWorker? Reviews { get; }

    public string Name => ConsumerName;

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Aggregate != DataSubject.Aggregate) return;
        switch (ev.Type)
        {
            case DataSubject.ErasureRequestedEvent:
            case GovernedSubject.LegalHoldReleasedEvent:
                await EvaluateAsync(ev.AggregateId, ct).ConfigureAwait(false);
                break;
            case GovernedSubject.ErasureApprovedEvent:
                await DispatchAsync(ev.AggregateId, DataSubject.EraseSubjectCommand, "{}", ct).ConfigureAwait(false);
                break;
            case GovernedSubject.ErasureHeldEvent when _outbox is not null:
                if (ReviewDate(ev.Data) is not { } review) break;
                var key = $"{ev.AggregateId}|{review.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";
                await _outbox.EnqueueAsync(ReviewConsumer, key, JsonSerializer.Serialize(new { subject = ev.AggregateId }), review, ct).ConfigureAwait(false);
                Reviews!.Nudge();
                break;
        }
    }

    private static DateTimeOffset? ReviewDate(string data)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
        return doc.RootElement.TryGetProperty("reviewAt", out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var at)
            ? at : null;
    }

    /// <summary>Asks the policy again for a held request whose review date has come (the outbox handler). A failure is
    /// tried again by the worker after a growing wait.</summary>
    private async Task<OutboxOutcome> ReviewAsync(OutboxEntry entry, CancellationToken ct)
    {
        string? subject = null;
        try
        {
            using var doc = JsonDocument.Parse(entry.Payload);
            if (doc.RootElement.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String) subject = s.GetString();
        }
        catch (JsonException) { }
        if (string.IsNullOrEmpty(subject))
        {
            _log?.Invoke($"erasure review {entry.Key} unreadable, dropped");
            return OutboxOutcome.Done;
        }
        await EvaluateAsync(subject, ct).ConfigureAwait(false);
        return OutboxOutcome.Done;
    }

    /// <summary>Rules on one subject's open request: nothing happens if there is none.</summary>
    public async Task EvaluateAsync(string subjectId, CancellationToken ct)
    {
        var state = await LoadAsync(subjectId, ct).ConfigureAwait(false);
        if (state.Erased || state.Stage is not (ErasureStage.Requested or ErasureStage.Held)) return;

        if (state.LegalHold)
        {
            // A legal hold overrides any policy. The request waits (with no review date: releasing the
            // hold is what re-opens it).
            if (state.Stage == ErasureStage.Requested)
                await DispatchAsync(subjectId, GovernedSubject.HoldErasureCommand, Payload("legal-hold", null), ct).ConfigureAwait(false);
            return;
        }

        var decision = await _policy.EvaluateAsync(subjectId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (decision.Allowed)
            await DispatchAsync(subjectId, GovernedSubject.ApproveErasureCommand, "{}", ct).ConfigureAwait(false);
        else
            await DispatchAsync(subjectId, GovernedSubject.HoldErasureCommand, Payload(decision.Reason ?? "retention", decision.ReviewAt), ct).ConfigureAwait(false);
    }

    private async Task<GovernedSubjectState> LoadAsync(string subjectId, CancellationToken ct)
    {
        var decider = GovernedSubject.Create();
        var stream = await _events.LoadStreamAsync(DataSubject.Aggregate, subjectId, ct).ConfigureAwait(false);
        return stream.Aggregate(decider.InitialState(), (state, ev) => decider.Evolve(state, ev));
    }

    private static string Payload(string reason, DateTimeOffset? reviewAt) =>
        JsonSerializer.Serialize(new { reason, reviewAt }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private async Task DispatchAsync(string subjectId, string command, string payload, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["actor"] = SystemActor,
            ["now"] = _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        try
        {
            await _registry.HandleWithMetaAsync(DataSubject.Aggregate, subjectId, new Command(command, payload), meta, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (DeciderRegistry.IsRejection(ex))
        {
            // The subject moved on between reading it and deciding (erased, held, a hold placed): the
            // current state wins. Retrying would fail the same way, so this is not a delivery failure.
            _log?.Invoke($"{command} for subject '{subjectId}' not applied: {ex.Message}");
        }
    }
}

/// <summary>Wiring for governed erasure on a host.</summary>
public static class ErasureRetention
{
    /// <summary>Governs erasure on the writer: registers the governed <c>dataSubject</c> decider in place of the core
    /// one (<see cref="DataSubject.RegisterDataSubjects"/> keeps it, whichever is called first) and the governor. Run
    /// the returned governor's <see cref="ErasureGovernor.Reviews"/> worker for as long as the host runs. The host still
    /// registers <see cref="SubjectKeyDestroyer"/> (it is what erases), and its gateway authorization must cover every
    /// command in <see cref="GovernedSubject.Commands"/>.</summary>
    public static ErasureGovernor RegisterErasureRetention(
        this ConsumerEngine engine, DeciderRegistry registry, IEventStore events, IRetentionPolicy policy,
        IOutbox? reviews, TimeProvider? time = null, Action<string>? log = null)
    {
        var governor = new ErasureGovernor(registry, events, policy, reviews, time, log);
        registry.RegisterGovernedDataSubjects();
        engine.Register(governor);
        return governor;
    }
}
