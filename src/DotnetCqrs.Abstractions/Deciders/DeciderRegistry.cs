using System.Runtime.CompilerServices;
using System.Text.Json;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Deciders;

/// <summary>Thrown when no decider is registered for an aggregate name.</summary>
public sealed class UnknownAggregateException(string aggregate)
    : Exception($"no decider registered for aggregate '{aggregate}'")
{
    public string Aggregate { get; } = aggregate;
}

/// <summary>Maps aggregate names to their deciders and executes commands against an
/// <see cref="IEventStore"/> (SQLite today, Postgres under Milestone 7 — this type
/// no longer names a provider).</summary>
public sealed class DeciderRegistry(IEventStore store)
{
    private readonly Dictionary<string, ErasedDecider> _deciders = [];

    /// <summary>Used to serialise a <see cref="NewEvent.Payload"/> into
    /// <see cref="NewEvent.Data"/> at append time. Defaults to camelCase property names,
    /// matching what generated deciders have always written by hand.</summary>
    public JsonSerializerOptions PayloadSerializerOptions { get; set; } =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Adds a decider for an aggregate name.</summary>
    public void Register<TState>(string aggregate, Decider<TState> decider) =>
        Register(aggregate, decider, protector: null);

    /// <summary>Adds a decider together with its <see cref="IPiiProtector"/>. A separate
    /// overload rather than an optional parameter so the two-argument form stays
    /// binary- and reflection-compatible (the verify harness resolves it by arity).</summary>
    public void Register<TState>(string aggregate, Decider<TState> decider, IPiiProtector? protector)
    {
        _deciders[aggregate] = new ErasedDecider(
            Initial: () => decider.InitialState()!,
            Decide: (state, command) => decider.Decide((TState)state, command),
            Evolve: (state, ev) => decider.Evolve((TState)state, ev)!,
            Protector: protector);
    }

    /// <summary>
    /// Loads the stream, folds it into state, decides the command and appends the
    /// resulting events with optimistic concurrency. Throws whatever <see cref="Decider{TState}.Decide"/>
    /// throws to reject the command, or <see cref="ConcurrencyException"/> if the
    /// stream changed between load and append. <see cref="IsRejection"/> tells a
    /// rejection apart from a failure around the decision.
    /// </summary>
    public Task<IReadOnlyList<Event>> HandleAsync(
        string aggregate, string aggregateId, Command command, CancellationToken ct = default)
        => HandleWithMetaAsync(aggregate, aggregateId, command, null, ct);

