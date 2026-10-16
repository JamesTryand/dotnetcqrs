using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using DotnetCqrs.ExtCalling;
using DotnetCqrs.Outbox;
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
/// reads and pooled connections. This backend deliberately does <b>not</b> deliver
/// unrestricted concurrent multi-writer append; see <c>docs/postgres-backend.md</c>.</para>
/// </summary>
public sealed class PostgresEventStore : IEventStore, IDeadLetterStore, IHeartbeatStore, IExtCallOutbox, IOutbox
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
            -- across providers (the cross-runtime timestamp format must match exactly).
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

        -- The external caller's outbox: events waiting for ExtCallOutboxWorker, one per consumer and event.
        CREATE TABLE IF NOT EXISTS extcall_outbox (
            id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            consumer        text NOT NULL,
            event_id        text NOT NULL,
            event_pos       bigint NOT NULL,
            event           text NOT NULL,
            attempts        integer NOT NULL DEFAULT 0,
            next_attempt_at text NOT NULL,
            follow_ups      text,
            dispatched      integer NOT NULL DEFAULT 0,
            UNIQUE (consumer, event_id)
        );
        CREATE INDEX IF NOT EXISTS idx_extcall_outbox_due ON extcall_outbox (consumer, next_attempt_at);

        -- The general outbox (IOutbox): work a consumer recorded, done by an OutboxWorker once due.
        CREATE TABLE IF NOT EXISTS outbox (
            consumer text NOT NULL,
            key      text NOT NULL,
            payload  text NOT NULL,
            due      text NOT NULL,
            attempts integer NOT NULL DEFAULT 0,
            PRIMARY KEY (consumer, key)
        );
        CREATE INDEX IF NOT EXISTS idx_outbox_due ON outbox (consumer, due);

        -- The writer heartbeat (health/telemetry contract section 5): one row, never an event.
        CREATE TABLE IF NOT EXISTS writer_heartbeat (
            id             integer PRIMARY KEY CHECK (id = 1),
            writer_node_id text NOT NULL,
            writer_ops_url text NOT NULL,
            written_at     text NOT NULL,
            sequence       bigint NOT NULL
        );
        """;

    // Arbitrary fixed application-wide key for pg_advisory_xact_lock. Any constant
    // works as long as every appender uses the same one; this value is not derived
    // from anything and never needs to change.
    private const long AppendAdvisoryLockKey = 917_000_000_000_000_007;

    private readonly NpgsqlDataSource _dataSource;

    private readonly Lock _subscribersLock = new();
    private readonly List<Action<Event>> _subscribers = [];
    private readonly List<Action> _commitSubscribers = [];

    // The commit push (R7): one session LISTENs on CommitChannel while anyone subscribes to commits. Null while not
    // listening; started by SubscribeToCommits, and again by the next successful poll once it has been lost.
    private readonly Lock _listenLock = new();
    private Task? _listening;
    private readonly CancellationTokenSource _closing = new();

    private PostgresEventStore(NpgsqlDataSource dataSource, string commitChannel)
    {
        _dataSource = dataSource;
        CommitChannel = commitChannel;
    }

    /// <summary>The <c>NOTIFY</c> channel every append to this log signals on as it commits (R7): one per log, named
    /// after the schema the log is in, so two logs in one database don't wake each other.</summary>
    public string CommitChannel { get; }

    /// <summary>Opens (creating the schema if necessary) the event store on the database
    /// named by <paramref name="connectionString"/>.</summary>
    public static async Task<PostgresEventStore> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var schema = dataSource.CreateCommand(Schema))
            await schema.ExecuteNonQueryAsync(ct);
        await using var currentSchema = dataSource.CreateCommand("SELECT current_schema()");
        var schemaName = (string)(await currentSchema.ExecuteScalarAsync(ct))!;

        return new PostgresEventStore(dataSource, ChannelFor(schemaName));
    }

    /// <summary>A channel name is an identifier, at most 63 bytes; a schema name long enough to overflow it is
    /// hashed instead.</summary>
    private static string ChannelFor(string schema)
    {
        const string prefix = "dotnetcqrs_events_";
        return Encoding.UTF8.GetByteCount(prefix + schema) <= 63
            ? prefix + schema
            : prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema)))[..40];
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

        // Delivered to every listening session when, and only if, the transaction commits (R7).
        await using (var notify = new NpgsqlCommand("SELECT pg_notify(@channel, @position)", connection, transaction))
        {
            notify.Parameters.AddWithValue("@channel", CommitChannel);
            notify.Parameters.AddWithValue("@position", appended[^1].Position.ToString(CultureInfo.InvariantCulture));
            await notify.ExecuteNonQueryAsync(ct);
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
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                results.Add(ReadEvent(reader));
        // The database answered, so a lost listener can listen again (R7). The engine polls on every pass, its tick
        // included, so nothing waits or retries for this: the pass after a loss restores it.
        EnsureListening();
        return results;
    }

    /// <inheritdoc/>
    public async Task<long?> HeadPositionAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(position), 0) FROM events";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
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
        // This process's own commit: no need to wait for its notification to come back.
        WakeCommitSubscribers();
    }

    /// <summary>Hears every process's commits to this log, through <c>LISTEN</c> on <see cref="CommitChannel"/>, as
    /// well as this process's own (R7).</summary>
    public void SubscribeToCommits(Action handler)
    {
        lock (_subscribersLock)
            _commitSubscribers.Add(handler);
        EnsureListening();
    }

    private void WakeCommitSubscribers()
    {
        List<Action> subscribers;
        lock (_subscribersLock)
            subscribers = [.. _commitSubscribers];
        foreach (var handler in subscribers)
        {
            try { handler(); } catch { /* best-effort */ }
        }
    }

    private void EnsureListening()
    {
        lock (_listenLock)
        {
            if (_listening is not null || _closing.IsCancellationRequested) return;
            lock (_subscribersLock)
                if (_commitSubscribers.Count == 0) return;
            _listening = Task.Run(() => ListenAsync(_closing.Token));
        }
    }

    /// <summary>Listens on one session until it is lost or the store is disposed. Once listening it wakes the
    /// subscribers, for anything committed while nobody was, and again when the session is lost, so the pass that
    /// wakes listens again (<see cref="PollAsync"/>). A session that never got as far as listening wakes nobody, so a
    /// refused <c>LISTEN</c> is tried once per pass, on the engine's tick, not in a loop.</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        var listened = false;
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            connection.Notification += (_, _) => WakeCommitSubscribers();
            await using (var listen = connection.CreateCommand())
            {
                listen.CommandText = $"LISTEN \"{CommitChannel.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                await listen.ExecuteNonQueryAsync(ct);
            }
            listened = true;
            WakeCommitSubscribers();
            while (true)
                await connection.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Lost (the server restarted or ended the session), or never got that far; see the summary.
        }
        lock (_listenLock)
            _listening = null;
        if (listened)
            WakeCommitSubscribers();
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

    // ---- IExtCallOutbox ----

    /// <summary>The one timestamp format the outbox uses, so text order is time order.</summary>
    private static string OutboxTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public async Task EnqueueAsync(string consumer, Event ev, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO extcall_outbox (consumer, event_id, event_pos, event, next_attempt_at)
            VALUES (@consumer, @eventId, @eventPos, @event, @now)
            ON CONFLICT (consumer, event_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@eventId", ev.Id);
        command.Parameters.AddWithValue("@eventPos", ev.Position);
        command.Parameters.AddWithValue("@event", JsonSerializer.Serialize(ev));
        command.Parameters.AddWithValue("@now", OutboxTime(now));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ExtCallOutboxEntry>> DueAsync(string consumer, DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, consumer, event, attempts, next_attempt_at, follow_ups, dispatched FROM extcall_outbox
            WHERE consumer = @consumer AND next_attempt_at <= @now ORDER BY event_pos, id LIMIT @limit
            """;
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@now", OutboxTime(now));
        command.Parameters.AddWithValue("@limit", limit);
        var entries = new List<ExtCallOutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(new ExtCallOutboxEntry(
                reader.GetInt64(0), reader.GetString(1), JsonSerializer.Deserialize<Event>(reader.GetString(2))!,
                reader.GetInt32(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6)));
        return entries;
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> NextDueAsync(string consumer, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(next_attempt_at) FROM extcall_outbox WHERE consumer = @consumer";
        command.Parameters.AddWithValue("@consumer", consumer);
        return await command.ExecuteScalarAsync(ct) is string text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(long id, int attempts, DateTimeOffset nextAttemptAt, string? followUps, int dispatched, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE extcall_outbox SET attempts = @attempts, next_attempt_at = @next, follow_ups = @followUps, dispatched = @dispatched
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@next", OutboxTime(nextAttemptAt));
        command.Parameters.AddWithValue("@followUps", (object?)followUps ?? DBNull.Value);
        command.Parameters.AddWithValue("@dispatched", dispatched);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM extcall_outbox WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---- IOutbox (the general outbox) ----
    // Explicit: IExtCallOutbox has a DueAsync and a NextDueAsync of the same shape.

    async Task IOutbox.EnqueueAsync(string consumer, string key, string payload, DateTimeOffset due, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox (consumer, key, payload, due) VALUES (@consumer, @key, @payload, @due)
            ON CONFLICT (consumer, key) DO NOTHING
            """;
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@due", OutboxTime(due));
        await command.ExecuteNonQueryAsync(ct);
    }

    async Task<IReadOnlyList<OutboxEntry>> IOutbox.DueAsync(string consumer, DateTimeOffset now, int limit, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT consumer, key, payload, due, attempts FROM outbox
            WHERE consumer = @consumer AND due <= @now ORDER BY due, key LIMIT @limit
            """;
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@now", OutboxTime(now));
        command.Parameters.AddWithValue("@limit", limit);
        var entries = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(new OutboxEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4)));
        return entries;
    }

    async Task<DateTimeOffset?> IOutbox.NextDueAsync(string consumer, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(due) FROM outbox WHERE consumer = @consumer";
        command.Parameters.AddWithValue("@consumer", consumer);
        return await command.ExecuteScalarAsync(ct) is string text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
    }

    async Task IOutbox.RetryAsync(string consumer, string key, int attempts, DateTimeOffset due, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox SET attempts = @attempts, due = @due WHERE consumer = @consumer AND key = @key";
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@due", OutboxTime(due));
        await command.ExecuteNonQueryAsync(ct);
    }

    async Task IOutbox.RemoveAsync(string consumer, string key, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM outbox WHERE consumer = @consumer AND key = @key";
        command.Parameters.AddWithValue("@consumer", consumer);
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(ct);
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

    /// <inheritdoc/>
    public async Task WriteHeartbeatAsync(string writerNodeId, string writerOpsUrl, DateTimeOffset writtenAt, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO writer_heartbeat (id, writer_node_id, writer_ops_url, written_at, sequence)
            VALUES (1, @node, @url, @at, 1)
            ON CONFLICT (id) DO UPDATE SET writer_node_id = excluded.writer_node_id,
                writer_ops_url = excluded.writer_ops_url, written_at = excluded.written_at,
                sequence = writer_heartbeat.sequence + 1
            """;
        command.Parameters.AddWithValue("@node", writerNodeId);
        command.Parameters.AddWithValue("@url", writerOpsUrl);
        command.Parameters.AddWithValue("@at", HeartbeatTimestamp.Format(writtenAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<WriterHeartbeat?> ReadHeartbeatAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT writer_node_id, writer_ops_url, written_at, sequence FROM writer_heartbeat WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new WriterHeartbeat(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        Task? listening;
        lock (_listenLock)
            listening = _listening;
        if (listening is not null)
            await listening;
        _closing.Dispose();
        await _dataSource.DisposeAsync();
    }
}
