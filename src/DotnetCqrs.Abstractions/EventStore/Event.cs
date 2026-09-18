namespace DotnetCqrs.EventStore;

/// <summary>An event about to be appended. <see cref="Data"/> is the JSON the store
/// persists. <see cref="Payload"/> is an optional typed object that
/// <see cref="Deciders.DeciderRegistry"/> serialises into <see cref="Data"/> just before
/// appending — a decider that returns one (via <see cref="Of"/>) never touches JSON
/// itself, and the registry can run protection hooks over the typed values first.
/// A store never sees a non-null <see cref="Payload"/>: the registry materialises it.</summary>
public sealed record NewEvent(string Type, string Data, string Metadata = "{}", object? Payload = null)
{
    /// <summary>A typed event whose JSON the registry produces at append time.</summary>
    public static NewEvent Of(string type, object payload, string metadata = "{}") =>
        new(type, "", metadata, payload);
}

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
