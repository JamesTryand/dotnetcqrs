using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using Npgsql;

namespace DotnetCqrs.Postgres;

/// <summary>
/// The Postgres implementation of <see cref="IEventStore"/> (and
/// <see cref="IDeadLetterStore"/>): the same append-only log as
/// <c>SqliteEventStore</c> — appending IS the commit, a per-aggregate sequence gives
/// optimistic concurrency, a global auto-increment position gives a total order — on a
/// client-server database instead of a single local file.
///
/// <para><b>Pool-aware.</b> Unlike the SQLite store (one dedicated connection for its
/// whole lifetime, which is what a local file wants), this holds an
/// <see cref="NpgsqlDataSource"/> and takes a pooled connection per operation.</para>
///
/// <para><b>Append is serialized by a transaction-scoped advisory lock</b>
/// (<c>pg_advisory_xact_lock</c>), not by an in-process semaphore. Identity columns hand
/// out <c>position</c> values in request order, but two overlapping transactions can
/// COMMIT out of that order — and a <see cref="ConsumerEngine"/> polling
/// <c>WHERE position &gt; checkpoint ORDER BY position</c> would then step over the
/// lower position for good. The advisory lock makes commit order equal position order,
/// reproducing SQLite's single-writer append semantics while still allowing concurrent
/// reads and pooled connections. Milestone 7 deliberately does <b>not</b> deliver
/// unrestricted concurrent multi-writer append; see <c>docs/postgres-backend.md</c>.</para>
/// </summary>
public sealed class PostgresEventStore : IEventStore, IDeadLetterStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS events (
            position     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            id           text NOT NULL UNIQUE,
            aggregate    text NOT NULL,
            aggregate_id text NOT NULL,
            sequence     bigint NOT NULL,
            type         text NOT NULL,
            data         text NOT NULL,
            metadata     text NOT NULL DEFAULT '{}',
            -- Deliberately text, in the same ...T...Z shape SqliteEventStore's
            -- strftime default produces, so the Event envelope is byte-identical
            -- across providers (Milestone 5's cross-runtime format finding).
            created      text NOT NULL DEFAULT to_char(now() AT TIME ZONE 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
            UNIQUE (aggregate, aggregate_id, sequence)
        );
        CREATE INDEX IF NOT EXISTS idx_events_stream ON events (aggregate, aggregate_id, sequence);

        CREATE TABLE IF NOT EXISTS consumer_checkpoints (
            name     text PRIMARY KEY,
            position bigint NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS dead_letters (
            id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            consumer     text NOT NULL,
            event_pos    bigint NOT NULL,
            event        text NOT NULL,
            error        text NOT NULL,
            attempts     bigint NOT NULL DEFAULT 1,
            first_failed text NOT NULL,
            last_failed  text NOT NULL,
            resolved     boolean NOT NULL DEFAULT false
        );
        """;

    // Arbitrary fixed application-wide key for pg_advisory_xact_lock. Any constant
    // works as long as every appender uses the same one; this value is not derived
    // from anything and never needs to change.
    private const long AppendAdvisoryLockKey = 917_000_000_000_000_007;

    private readonly NpgsqlDataSource _dataSource;

    private readonly Lock _subscribersLock = new();
    private readonly List<Action<Event>> _subscribers = [];

    private PostgresEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Opens (creating the schema if necessary) the event store on the database
    /// named by <paramref name="connectionString"/>.</summary>
    public static async Task<PostgresEventStore> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var schema = dataSource.CreateCommand(Schema))
            await schema.ExecuteNonQueryAsync(ct);

        return new PostgresEventStore(dataSource);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Event>> AppendAsync(
        string aggregate, string aggregateId, long expectedSequence, IReadOnlyList<NewEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return [];

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Serializes appends across every connection/process: the whole
        // check-then-insert runs under one cluster-global lock, auto-released at
        // transaction end, so commit order == position order.
        await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            advisoryLock.Parameters.AddWithValue("@key", AppendAdvisoryLockKey);
            await advisoryLock.ExecuteNonQueryAsync(ct);
        }

        var current = await CurrentSequenceAsync(connection, transaction, aggregate, aggregateId, ct);
        if (current != expectedSequence)
            throw new ConcurrencyException(aggregate, aggregateId, current, expectedSequence);

        var appended = new List<Event>(events.Count);
        var sequence = current;
        foreach (var newEvent in events)
        {
            sequence++;
            appended.Add(await InsertAsync(connection, transaction, aggregate, aggregateId, sequence, newEvent, ct));
        }

        await transaction.CommitAsync(ct);
        foreach (var ev in appended)
            Publish(ev);
        return appended;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position, id, aggregate, aggregate_id, sequence, type, data, metadata, created
            FROM events WHERE position > @after ORDER BY position LIMIT @limit
            """;
        command.Parameters.AddWithValue("@after", after);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEvent(reader));
        return results;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public async Task<long> CheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT position FROM consumer_checkpoints WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : (long)result;
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO consumer_checkpoints (name, position) VALUES (@name, @position)
            ON CONFLICT (name) DO UPDATE SET position = excluded.position
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@position", position);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Event>> LoadStreamAsync(string aggregate, string aggregateId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT position, id, aggregate, aggregate_id, sequence, type, data, metadata, created
            FROM events WHERE aggregate = @aggregate AND aggregate_id = @aggregateId
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("@aggregate", aggregate);
        command.Parameters.AddWithValue("@aggregateId", aggregateId);

        var results = new List<Event>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadEvent(reader));
        return results;
    }

    private static async Task<long> CurrentSequenceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string aggregate, string aggregateId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(MAX(sequence), 0) FROM events
            WHERE aggregate = @aggregate AND aggregate_id = @aggregateId
            """, connection, transaction);
        command.Parameters.AddWithValue("@aggregate", aggregate);
        command.Parameters.AddWithValue("@aggregateId", aggregateId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<Event> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string aggregate, string aggregateId, long sequence, NewEvent newEvent, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var metadata = string.IsNullOrEmpty(newEvent.Metadata) ? "{}" : newEvent.Metadata;

        await using var command = new NpgsqlCommand("""
            INSERT INTO events (id, aggregate, aggregate_id, sequence, type, data, metadata)
            VALUES (@id, @aggregate, @aggregateId, @sequence, @type, @data, @metadata)
            RETURNING position, created
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@aggregate", aggregate);
        command.Parameters.AddWithValue("@aggregateId", aggregateId);
        command.Parameters.AddWithValue("@sequence", sequence);
        command.Parameters.AddWithValue("@type", newEvent.Type);
        command.Parameters.AddWithValue("@data", newEvent.Data);
        command.Parameters.AddWithValue("@metadata", metadata);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var position = reader.GetInt64(0);
        var created = reader.GetString(1);

        return new Event(position, id, aggregate, aggregateId, sequence, newEvent.Type, newEvent.Data, metadata, created);
    }

    /// <inheritdoc/>
    public async Task AddDeadLetterAsync(string consumer, Event ev, string error, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dead_letters (consumer, event_pos, event, error, first_failed, last_failed)
            VALUES (@consumer, @eventPos, @event, @error, @now, @now)
            """;
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@eventPos", ev.Position);
        command.Parameters.AddWithValue("@event", JsonSerializer.Serialize(ev));
        command.Parameters.AddWithValue("@error", error);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeadLetter>> ListDeadLettersAsync(bool includeResolved = false, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, consumer, event_pos, event, error, attempts, first_failed, last_failed, resolved FROM dead_letters"
            + (includeResolved ? " ORDER BY id" : " WHERE resolved = false ORDER BY id");

        var results = new List<DeadLetter>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ev = JsonSerializer.Deserialize<Event>(reader.GetString(3))!;
            results.Add(new DeadLetter(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), ev,
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
                reader.GetBoolean(8)));
        }
        return results;
    }

    /// <inheritdoc/>
    public async Task ResolveDeadLetterAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dead_letters SET resolved = true WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected == 0)
            throw new KeyNotFoundException($"dead letter {id} not found");
    }

    private static Event ReadEvent(NpgsqlDataReader reader) => new(
        Position: reader.GetInt64(0),
        Id: reader.GetString(1),
        Aggregate: reader.GetString(2),
        AggregateId: reader.GetString(3),
        Sequence: reader.GetInt64(4),
        Type: reader.GetString(5),
        Data: reader.GetString(6),
        Metadata: reader.GetString(7),
        Created: reader.GetString(8));

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
