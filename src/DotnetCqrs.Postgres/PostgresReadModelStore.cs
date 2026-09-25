using System.Data.Common;
using DotnetCqrs.ReadModels;
using Npgsql;

namespace DotnetCqrs.Postgres;

/// <summary>
/// The Postgres implementation of <see cref="IReadModelStore"/>: one dedicated
/// connection for the store's lifetime (a projection is a single sequential consumer,
/// so this — not a pool — is the right shape, same as <c>SqliteReadModelStore</c>).
/// Schema is owned entirely by whichever projection(s) target this database; nothing is
/// created here and nothing here is ever the source of truth.
///
/// <para><b>The write-guard is the database's own grant system, not a trigger.</b>
/// The projection's <c>InitAsync</c> runs <c>CREATE TABLE</c> on this store's
/// connection, so the connecting role owns the read-model tables and keeps full rights
/// on them as owner. <see cref="InstallWriteGuardAsync"/> makes the "nobody else writes"
/// policy explicit by revoking write privileges from <c>PUBLIC</c> (and optionally
/// granting <c>SELECT</c> to a designated read-only role). Any other role — a stray
/// <c>psql</c> session, app code taking a shortcut — simply has no <c>INSERT</c>/
/// <c>UPDATE</c>/<c>DELETE</c> privilege and is refused by the database. That makes
/// <see cref="BeginBypassAsync"/> a genuine no-op here: the store's own connection is
/// always the owner, so projection bodies are byte-identical across providers.</para>
///
/// <para><b>Concurrent reads get their own connections.</b> The dedicated connection
/// serves one command at a time (Npgsql refuses an overlapping one), so
/// <see cref="ReadAsync"/> -- the query routes' path -- takes a pooled connection from a
/// data source on the same connection string (same database, same search path) instead
/// of queueing behind the projections.</para>
/// </summary>
public sealed class PostgresReadModelStore : IReadModelStore
{
    private static readonly IAsyncDisposable NoopBypass = new NoopScope();

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlDataSource _reads;
    private readonly string? _readerRole;

    private PostgresReadModelStore(NpgsqlConnection connection, NpgsqlDataSource reads, string? readerRole)
    {
        _connection = connection;
        _reads = reads;
        _readerRole = readerRole;
    }

    public DbConnection Connection => _connection;

    /// <summary>Opens the read-model database named by <paramref name="connectionString"/>.
    /// The composition root names this concrete type; projections only ever see
    /// <see cref="IReadModelStore"/>.</summary>
    public static Task<PostgresReadModelStore> OpenAsync(string connectionString, CancellationToken ct = default)
        => OpenAsync(connectionString, readerRole: null, ct);

    /// <summary>As <see cref="OpenAsync(string, CancellationToken)"/>, but
    /// <see cref="InstallWriteGuardAsync"/> will also <c>GRANT SELECT</c> on the guarded
    /// tables to <paramref name="readerRole"/> — an existing Postgres role a read-only
    /// consumer (a dashboard, a reporting job) connects as.</summary>
    public static async Task<PostgresReadModelStore> OpenAsync(string connectionString, string? readerRole, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return new PostgresReadModelStore(connection, NpgsqlDataSource.Create(connectionString), readerRole);
    }

    /// <summary>Opens the store in schema <paramref name="schema"/> of the database named by
    /// <paramref name="connectionString"/>, creating the schema if necessary. The connection's
    /// search path is set to it, so the projections' tables land there. A generated host uses
    /// this to keep read models apart from the event store's tables in a shared database: a
    /// read model called <c>events</c> would otherwise collide with the log itself.</summary>
    public static async Task<PostgresReadModelStore> OpenInSchemaAsync(string connectionString, string schema, CancellationToken ct = default)
    {
        await using (var setup = new NpgsqlConnection(connectionString))
        {
            await setup.OpenAsync(ct);
            await using var create = setup.CreateCommand();
            create.CommandText = $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)}";
            await create.ExecuteNonQueryAsync(ct);
        }
        return await OpenAsync(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString, ct);
    }

    /// <summary>Revokes write privileges on <paramref name="tables"/> from <c>PUBLIC</c>
    /// (Postgres grants none by default — this makes the policy explicit and covers a
    /// database that has loosened it), and grants <c>SELECT</c> to the reader role if one
    /// was supplied to <see cref="OpenAsync(string, string?, CancellationToken)"/>. The
    /// owning role — this store's connection — is unaffected and still writes freely,
    /// which is why <see cref="BeginBypassAsync"/> need do nothing. An empty list guards
    /// nothing.
    ///
    /// <para>The names in <paramref name="tables"/> are unqualified and are resolved
    /// through this connection's <c>search_path</c> — the same schema the projection's
    /// <c>InitAsync</c> created the tables in (whatever the connection string's
    /// <c>Search Path</c> selects, or <c>public</c> by default). A name that does not
    /// resolve there surfaces as a <c>PostgresException</c> (<c>42P01</c>) rather than
    /// silently guarding nothing.</para>
    ///
    /// <para>A name resolves the way it would written unquoted in the projection's own SQL
    /// (<c>to_regclass</c>): <c>orderSummary</c> finds the table <c>CREATE TABLE orderSummary</c>
    /// made, which Postgres folded to <c>ordersummary</c>. Quoting the name as given would miss
    /// it.</para></summary>
    public async Task InstallWriteGuardAsync(IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        foreach (var table in tables)
        {
            // Unresolvable: fall back to the quoted name, so the REVOKE raises 42P01.
            var quoted = await ResolveTableAsync(table, ct) ?? Quote(table);
            await ExecuteAsync($"REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON {quoted} FROM PUBLIC", ct);
            if (_readerRole is not null)
                await ExecuteAsync($"GRANT SELECT ON {quoted} TO {Quote(_readerRole)}", ct);
        }
    }

    public ValueTask<IAsyncDisposable> BeginBypassAsync(CancellationToken ct = default)
        => ValueTask.FromResult(NoopBypass);

    public async Task<T> ReadAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read, CancellationToken ct = default)
    {
        await using var connection = await _reads.OpenConnectionAsync(ct);
        return await read(connection, ct);
    }

    private async Task<string?> ResolveTableAsync(string table, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table)::text";
        command.AddParam("@table", table);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    // Identifier quoting for a table/role name spliced into DDL (GRANT/REVOKE take no
    // parameters). Doubles any embedded quote; the surrounding double-quotes make it a
    // single quoted identifier.
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed class NoopScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _reads.DisposeAsync();
    }
}
