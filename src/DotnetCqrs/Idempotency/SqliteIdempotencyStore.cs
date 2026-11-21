using Microsoft.Data.Sqlite;

namespace DotnetCqrs.Idempotency;

/// <summary>
/// An <see cref="IIdempotencyStore"/> in a SQLite file of its own (a generated SQLite host's <c>data/idempotency.db</c>),
/// so a recorded answer survives a restart for its whole retention (at least 7 days). One connection, used one
/// operation at a time. Expired rows are dropped as new ones are written.
/// </summary>
public sealed class SqliteIdempotencyStore : IIdempotencyStore, IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS idempotency (
            key          TEXT PRIMARY KEY,
            request_hash TEXT NOT NULL,
            status       INTEGER NOT NULL,
            content_type TEXT NOT NULL,
            body         BLOB NOT NULL,
            expires_at   INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idempotency_expires ON idempotency (expires_at);
        """;

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;

    private SqliteIdempotencyStore(SqliteConnection connection, TimeSpan retention, TimeProvider time)
    {
        _connection = connection;
        _retention = retention;
        _time = time;
    }

    /// <summary>Opens (creating if need be) the store at <paramref name="path"/>.</summary>
    /// <param name="retention">How long a record is kept; default and minimum 7 days.</param>
    /// <param name="time">The clock; the system clock by default.</param>
    public static async Task<SqliteIdempotencyStore> OpenAsync(
        string path, TimeSpan? retention = null, TimeProvider? time = null, CancellationToken ct = default)
    {
        var checkedRetention = IdempotencyRetention.Check(retention);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000;" + Schema;
            await command.ExecuteNonQueryAsync(ct);
        }
        return new SqliteIdempotencyStore(connection, checkedRetention, time ?? TimeProvider.System);
    }

    public async Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT request_hash, status, content_type, body FROM idempotency WHERE key = $key AND expires_at > $now";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToUnixTimeMilliseconds());
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return new IdempotencyRecord(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), (byte[])reader.GetValue(3));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAddAsync(string key, IdempotencyRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            // Expired rows go first, so an expired record under this key gives way to the new one.
            await using (var prune = _connection.CreateCommand())
            {
                prune.CommandText = "DELETE FROM idempotency WHERE expires_at <= $now";
                prune.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                await prune.ExecuteNonQueryAsync(ct);
            }
            await using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO idempotency (key, request_hash, status, content_type, body, expires_at)
                VALUES ($key, $hash, $status, $type, $body, $expires)
                """;
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$hash", record.RequestHash);
            insert.Parameters.AddWithValue("$status", record.StatusCode);
            insert.Parameters.AddWithValue("$type", record.ContentType);
            insert.Parameters.AddWithValue("$body", record.Body);
            insert.Parameters.AddWithValue("$expires", (now + _retention).ToUnixTimeMilliseconds());
            return await insert.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
