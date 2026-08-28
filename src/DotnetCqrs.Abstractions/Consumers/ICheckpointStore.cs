using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>Durably persists consumer progress. <c>SqliteEventStore</c> satisfies
/// this, so checkpoints normally live in the same store being polled.</summary>
public interface ICheckpointStore
{
    Task<long> CheckpointAsync(string name, CancellationToken ct = default);
    Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default);
}
