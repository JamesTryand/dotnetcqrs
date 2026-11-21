using System.Collections.Concurrent;
using DotnetCqrs.Idempotency;

namespace DotnetCqrs.Host;

/// <summary>
/// An <see cref="IIdempotencyStore"/> in this process's memory: what the gateway uses when the host registers none.
/// It is lost on restart, so it cannot keep a record the contract's 7 days across one; a host that must (every
/// generated host) registers <c>SqliteIdempotencyStore</c> or <c>PostgresIdempotencyStore</c> instead.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (IdempotencyRecord Record, DateTimeOffset Expires)> _records = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <param name="retention">How long a record is kept; default and minimum 7 days.</param>
    /// <param name="time">The clock; the system clock by default.</param>
    public InMemoryIdempotencyStore(TimeSpan? retention = null, TimeProvider? time = null)
    {
        _retention = IdempotencyRetention.Check(retention);
        _time = time ?? TimeProvider.System;
    }

    public Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(key, out var entry) && entry.Expires > _time.GetUtcNow() ? entry.Record : null);

    public Task<bool> TryAddAsync(string key, IdempotencyRecord record, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (now >= _nextPrune)
        {
            // At most once a minute: a sweep walks every record.
            _nextPrune = now + TimeSpan.FromMinutes(1);
            foreach (var (k, entry) in _records)
                if (entry.Expires <= now)
                    _records.TryRemove(new KeyValuePair<string, (IdempotencyRecord, DateTimeOffset)>(k, entry));
        }
        // An expired record still in place (not yet swept) gives way to the new one.
        if (_records.TryGetValue(key, out var existing) && existing.Expires <= now)
            _records.TryRemove(new KeyValuePair<string, (IdempotencyRecord, DateTimeOffset)>(key, existing));
        return Task.FromResult(_records.TryAdd(key, (record, now + _retention)));
    }
}
