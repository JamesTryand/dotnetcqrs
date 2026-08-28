namespace DotnetCqrs.EventStore;

/// <summary>
/// Captures permanently-failed deliveries of one event to one consumer, for
/// inspection and manual resolution rather than blocking the log. A separate
/// contract from <see cref="IEventStore"/> because a component like
/// <c>ExtCallerConsumer</c> only needs somewhere to record failures — pointed at a
/// store it owns outright — not the whole event-store surface. <c>SqliteEventStore</c>
/// implements both.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>Records a permanently failed delivery of <paramref name="ev"/> to
    /// <paramref name="consumer"/>.</summary>
    Task AddDeadLetterAsync(string consumer, Event ev, string error, CancellationToken ct = default);

    /// <summary>Lists dead letters, pending only unless <paramref name="includeResolved"/>.</summary>
    Task<IReadOnlyList<DeadLetter>> ListDeadLettersAsync(bool includeResolved = false, CancellationToken ct = default);

    /// <summary>Marks a dead letter resolved (retry succeeded, or dismissed). Throws
    /// <see cref="KeyNotFoundException"/> if <paramref name="id"/> doesn't exist.</summary>
    Task ResolveDeadLetterAsync(long id, CancellationToken ct = default);
}
