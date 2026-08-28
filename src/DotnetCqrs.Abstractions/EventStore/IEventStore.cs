using DotnetCqrs.Consumers;

namespace DotnetCqrs.EventStore;

/// <summary>
/// An append-only event log: appending IS the commit, a per-aggregate sequence gives
/// optimistic concurrency, a global position gives a total order. Also an
/// <see cref="IPollSource"/> and an <see cref="ICheckpointStore"/> — checkpoints
/// normally live in the same store being polled — so a <c>ConsumerEngine</c> can be
/// pointed straight at one.
///
/// <para>The provider-neutral contract <c>DeciderRegistry</c> depends on;
/// <c>SqliteEventStore</c> is the SQLite implementation, a Postgres one (Milestone 7)
/// is a second. Dead-letter capture is a separate concern — see
/// <see cref="IDeadLetterStore"/>.</para>
/// </summary>
public interface IEventStore : IPollSource, ICheckpointStore, IAsyncDisposable
{
    /// <summary>Atomically validates <paramref name="expectedSequence"/> against the
    /// stream's current length and appends <paramref name="events"/>. Sequences are
    /// 1-based and contiguous, so the expected sequence equals the number of events
    /// already in the stream. Throws <see cref="ConcurrencyException"/> on a mismatch.</summary>
    Task<IReadOnlyList<Event>> AppendAsync(
        string aggregate, string aggregateId, long expectedSequence, IReadOnlyList<NewEvent> events, CancellationToken ct = default);

    /// <summary>Returns all events of one stream in sequence order.</summary>
    Task<IReadOnlyList<Event>> LoadStreamAsync(string aggregate, string aggregateId, CancellationToken ct = default);
}
