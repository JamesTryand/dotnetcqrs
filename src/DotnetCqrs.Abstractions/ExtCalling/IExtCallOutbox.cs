using DotnetCqrs.EventStore;

namespace DotnetCqrs.ExtCalling;

/// <summary>One event waiting for <see cref="ExtCallOutboxWorker"/>: the call it is to make (or, once made, the
/// follow-ups its response produced, and how many of them are dispatched), how many attempts it has had, and when
/// the next one is due.</summary>
/// <param name="FollowUps">Null until the call has succeeded; then the follow-ups as JSON, kept so a dispatch can be
/// retried without calling the outside service again.</param>
/// <param name="Dispatched">How many of <paramref name="FollowUps"/> are dispatched, in order.</param>
public sealed record ExtCallOutboxEntry(
    long Id, string Consumer, Event Event, int Attempts, DateTimeOffset NextAttemptAt, string? FollowUps, int Dispatched);

/// <summary>
/// The external caller's outbox (D40, timesheets' paradigm audit R4): <see cref="ExtCallerConsumer"/> records an event
/// here, in the engine, and <see cref="ExtCallOutboxWorker"/> makes the call outside it, so a slow or failing service
/// never holds up any consumer, and a retry survives a restart. Implemented by the event stores, beside their dead
/// letters.
/// </summary>
public interface IExtCallOutbox
{
    /// <summary>Records <paramref name="ev"/> for <paramref name="consumer"/>, due now. Recording the same event for the
    /// same consumer again does nothing, so a redelivery is harmless.</summary>
    Task EnqueueAsync(string consumer, Event ev, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The consumer's entries due at <paramref name="now"/>, oldest event first, at most
    /// <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ExtCallOutboxEntry>> DueAsync(string consumer, DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>When the consumer's next entry is due, or null if it has none.</summary>
    Task<DateTimeOffset?> NextDueAsync(string consumer, CancellationToken ct = default);

    /// <summary>Records an entry's progress: its attempts, when it is next due, its follow-ups and how many are
    /// dispatched.</summary>
    Task UpdateAsync(long id, int attempts, DateTimeOffset nextAttemptAt, string? followUps, int dispatched, CancellationToken ct = default);

    /// <summary>Removes an entry: done, or dead-lettered.</summary>
    Task RemoveAsync(long id, CancellationToken ct = default);
}
