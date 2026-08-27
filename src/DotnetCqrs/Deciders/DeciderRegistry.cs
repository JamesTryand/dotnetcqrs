using System.Text.Json;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Deciders;

/// <summary>Thrown when no decider is registered for an aggregate name.</summary>
public sealed class UnknownAggregateException(string aggregate)
    : Exception($"no decider registered for aggregate '{aggregate}'")
{
    public string Aggregate { get; } = aggregate;
}

/// <summary>Maps aggregate names to their deciders and executes commands against a
/// <see cref="SqliteEventStore"/>.</summary>
public sealed class DeciderRegistry(SqliteEventStore store)
{
    private readonly Dictionary<string, ErasedDecider> _deciders = [];

    /// <summary>Adds a decider for an aggregate name.</summary>
    public void Register<TState>(string aggregate, Decider<TState> decider)
    {
        _deciders[aggregate] = new ErasedDecider(
            Initial: () => decider.InitialState()!,
            Decide: (state, command) => decider.Decide((TState)state, command),
            Evolve: (state, ev) => decider.Evolve((TState)state, ev)!);
    }

    /// <summary>
    /// Loads the stream, folds it into state, decides the command and appends the
    /// resulting events with optimistic concurrency. Throws whatever <see cref="Decider{TState}.Decide"/>
    /// throws to reject the command, or <see cref="ConcurrencyException"/> if the
    /// stream changed between load and append.
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

        var newEvents = decider.Decide(state, cmd);
        if (newEvents.Count == 0) return [];

        var withMeta = newEvents.Select(ne => ne with { Metadata = MergeMeta(ne.Metadata, resolvedMeta) }).ToList();

        return await store.AppendAsync(aggregate, aggregateId, stream.Count, withMeta, ct);
    }

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
        Func<object, Event, object> Evolve);
}
