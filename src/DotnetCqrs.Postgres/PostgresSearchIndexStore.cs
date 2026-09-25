using System.Data.Common;
using DotnetCqrs.ReadModels;
using Npgsql;

namespace DotnetCqrs.Postgres;

/// <summary>
/// The Postgres <see cref="ISearchIndexStore"/>: a schema named <c>search</c> of
/// <b>unlogged</b> tables in the application's database, so the query routes' SQL
/// (<c>key IN (SELECT row_key FROM search.&lt;table&gt; ...)</c>) is the same as on SQLite, where
/// <c>search.db</c> is attached under that name. One application per database, as one
/// <c>search.db</c> per application.
///
/// <para><b>Why unlogged</b> (platform/eventmodeling-codegen, "Postgres parity" item 5). The
/// index holds normalized plaintext personal data and must not come back from a backup after
/// the person is erased. Unlogged rows never enter the write-ahead log, so physical backups
/// (<c>pg_basebackup</c>, pgBackRest, Barman), point-in-time recovery and streaming replicas
/// don't carry them, and Postgres empties unlogged tables after any crash recovery, which
/// includes restoring a physical backup or a snapshot of a running server. Two gaps remain,
/// both closed by the startup purge against the key service's erasure ledger
/// (<c>PurgeErasedSubjectsAsync</c>): a <c>pg_dump</c> without
/// <c>--exclude-schema=search</c>, and a snapshot taken after a clean shutdown.</para>
///
/// <para><b>Why <see cref="ScrubAsync"/> rewrites tables.</b> Postgres never updates or deletes a
/// row in place: the old version stays in the table's files until the space is reused, so a
/// deleted value is still on disk. <c>VACUUM FULL</c> copies the live rows to a new file and
/// deletes the old one. Erasures are rare, so each one does this for the tables it touched,
/// holding a brief exclusive lock on each.</para>
///
/// <para>The connecting role owns the tables it creates, which <c>VACUUM FULL</c> requires.</para>
/// </summary>
public sealed class PostgresSearchIndexStore : ISearchIndexStore
{
    private readonly NpgsqlConnection _connection;

    private PostgresSearchIndexStore(NpgsqlConnection connection) => _connection = connection;

    public DbConnection Connection => _connection;

    public string CreateTable => "CREATE UNLOGGED TABLE";

    /// <summary>Opens the store in the database named by <paramref name="connectionString"/>,
    /// creating the <c>search</c> schema and its bookkeeping tables if necessary. The
    /// connection's search path is set to <c>search</c> whatever the connection string says, so
    /// the index consumers' unqualified table names land there.</summary>
    public static async Task<PostgresSearchIndexStore> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = ISearchIndexStore.SchemaName };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE SCHEMA IF NOT EXISTS {ISearchIndexStore.SchemaName};
            CREATE UNLOGGED TABLE IF NOT EXISTS search_checkpoints (name text PRIMARY KEY, position bigint NOT NULL);
            -- The key version each hashed index searches with (see HashedSearchIndexRegistration).
            CREATE UNLOGGED TABLE IF NOT EXISTS search_index_versions (name text PRIMARY KEY, version integer NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);

        return new PostgresSearchIndexStore(connection);
    }

    public async Task<long> CheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var command = Command("SELECT position FROM search_checkpoints WHERE name = @name", ("@name", name));
        return await command.ExecuteScalarAsync(ct) is long position ? position : 0;
    }

    public async Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO search_checkpoints (name, position) VALUES (@name, @position)
            ON CONFLICT (name) DO UPDATE SET position = excluded.position
            """, ("@name", name), ("@position", position));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteCheckpointAsync(string name, CancellationToken ct = default)
    {
        await using var command = Command("DELETE FROM search_checkpoints WHERE name = @name", ("@name", name));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> IndexVersionAsync(string name, CancellationToken ct = default)
    {
        await using var command = Command("SELECT version FROM search_index_versions WHERE name = @name", ("@name", name));
        return await command.ExecuteScalarAsync(ct) is int version ? version : null;
    }

    public async Task SetIndexVersionAsync(string name, int version, CancellationToken ct = default)
    {
        await using var command = Command("""
            INSERT INTO search_index_versions (name, version) VALUES (@name, @version)
            ON CONFLICT (name) DO UPDATE SET version = excluded.version
            """, ("@name", name), ("@version", version));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ScrubAsync(IReadOnlyCollection<string> tables, CancellationToken ct = default)
    {
        // One statement per command: VACUUM refuses to run inside a transaction block, which
        // a multi-statement command would be.
        foreach (var table in tables.Distinct())
        {
            await using var command = Command($"VACUUM FULL {Quote(table)}");
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> DeleteSubjectsAsync(IReadOnlyCollection<string> subjects, CancellationToken ct = default)
    {
        if (subjects.Count == 0) return 0;

        var tables = new List<string>();
        await using (var list = Command("""
            SELECT table_name FROM information_schema.columns
            WHERE table_schema = @schema AND column_name = 'subject'
            ORDER BY table_name
            """, ("@schema", ISearchIndexStore.SchemaName)))
        {
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        }

        var deleted = 0;
        var touched = new List<string>();
        var ids = subjects.ToArray();
        foreach (var table in tables)
        {
            await using var delete = Command($"DELETE FROM {Quote(table)} WHERE subject = ANY(@subjects)", ("@subjects", ids));
            var rows = await delete.ExecuteNonQueryAsync(ct);
            if (rows == 0) continue;
            deleted += rows;
            touched.Add(table);
        }
        await ScrubAsync(touched, ct);
        return deleted;
    }

    private NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, _connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    // A generated index table name is a lowercase identifier (Postgres folds the unquoted name
    // in its CREATE), and information_schema returns it folded, so quoting the folded form
    // matches both.
    private static string Quote(string table) => $"\"{table.ToLowerInvariant().Replace("\"", "\"\"")}\"";

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
