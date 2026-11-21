using Npgsql;

namespace DotnetCqrs.Idempotency;

/// <summary>
/// An <see cref="IIdempotencyStore"/> in Postgres, one table (<c>cqrs_idempotency</c>) in the connection's default
/// schema, beside the event log of a generated host on Postgres: a recorded answer survives a restart, and the host's
/// move to another machine, for its whole retention (at least 7 days). Expired rows are dropped as new ones are
/// written. A pooled connection per operation.
/// </summary>
public sealed class PostgresIdempotencyStore : IIdempotencyStore, IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS cqrs_idempotency (
            key          TEXT PRIMARY KEY,
            request_hash TEXT NOT NULL,
            status       INTEGER NOT NULL,
            content_type TEXT NOT NULL,
            body         BYTEA NOT NULL,
            expires_at   TIMESTAMPTZ NOT NULL
        );
        CREATE INDEX IF NOT EXISTS cqrs_idempotency_expires ON cqrs_idempotency (expires_at);
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;

    private PostgresIdempotencyStore(NpgsqlDataSource dataSource, TimeSpan retention, TimeProvider time)
    {
        _dataSource = dataSource;
        _retention = retention;
        _time = time;
    }

    /// <summary>Opens (creating the table if need be) the store on the database named by
    /// <paramref name="connectionString"/>.</summary>
    /// <param name="retention">How long a record is kept; default and minimum 7 days.</param>
    /// <param name="time">The clock; the system clock by default.</param>
    public static async Task<PostgresIdempotencyStore> OpenAsync(
        string connectionString, TimeSpan? retention = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        var checkedRetention = IdempotencyRetention.Check(retention);
        var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var schema = dataSource.CreateCommand(Schema))
            await schema.ExecuteNonQueryAsync(ct);
        return new PostgresIdempotencyStore(dataSource, checkedRetention, time ?? TimeProvider.System);
    }

    public async Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT request_hash, status, content_type, body FROM cqrs_idempotency WHERE key = @key AND expires_at > @now");
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@now", _time.GetUtcNow().UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new IdempotencyRecord(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), (byte[])reader.GetValue(3));
    }

    public async Task<bool> TryAddAsync(string key, IdempotencyRecord record, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        // Expired rows go first, so an expired record under this key gives way to the new one.
        await using (var prune = _dataSource.CreateCommand("DELETE FROM cqrs_idempotency WHERE expires_at <= @now"))
        {
            prune.Parameters.AddWithValue("@now", now);
            await prune.ExecuteNonQueryAsync(ct);
        }
        await using var insert = _dataSource.CreateCommand("""
            INSERT INTO cqrs_idempotency (key, request_hash, status, content_type, body, expires_at)
            VALUES (@key, @hash, @status, @type, @body, @expires)
            ON CONFLICT (key) DO NOTHING
            """);
        insert.Parameters.AddWithValue("@key", key);
        insert.Parameters.AddWithValue("@hash", record.RequestHash);
        insert.Parameters.AddWithValue("@status", record.StatusCode);
        insert.Parameters.AddWithValue("@type", record.ContentType);
        insert.Parameters.AddWithValue("@body", record.Body);
        insert.Parameters.AddWithValue("@expires", now + _retention);
        return await insert.ExecuteNonQueryAsync(ct) > 0;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
