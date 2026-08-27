using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>Processes events from the log in position order. Projections and
/// reactors are both consumers.</summary>
public interface IConsumer
{
    /// <summary>The durable checkpoint key.</summary>
    string Name { get; }

    /// <summary>Handles one event. Must be idempotent — delivery is at-least-once.</summary>
    Task ApplyAsync(Event ev, CancellationToken ct);
}
