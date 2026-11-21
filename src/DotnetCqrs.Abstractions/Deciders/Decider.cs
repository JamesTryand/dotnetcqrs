using DotnetCqrs.EventStore;

namespace DotnetCqrs.Deciders;

/// <summary>
/// The write-side model of one aggregate type: a pure decision function with
/// no database access. <see cref="DeciderRegistry"/> replays a stream through
/// <see cref="Evolve"/> to fold state, then calls <see cref="Decide"/> against
/// it — the only database write in the whole path is the resulting append.
/// </summary>
public sealed class Decider<TState>
{
    /// <summary>What a brand-new stream looks like before any events.</summary>
    public required Func<TState> InitialState { get; init; }

    /// <summary>The business rule: decide the command against folded state, producing
    /// events to append, or throwing to reject.</summary>
    public required Func<TState, Command, IReadOnlyList<NewEvent>> Decide { get; init; }

    /// <summary>How one event changes the state.</summary>
    public required Func<TState, Event, TState> Evolve { get; init; }

    /// <summary>When true, <see cref="Decide"/> refuses only by throwing a <see cref="CommandRejectedException"/> (or
    /// a type derived from it), and anything else escaping it is a fault: a bug, or a broken invariant of the host's
    /// own, which the gateway answers 500 and a reactor retries, instead of a refusal answered 400 (hub K8, option b).
    /// A <see cref="System.Text.Json.JsonException"/> stays a refusal: the command's own payload could not be read.
    /// False (the default) keeps the old rule, any exception from <c>Decide</c> a refusal, for hand-written deciders
    /// until they move across. The generated deciders set it.</summary>
    public bool TypedRejections { get; init; }
}
