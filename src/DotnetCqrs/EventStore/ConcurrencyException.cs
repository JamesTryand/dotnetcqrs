namespace DotnetCqrs.EventStore;

/// <summary>Thrown by <see cref="SqliteEventStore.AppendAsync"/> when a stream's
/// current sequence does not match the caller's expected sequence.</summary>
public sealed class ConcurrencyException(string aggregate, string aggregateId, long actualSequence, long expectedSequence)
    : Exception($"stream {aggregate}/{aggregateId} is at sequence {actualSequence}, expected {expectedSequence}")
{
    public string Aggregate { get; } = aggregate;
    public string AggregateId { get; } = aggregateId;
    public long ActualSequence { get; } = actualSequence;
    public long ExpectedSequence { get; } = expectedSequence;
}
