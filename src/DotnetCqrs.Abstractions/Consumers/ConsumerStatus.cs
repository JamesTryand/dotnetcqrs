namespace DotnetCqrs.Consumers;

/// <summary>A consumer's state, as the health/telemetry contract names it (<c>STATE-MACHINES.md</c>,
/// machine 2, <c>ReadModelConsumer</c>). Stopped needs no value: an unregistered consumer is
/// simply not listed.</summary>
public enum ConsumerState
{
    /// <summary>Lag within the engine's <see cref="ConsumerEngine.LagThreshold"/>.</summary>
    Current,

    /// <summary>Lag over the threshold, or not yet measured (the consumer has not run a pass).</summary>
    Behind,

    /// <summary>Stuck on a failing event (or failing to read), retrying every pass.</summary>
    Blocked,
}

/// <summary>One consumer's progress, from <see cref="ConsumerEngine.Status"/>.</summary>
/// <param name="Name">The consumer's checkpoint name.</param>
/// <param name="IsReadModel">Whether it counts toward readiness (<see cref="IConsumer.IsReadModel"/>).</param>
/// <param name="State">Current, behind or blocked.</param>
/// <param name="Checkpoint">The last position it applied; null before its first pass.</param>
/// <param name="LagPositions">Positions behind the log head as of its last pass; null when the
/// source cannot report a head (<see cref="IPollSource.HeadPositionAsync"/>) or before its first
/// pass.</param>
/// <param name="LagSeconds">The age of the oldest event it has not yet applied (0 when caught up);
/// null before its first pass.</param>
public sealed record ConsumerStatus(
    string Name, bool IsReadModel, ConsumerState State, long? Checkpoint, long? LagPositions, double? LagSeconds);
