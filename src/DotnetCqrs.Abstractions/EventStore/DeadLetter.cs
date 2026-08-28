namespace DotnetCqrs.EventStore;

/// <summary>A permanently failed delivery of one event to one consumer, captured for
/// inspection and manual resolution rather than blocking the log.</summary>
public sealed record DeadLetter(
    long Id,
    string Consumer,
    long EventPosition,
    Event Event,
    string Error,
    long Attempts,
    string FirstFailed,
    string LastFailed,
    bool Resolved);
