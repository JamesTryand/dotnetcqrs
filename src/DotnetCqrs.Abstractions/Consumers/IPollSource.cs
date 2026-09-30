using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>The event feed a <see cref="ConsumerEngine"/> follows: <see cref="PollAsync"/>
/// for catch-up batches, <see cref="Subscribe"/> for the in-process nudge that shortens
/// the usual tick latency. <c>SqliteEventStore</c> satisfies this.</summary>
public interface IPollSource
{
    Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default);
    void Subscribe(Action<Event> handler);

    /// <summary>The position of the newest committed event (0 for an empty log), or null when the
    /// source cannot say. A <see cref="ConsumerEngine"/> uses it for each consumer's lag in
    /// positions; the default is null, so a source written before this existed keeps
    /// compiling and simply reports that lag as unknown.</summary>
    Task<long?> HeadPositionAsync(CancellationToken ct = default) => Task.FromResult<long?>(null);
}