    /// <summary>
    /// <see cref="HandleAsync"/> with caller-supplied metadata (e.g. the authenticated
    /// actor, or a reactor/extcaller's causationId/correlationId) merged into every
    /// appended event's metadata — caller-supplied keys win over decider-supplied ones.
    /// Stamps "now" (UTC, ISO 8601) into meta if absent before deciding, and fills
    /// <see cref="Command.Actor"/>/<see cref="Command.Now"/>/<see cref="Command.Provenance"/>
    /// from the resolved meta before <c>Decide</c> sees it.
    /// </summary>
    public async Task<IReadOnlyList<Event>> HandleWithMetaAsync(
        string aggregate, string aggregateId, Command command, IReadOnlyDictionary<string, object>? meta, CancellationToken ct = default)
    {
        if (!_deciders.TryGetValue(aggregate, out var decider))
            throw new UnknownAggregateException(aggregate);

        var resolvedMeta = meta is null ? new Dictionary<string, object>() : new Dictionary<string, object>(meta);
        if (!resolvedMeta.ContainsKey("now"))
            resolvedMeta["now"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var cmd = command with
        {
            Actor = MetaString(resolvedMeta, "actor") ?? command.Actor,
            Now = MetaString(resolvedMeta, "now") ?? command.Now,
            Provenance = MetaString(resolvedMeta, "provenance") ?? command.Provenance,
        };

        var stream = await store.LoadStreamAsync(aggregate, aggregateId, ct);

        var state = decider.Initial();
        foreach (var ev in stream)
            state = decider.Evolve(state, ev);

        IReadOnlyList<NewEvent> newEvents;
        try
        {
            newEvents = Decide(decider, state, cmd);
        }
        catch (RevealRequiredException) when (decider.Protector is not null)
        {
            // The decision read protected state that is still ciphertext. Reveal it (one
            // batched round trip) and decide again -- Decide is pure, so re-running it is
            // free of side effects. A decision that never reads protected state never
            // lands here and pays nothing.
            state = await decider.Protector.RevealAsync(state, ct);
            newEvents = Decide(decider, state, cmd);
        }
        if (newEvents.Count == 0) return [];

        if (decider.Protector is not null)
            newEvents = await decider.Protector.ProtectAsync(aggregateId, newEvents, ct);

        var withMeta = newEvents
            .Select(ne => ne with { Data = Materialize(ne), Payload = null, Metadata = MergeMeta(ne.Metadata, resolvedMeta) })
            .ToList();

        return await store.AppendAsync(aggregate, aggregateId, stream.Count, withMeta, ct);
    }

    /// <summary>True when <paramref name="ex"/> was thrown by a decider's <c>Decide</c> during
    /// <see cref="HandleWithMetaAsync"/>: the command was refused on its merits. False for
    /// anything thrown around it -- loading the stream, revealing or protecting PII,
    /// serialising, appending -- which means the command was never decided. The shell uses
    /// this to keep "rejected" apart from infrastructure failures and bugs (gateway 400 vs
    /// 5xx; a reactor drops a rejection but retries anything else).
    ///
    /// <para>Rejection is defined by where the exception came from, not its type: Decide
    /// may reject by throwing whatever it likes, so no type could say this. Consequence: a
    /// bug inside Decide itself (a null reference, a bad cast) also counts as a rejection.
    /// <see cref="RevealRequiredException"/> is never one -- it is the registry's reveal
    /// signal, and escaping means no protector was registered to act on it.</para></summary>
    public static bool IsRejection(Exception ex) => Rejections.TryGetValue(ex, out _);

    // Keyed by the exception object itself and held weakly: nothing is added to the
    // exception, nothing leaks, and the mark is invisible to anyone not asking.
    private static readonly ConditionalWeakTable<Exception, object> Rejections = new();

    /// <summary>Calls Decide unchanged. The filter marks whatever escapes it and returns
    /// false, so the exception is never caught: it propagates as the same object with its
    /// original stack trace, and a decision that doesn't throw pays nothing.</summary>
    private static IReadOnlyList<NewEvent> Decide(ErasedDecider decider, object state, Command cmd)
    {
        try
        {
            return decider.Decide(state, cmd);
        }
        catch (Exception ex) when (MarkRejection(ex))
        {
            throw; // unreachable: MarkRejection always returns false
        }
    }

    private static bool MarkRejection(Exception ex)
    {
        if (ex is not RevealRequiredException)
            Rejections.AddOrUpdate(ex, Rejections);
        return false;
    }

    private string Materialize(NewEvent ne) =>
        ne.Payload is null ? ne.Data : JsonSerializer.Serialize(ne.Payload, ne.Payload.GetType(), PayloadSerializerOptions);

    private static string? MetaString(IReadOnlyDictionary<string, object> meta, string key) =>
        meta.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>Overlays <paramref name="extra"/> onto the event's existing metadata:
    /// existing keys are kept unless also present in <paramref name="extra"/>, in which
    /// case <paramref name="extra"/> wins.</summary>
    private static string MergeMeta(string existingMetadataJson, IReadOnlyDictionary<string, object> extra)
    {
        var merged = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(existingMetadataJson) && existingMetadataJson != "{}")
        {
            var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingMetadataJson);
            if (existing is not null)
                foreach (var (key, value) in existing)
                    merged[key] = value;
        }
        foreach (var (key, value) in extra)
            merged[key] = value;
        return JsonSerializer.Serialize(merged);
    }

    private sealed record ErasedDecider(
        Func<object> Initial,
        Func<object, Command, IReadOnlyList<NewEvent>> Decide,
        Func<object, Event, object> Evolve,
        IPiiProtector? Protector);
}
