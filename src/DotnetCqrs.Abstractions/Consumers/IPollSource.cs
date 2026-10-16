using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>The event feed a <see cref="ConsumerEngine"/> follows: <see cref="PollAsync"/>
/// for catch-up batches, <see cref="Subscribe"/> for the in-process nudge that shortens
/// the usual tick latency. <c>SqliteEventStore</c> satisfies this.</summary>
public interface IPollSource
{
    Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default);

    /// <summary>Calls <paramref name="handler"/> with each event this process appends, after it commits. In-process
    /// only, whatever the store: other processes' appends never reach it (see <see cref="SubscribeToCommits"/>).</summary>
    void Subscribe(Action<Event> handler);

    /// <summary>Calls <paramref name="handler"/> whenever new events may have been committed: by this process, and,
    /// where the store can hear them, by any other process appending to the same log (Postgres's <c>LISTEN/NOTIFY</c>).
    /// A wake-up, not a delivery: it may come more than once for one commit, or for
    /// none, and <see cref="PollAsync"/> is still how events are read. A <see cref="ConsumerEngine"/> runs a pass on
    /// it. The default hears this process only, through <see cref="Subscribe"/>.</summary>
    void SubscribeToCommits(Action handler) => Subscribe(_ => handler());

    /// <summary>The position of the newest committed event (0 for an empty log), or null when the
    /// source cannot say. A <see cref="ConsumerEngine"/> uses it for each consumer's lag in
    /// positions; the default is null, so a source written before this existed keeps
    /// compiling and simply reports that lag as unknown.</summary>
    Task<long?> HeadPositionAsync(CancellationToken ct = default) => Task.FromResult<long?>(null);
}
