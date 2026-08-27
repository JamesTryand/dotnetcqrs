namespace DotnetCqrs.EventStore;

/// <summary>An event about to be appended.</summary>
public sealed record NewEvent(string Type, string Data, string Metadata = "{}");

/// <summary>A committed event envelope.</summary>
public sealed record Event(
    long Position,
    string Id,
    string Aggregate,
    string AggregateId,
    long Sequence,
    string Type,
    string Data,
    string Metadata,
    string Created);
