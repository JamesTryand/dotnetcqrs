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

    /// <summary>True for a consumer whose output queries read (a projection, a search index):
    /// its lag, and whether it is blocked, count toward the node's readiness (health/telemetry
    /// contract section 4.5). False, the default, for reactors and other side-effect consumers,
    /// which only delay side effects and are reported as metrics.</summary>
    bool IsReadModel => false;
}
