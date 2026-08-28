using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// SQLite implementation of <see cref="IReadModelStore"/>: one dedicated connection
/// for the store's lifetime, opened with the same operational pragmas as the event
/// store (WAL, busy timeout). Schema is owned entirely by whichever projection(s)
/// target this database — nothing is created here, and nothing here is ever the
/// source of truth (read models are ordinary, freely rewritable/rebuildable tables).
///
/// <para>The write-guard is enforced with plain SQLite triggers plus a
/// connection-scoped SQL function (<c>writeguard_bypass_active()</c>): a persistent
/// trigger cannot reference a TEMP table, so a per-connection function is the
/// mechanism. Only the connection that called <see cref="InstallWriteGuardAsync"/>
/// has that function registered — any other connection, including a fresh one against
/// the same file, fails with "no such function" the moment its own write fires the
/// trigger. That is the direct analogue of pocketcqrs's per-request context marker
/// that "never crosses the API boundary." Milestone 7's Postgres store replaces this
/// with a least-privilege role and makes <see cref="BeginBypassAsync"/> a no-op.</para>
/// </summary>
public sealed class SqliteReadModelStore : IReadModelStore
{
    private readonly SqliteConnection _connection;

    // Bypass depth (supports nested BeginBypassAsync scopes). Instance state, not a
    // ConditionalWeakTable keyed on connection identity: this type IS the per-connection
    // owner now.
    private int _bypassDepth;

    private SqliteReadModelStore(SqliteConnection connection) => _connection = connection;

    public DbConnection Connection => _connection;

    /// <summary>Opens (creating the file if necessary) the read-model database at
    /// <paramref name="path"/>. The composition root names this concrete type; projections
    /// only ever see <see cref="IReadModelStore"/>.</summary>
    public static async Task<SqliteReadModelStore> OpenAsync(string path, CancellationToken ct = default)
    {
        // Pooling=False: a pooled connection keeps the OS file handle open past
        // DisposeAsync -- see SqliteEventStore.OpenAsync's identical comment.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(ct);

        return new SqliteReadModelStore(connection);
    }

    public Task InstallWriteGuardAsync(IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return Task.CompletedTask;

        _connection.CreateFunction("writeguard_bypass_active", () => _bypassDepth > 0);
        return InstallTriggersAsync(tables, ct);
    }

    private async Task InstallTriggersAsync(IReadOnlyList<string> tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS writeguard_{table}_{op.ToLowerInvariant()}
                    BEFORE {op} ON {table}
                    WHEN writeguard_bypass_active() = 0
                    BEGIN
                        SELECT RAISE(ABORT, 'direct writes to ''{table}'' are disabled; state changes must go through a command/decider');
                    END
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public ValueTask<IAsyncDisposable> BeginBypassAsync(CancellationToken ct = default)
    {
        _bypassDepth++;
        return ValueTask.FromResult<IAsyncDisposable>(new BypassScope(this));
    }

    private sealed class BypassScope(SqliteReadModelStore store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            store._bypassDepth--;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
