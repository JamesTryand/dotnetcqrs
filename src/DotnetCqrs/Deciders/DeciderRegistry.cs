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
    public async Task<IReadOnlyList<Event>> HandleAsync(
        string aggregate, string aggregateId, Command command, CancellationToken ct = default)
    {
        if (!_deciders.TryGetValue(aggregate, out var decider))
            throw new UnknownAggregateException(aggregate);

        var stream = await store.LoadStreamAsync(aggregate, aggregateId, ct);

        var state = decider.Initial();
        foreach (var ev in stream)
            state = decider.Evolve(state, ev);

        var newEvents = decider.Decide(state, command);
        if (newEvents.Count == 0) return [];

        return await store.AppendAsync(aggregate, aggregateId, stream.Count, newEvents, ct);
    }

    private sealed record ErasedDecider(
        Func<object> Initial,
        Func<object, Command, IReadOnlyList<NewEvent>> Decide,
        Func<object, Event, object> Evolve);
}
