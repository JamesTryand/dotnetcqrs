namespace DotnetCqrs.Consumers;

/// <summary>Checkpoints that live only as long as the process. For a consumer whose
/// state is itself in memory (see <see cref="ConsumerEngine.Register(IConsumer, ICheckpointStore)"/>):
/// its position must restart with the state it describes, and must never be shared with
/// another instance. Every name starts at <paramref name="start"/>.</summary>
public sealed class InMemoryCheckpointStore(long start = 0) : ICheckpointStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _positions = new();

    public Task<long> CheckpointAsync(string name, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_positions.TryGetValue(name, out var pos) ? pos : start);
    }

    public Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
    {
        lock (_lock)
            _positions[name] = position;
        return Task.CompletedTask;
    }
}
