using System.Text.Json;
using Microsoft.Data.Sqlite;
using DotnetCqrs.Consumers;

namespace DotnetCqrs.EventStore;

/// <summary>
/// An append-only event log backed by a single SQLite file. Appending IS the
/// commit; a per-aggregate sequence gives optimistic concurrency, a global
/// auto-increment position gives a total order. Also satisfies
/// <see cref="IPollSource"/> and <see cref="ICheckpointStore"/>, the ports a
/// <see cref="ConsumerEngine"/> needs — checkpoints normally live in the same
/// store being polled.
/// </summary>
public sealed class SqliteEventStore : IAsyncDisposable, IPollSource, ICheckpointStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            position     INTEGER PRIMARY KEY AUTOINCREMENT,
            id           TEXT NOT NULL UNIQUE,
            aggregate    TEXT NOT NULL,
            aggregate_id TEXT NOT NULL,
            sequence     INTEGER NOT NULL,
            type         TEXT NOT NULL,
            data         TEXT NOT NULL,
            metadata     TEXT NOT NULL DEFAULT '{}',
            created      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            UNIQUE (aggregate, aggregate_id, sequence)
        );
        CREATE INDEX IF NOT EXISTS idx_events_stream ON events (aggregate, aggregate_id, sequence);

        CREATE TABLE IF NOT EXISTS consumer_checkpoints (
            name     TEXT PRIMARY KEY,
            position INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS dead_letters (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            consumer     TEXT NOT NULL,
            event_pos    INTEGER NOT NULL,
            event        TEXT NOT NULL,
            error        TEXT NOT NULL,
            attempts     INTEGER NOT NULL DEFAULT 1,
            first_failed TEXT NOT NULL,
            last_failed  TEXT NOT NULL,
            resolved     INTEGER NOT NULL DEFAULT 0
        );
        """;

    private readonly SqliteConnection _connection;

    // Serializes appends so the expected-sequence check and the insert stay
    // atomic from the store's point of view (one writer, same as SQLite itself
    // wants for a single file).
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    private readonly Lock _subscribersLock = new();
    private readonly List<Action<Event>> _subscribers = [];

    private SqliteEventStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (creating if necessary) the event store at <paramref name="path"/>.</summary>
    public static async Task<SqliteEventStore> OpenAsync(string path, CancellationToken ct = default)
    {
        // Pooling=False: a pooled connection keeps the underlying OS file handle open
        // past DisposeAsync, so a caller that deletes/replaces the file right after
        // closing the store (tests included) hits "file in use" -- this store already
        // holds one dedicated connection for its whole lifetime, so pooling buys nothing.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            await schema.ExecuteNonQueryAsync(ct);
        }

        return new SqliteEventStore(connection);
    }

    /// <summary>
    /// Atomically validates <paramref name="expectedSequence"/> against the stream's
    /// current length and appends <paramref name="events"/>. Sequences are 1-based
    /// and contiguous, so the expected sequence equals the number of events already
    /// in the stream.
    /// </summary>
    public async Task<IReadOnlyList<Event>> AppendAsync(
        string aggregate, string aggregateId, long expectedSequence, IReadOnlyList<NewEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return [];

        await _appendLock.WaitAsync(ct);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);

            var current = await CurrentSequenceAsync(transaction, aggregate, aggregateId, ct);
            if (current != expectedSequence)
                throw new ConcurrencyException(aggregate, aggregateId, current, expectedSequence);

            var appended = new List<Event>(events.Count);
            var sequence = current;
            foreach (var newEvent in events)
            {
                sequence++;
                appended.Add(await InsertAsync(transaction, aggregate, aggregateId, sequence, newEvent, ct));
            }

            await transaction.CommitAsync(ct);
            foreach (var ev in appended)
                Publish(ev);
            return appended;
        }
        finally
        {
            _appendLock.Release();
        }
    }

    /// <summary>Returns up to <paramref name="limit"/> events with position &gt; <paramref name="after"/>,
    /// in position order — the catch-up feed a <see cref="ConsumerEngine"/> polls.</summary>
    public async Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT position, id, aggregate, aggregate_id, sequence, type, data, metadata, created
            FROM events WHERE position > $after ORDER BY position LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEvent(reader));
        return results;
    }

    /// <summary>Registers <paramref name="handler"/> to be called (best-effort, in-process)
    /// with each event right after it commits. A consumer needing guaranteed delivery
    /// should poll via <see cref="PollAsync"/> with a durable checkpoint instead.</summary>
    public void Subscribe(Action<Event> handler)
    {
        lock (_subscribersLock)
            _subscribers.Add(handler);
    }

    private void Publish(Event ev)
    {
        List<Action<Event>> subscribers;
        lock (_subscribersLock)
            subscribers = [.. _subscribers];
        foreach (var handler in subscribers)
            _ = Task.Run(() =>
            {
                try { handler(ev); } catch { /* best-effort */ }
            });
    }

    /// <summary>Returns the durable position of a named consumer (0 if none).</summary>
    public async Task<long> CheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT position FROM consumer_checkpoints WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null ? 0 : (long)result;
    }

    /// <summary>Durably stores the position of a named consumer.</summary>
    public async Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO consumer_checkpoints (name, position) VALUES ($name, $position)
            ON CONFLICT (name) DO UPDATE SET position = excluded.position
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$position", position);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Returns all events of one stream in sequence order.</summary>
    public async Task<IReadOnlyList<Event>> LoadStreamAsync(string aggregate, string aggregateId, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT position, id, aggregate, aggregate_id, sequence, type, data, metadata, created
            FROM events WHERE aggregate = $aggregate AND aggregate_id = $aggregateId
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$aggregate", aggregate);
        command.Parameters.AddWithValue("$aggregateId", aggregateId);

        var results = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEvent(reader));
        return results;
    }

    private static async Task<long> CurrentSequenceAsync(
        SqliteTransaction transaction, string aggregate, string aggregateId, CancellationToken ct)
    {
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(sequence), 0) FROM events
            WHERE aggregate = $aggregate AND aggregate_id = $aggregateId
            """;
        command.Parameters.AddWithValue("$aggregate", aggregate);
        command.Parameters.AddWithValue("$aggregateId", aggregateId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Event> InsertAsync(
        SqliteTransaction transaction, string aggregate, string aggregateId, long sequence, NewEvent newEvent, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var metadata = string.IsNullOrEmpty(newEvent.Metadata) ? "{}" : newEvent.Metadata;

        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events (id, aggregate, aggregate_id, sequence, type, data, metadata)
            VALUES ($id, $aggregate, $aggregateId, $sequence, $type, $data, $metadata)
            RETURNING position, created
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$aggregate", aggregate);
        command.Parameters.AddWithValue("$aggregateId", aggregateId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$type", newEvent.Type);
        command.Parameters.AddWithValue("$data", newEvent.Data);
        command.Parameters.AddWithValue("$metadata", metadata);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var position = reader.GetInt64(0);
        var created = reader.GetString(1);

        return new Event(position, id, aggregate, aggregateId, sequence, newEvent.Type, newEvent.Data, metadata, created);
    }

    /// <summary>Records a permanently failed delivery of <paramref name="ev"/> to
    /// <paramref name="consumer"/> — captured for inspection/manual resolution rather
    /// than blocking the log.</summary>
    public async Task AddDeadLetterAsync(string consumer, Event ev, string error, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dead_letters (consumer, event_pos, event, error, first_failed, last_failed)
            VALUES ($consumer, $eventPos, $event, $error, $now, $now)
            """;
        command.Parameters.AddWithValue("$consumer", consumer);
        command.Parameters.AddWithValue("$eventPos", ev.Position);
        command.Parameters.AddWithValue("$event", JsonSerializer.Serialize(ev));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lists dead letters, pending only unless <paramref name="includeResolved"/>.</summary>
    public async Task<IReadOnlyList<DeadLetter>> ListDeadLettersAsync(bool includeResolved = false, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, consumer, event_pos, event, error, attempts, first_failed, last_failed, resolved FROM dead_letters"
            + (includeResolved ? " ORDER BY id" : " WHERE resolved = 0 ORDER BY id");

        var results = new List<DeadLetter>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ev = JsonSerializer.Deserialize<Event>(reader.GetString(3))!;
            results.Add(new DeadLetter(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), ev,
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt64(8) != 0));
        }
        return results;
    }

    /// <summary>Marks a dead letter resolved (retry succeeded, or dismissed). Throws
    /// <see cref="KeyNotFoundException"/> if <paramref name="id"/> doesn't exist.</summary>
    public async Task ResolveDeadLetterAsync(long id, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE dead_letters SET resolved = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected == 0)
            throw new KeyNotFoundException($"dead letter {id} not found");
    }

    private static Event ReadEvent(SqliteDataReader reader) => new(
        Position: reader.GetInt64(0),
        Id: reader.GetString(1),
        Aggregate: reader.GetString(2),
        AggregateId: reader.GetString(3),
        Sequence: reader.GetInt64(4),
        Type: reader.GetString(5),
        Data: reader.GetString(6),
        Metadata: reader.GetString(7),
        Created: reader.GetString(8));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
