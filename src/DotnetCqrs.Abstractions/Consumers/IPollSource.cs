using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>The event feed a <see cref="ConsumerEngine"/> follows: <see cref="PollAsync"/>
/// for catch-up batches, <see cref="Subscribe"/> for the in-process nudge that shortens
/// the usual tick latency. <c>SqliteEventStore</c> satisfies this.</summary>
public interface IPollSource
{
    Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default);
    void Subscribe(Action<Event> handler);
}
