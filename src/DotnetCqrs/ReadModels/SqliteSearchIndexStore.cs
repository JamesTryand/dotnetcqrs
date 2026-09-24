using System.Data.Common;
using DotnetCqrs.Consumers;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// The separate SQLite file (conventionally <c>search.db</c>) that holds search indexes
/// over personal data, the one kind of read model that keeps readable values at rest
/// (schema 3.1.0 <c>match</c>, mode <c>contains</c>, on a <c>field.pii</c> field).
///
/// <para><b>Why a separate file.</b> The index holds normalized plaintext, so it must stay
/// out of backups (crypto-shredding can't reach a copy that was never encrypted). As its
/// own file it can be excluded whole, rather than relying on whoever runs backups to skip
/// one table inside the read-model database. It is deleted from on erasure and rebuilt
/// from the log.</para>
///
/// <para><b>Its checkpoints live here too</b>: this type is the <see cref="ICheckpointStore"/>
/// for the consumers that fill it (register them with
/// <c>ConsumerEngine.Register(consumer, searchStore)</c>). If the file is lost, deleted or
/// not restored, the index and its position vanish together, and the consumer rebuilds from
/// the start of the log. A checkpoint kept anywhere else would outlive the index and leave
/// it silently incomplete.</para>
///
/// <para>Queries reach it by attaching it to the read-model connection under the schema
/// name <c>search</c> (<see cref="AttachAsync"/>), so a route's WHERE clause can use
/// <c>key IN (SELECT row_key FROM search.&lt;table&gt; ...)</c>. On Postgres the same SQL
/// works against a <c>search</c> schema of UNLOGGED tables; that store isn't built yet.</para>
/// </summary>
public sealed class SqliteSearchIndexStore : ICheckpointStore, IAsyncDisposable
{
    /// <summary>The schema name the store is attached under on a read-model connection.</summary>
    public const string SchemaName = "search";

    private readonly SqliteConnection _connection;

    private SqliteSearchIndexStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    /// <summary>The connection the index's own consumers write through.</summary>
    public DbConnection Connection => _connection;

    /// <summary>The file this store lives in.</summary>
    public string Path { get; }

    public static async Task<SqliteSearchIndexStore> OpenAsync(string path, CancellationToken ct = default)
    {
        // Pooling=False for the same reason as SqliteReadModelStore.OpenAsync.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 10000;
            -- Deleted rows are overwritten, not left readable in free pages until a VACUUM:
            -- erasure deletes plaintext from this file.
            PRAGMA secure_delete = ON;
            CREATE TABLE IF NOT EXISTS search_checkpoints (name TEXT PRIMARY KEY, position INTEGER NOT NULL);
            -- The key version each hashed index searches with (see HashedSearchIndexRegistration).
            CREATE TABLE IF NOT EXISTS search_index_versions (name TEXT PRIMARY KEY, version INTEGER NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);

        return new SqliteSearchIndexStore(connection, path);
    }

    /// <summary>Attaches <paramref name="path"/> to <paramref name="readModelConnection"/>
    /// as schema <see cref="SchemaName"/>, for query routes. Call once per connection.</summary>
    public static async Task AttachAsync(DbConnection readModelConnection, string path, CancellationToken ct = default)
    {
        await using var command = readModelConnection.CreateCommand();
        command.CommandText = $"ATTACH DATABASE @path AS {SchemaName}";
        var p = command.CreateParameter();
        p.ParameterName = "@path";
        p.Value = path;
        command.Parameters.Add(p);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT position FROM search_checkpoints WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(ct) is long position ? position : 0;
    }

    public async Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO search_checkpoints (name, position) VALUES (@name, @position)
            ON CONFLICT (name) DO UPDATE SET position = excluded.position
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@position", position);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Forgets a consumer's position, so it next starts from the beginning of the log.</summary>
    public async Task DeleteCheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM search_checkpoints WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The key version the hashed index <paramref name="name"/> searches with, or
    /// null if it has never been built in this file.</summary>
    public async Task<int?> IndexVersionAsync(string name, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version FROM search_index_versions WHERE name = @name";
        command.Parameters.AddWithValue("@name", name);
        return await command.ExecuteScalarAsync(ct) is long version ? (int)version : null;
    }

    public async Task SetIndexVersionAsync(string name, int version, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO search_index_versions (name, version) VALUES (@name, @version)
            ON CONFLICT (name) DO UPDATE SET version = excluded.version
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
