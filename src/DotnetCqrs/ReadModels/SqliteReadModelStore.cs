using System.Collections.Concurrent;
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
///
/// <para><b>Two kinds of connection.</b> <see cref="Connection"/> is the projections' (the one writer). Reads go
/// through <see cref="ReadAsync{T}"/>, which, for a file-backed store, runs each on its own pooled connection:
/// SQLite does not make one connection safe for overlapping commands, and WAL lets those readers run beside the
/// writer. See <see cref="ReadAsync{T}"/>.</para>
/// </summary>
public sealed class SqliteReadModelStore : IReadModelStore
{
    private readonly SqliteConnection _connection;

    // Bypass depth (supports nested BeginBypassAsync scopes). Instance state, not a
    // ConditionalWeakTable keyed on connection identity: this type IS the per-connection
    // owner now.
    private int _bypassDepth;

    // In-memory stores only: reads take turns on the shared connection, since a second connection to a private
    // :memory: database would be a different, empty database. See ReadAsync.
    private readonly SemaphoreSlim _reads = new(1, 1);

    /// <summary>At most this many reads run at once on a file-backed store; the rest wait their turn.</summary>
    public const int MaxConcurrentReads = 8;

    // File-backed stores: the file the read connections open (null for an in-memory store), a limit on how many
    // reads run at once, and the read connections kept for reuse.
    private readonly string? _path;
    private readonly SemaphoreSlim _readSlots = new(MaxConcurrentReads, MaxConcurrentReads);
    private readonly ConcurrentStack<ReadConnection> _idleReads = new();
    private volatile bool _disposed;

    private sealed record ReadConnection(SqliteConnection Connection, int AttachmentCount);

    private SqliteReadModelStore(SqliteConnection connection, string? path)
    {
        _connection = connection;
        _path = path;
    }

    /// <summary>The projections' connection: the one writer. Read through <see cref="ReadAsync{T}"/> instead,
    /// which does not share it (a read on this connection can overlap a write and throw from inside SQLite).</summary>
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

        return new SqliteReadModelStore(connection, IsInMemory(path) ? null : path);
    }

    private static bool IsInMemory(string path) =>
        string.IsNullOrEmpty(path) || path == ":memory:" || path.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Runs a read that may overlap other reads AND the projections' writes: what an HTTP query route, an
    /// authorization check or a batch step does.
    ///
    /// <para><b>File-backed store:</b> the read runs on its own connection, taken from a small pool (at most
    /// <see cref="MaxConcurrentReads"/> at once; more wait). SQLite does not make one <see cref="SqliteConnection"/>
    /// safe for overlapping commands, so a read sharing <see cref="Connection"/> with a projection's write could
    /// throw from inside SQLite (found: the legacy migration failing with ArgumentOutOfRangeException from
    /// SqliteCommand.Dispose; before that, 20 overlapping query requests made a route return 500 about one run in
    /// twenty). WAL lets these readers run beside the one writer, and a read sees what has been committed.
    /// The connection is <c>query_only</c>: a write through it fails.</para>
    ///
    /// <para><b>Attached databases</b> (the search index, <c>ATTACH DATABASE ... AS search</c>) exist per connection,
    /// so each read connection is given the ones attached to <see cref="Connection"/> through
    /// <see cref="SqliteSearchIndexStore.AttachAsync"/>; a pooled connection that predates a later attach is
    /// replaced.</para>
    ///
    /// <para><b>In-memory store:</b> a second connection would be a different, empty database, so reads take turns
    /// on the shared connection, as before. That does not cover a write overlapping a read, but an in-memory store
    /// is for tests and tools, not a live host.</para>
    ///
    /// <para>A read that throws has its connection discarded, not reused.</para>
    /// </summary>
    public async Task<T> ReadAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        if (_path is null)
        {
            await _reads.WaitAsync(ct);
            try { return await read(_connection, ct); }
            finally { _reads.Release(); }
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _readSlots.WaitAsync(ct);
        ReadConnection? lease = null;
        try
        {
            lease = await RentReadConnectionAsync(ct);
            var result = await read(lease.Connection, ct);
            var done = lease;
            lease = null;
            await ReturnReadConnectionAsync(done);
            return result;
        }
        finally
        {
            if (lease is not null) await lease.Connection.DisposeAsync();
            _readSlots.Release();
        }
    }

    private async Task<ReadConnection> RentReadConnectionAsync(CancellationToken ct)
    {
        var attachments = ReadConnectionAttachments.For(_connection);
        while (_idleReads.TryPop(out var idle))
        {
            if (idle.AttachmentCount == attachments.Count && idle.Connection.State == System.Data.ConnectionState.Open)
                return idle;
            await idle.Connection.DisposeAsync(); // opened before a later ATTACH: it cannot see that database
        }

        var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        try
        {
            await connection.OpenAsync(ct);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA query_only = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
            foreach (var (schema, path) in attachments)
            {
                await using var attach = connection.CreateCommand();
                attach.CommandText = $"ATTACH DATABASE @path AS {schema}";
                attach.Parameters.AddWithValue("@path", path);
                await attach.ExecuteNonQueryAsync(ct);
            }
            return new ReadConnection(connection, attachments.Count);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ReturnReadConnectionAsync(ReadConnection lease)
    {
        if (_disposed) await lease.Connection.DisposeAsync();
        else _idleReads.Push(lease);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        while (_idleReads.TryPop(out var idle)) await idle.Connection.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
