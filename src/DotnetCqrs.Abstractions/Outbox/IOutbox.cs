namespace DotnetCqrs.Outbox;

/// <summary>One piece of work waiting in the outbox: what a consumer recorded, keyed so recording it again does
/// nothing, and when it is due.</summary>
/// <param name="Consumer">Whose work it is; one <see cref="OutboxWorker"/> takes each consumer's.</param>
/// <param name="Key">Unique per consumer.</param>
/// <param name="Payload">What the handler needs, as the consumer wrote it (typically JSON).</param>
/// <param name="Due">When it is to be done (or tried again).</param>
/// <param name="Attempts">How many times it has been tried and asked to be retried.</param>
public sealed record OutboxEntry(string Consumer, string Key, string Payload, DateTimeOffset Due, int Attempts);

/// <summary>What a handler made of an entry: done (it is removed), or to be tried again at a time.</summary>
public readonly record struct OutboxOutcome(bool IsDone, DateTimeOffset RetryAt, string? Error)
{
    /// <summary>Done: the entry is removed.</summary>
    public static OutboxOutcome Done => new(true, default, null);

    /// <summary>Not done: the entry is due again at <paramref name="at"/>, one more attempt counted.</summary>
    public static OutboxOutcome Retry(DateTimeOffset at, string? error = null) => new(false, at, error);
}

/// <summary>
/// A general due-time outbox: a consumer records work as it handles an event, and an <see cref="OutboxWorker"/> does
/// it once it is due, outside the consumer engine. So nothing slow or failing holds up the engine, a retry survives a
/// restart, and work due in the future (a review on its date) waits for that date, not for a timer to look for it.
/// Implemented by the event stores.
/// </summary>
public interface IOutbox
{
    /// <summary>Records work for <paramref name="consumer"/> under <paramref name="key"/>, due at
    /// <paramref name="due"/>. Recording the same key for the same consumer again does nothing, so a redelivered
    /// event is harmless.</summary>
    Task EnqueueAsync(string consumer, string key, string payload, DateTimeOffset due, CancellationToken ct = default);

    /// <summary>The consumer's entries due at <paramref name="now"/>, soonest first (then by key), at most
    /// <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<OutboxEntry>> DueAsync(string consumer, DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>When the consumer's next entry is due, or null if it has none.</summary>
    Task<DateTimeOffset?> NextDueAsync(string consumer, CancellationToken ct = default);

    /// <summary>Records that an entry is to be tried again: its attempts so far, and when it is next due.</summary>
    Task RetryAsync(string consumer, string key, int attempts, DateTimeOffset due, CancellationToken ct = default);

    /// <summary>Removes an entry: it is done.</summary>
    Task RemoveAsync(string consumer, string key, CancellationToken ct = default);
}
