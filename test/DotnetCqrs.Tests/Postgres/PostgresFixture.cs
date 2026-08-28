using DotnetCqrs.Postgres;
using Npgsql;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>
/// Shared setup for the Postgres backend tests. They run only when
/// <c>DOTNETCQRS_PG</c> names a reachable database (a privileged role — superuser, or
/// <c>CREATEROLE</c> — because the write-guard test creates throwaway login roles);
/// when it is unset every Postgres test is <b>skipped</b>, not silently passed.
///
/// <para>Each <see cref="NewSchemaAsync"/> call makes a uniquely-named schema so test
/// classes never collide, and <see cref="DisposeAsync"/> drops every schema and role it
/// handed out. See <c>ops/postgres/</c> for a one-command local Postgres.</para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string RolePassword = "dotnetcqrs-test";

    private readonly string? _baseConnectionString = Environment.GetEnvironmentVariable("DOTNETCQRS_PG");
    private readonly List<string> _schemas = [];
    private readonly List<string> _roles = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlDataSource? _admin;

    public bool Available => _baseConnectionString is not null;

    /// <summary>The raw <c>DOTNETCQRS_PG</c> value — no schema override, so a store
    /// opened on it lands in the default search path (<c>public</c>), the way an
    /// ordinary consumer's connection string does.</summary>
    public string BaseConnectionString => _baseConnectionString!;

    /// <summary>Reason string for <c>Skip.If</c> — non-null exactly when the tests must skip.</summary>
    public string? SkipReason => Available ? null : "DOTNETCQRS_PG is not set (no reachable Postgres)";

    public async Task InitializeAsync()
    {
        if (!Available) return;
        _admin = NpgsqlDataSource.Create(_baseConnectionString!);
        // Fail loudly here rather than mid-test if the connection string is bad.
        await using var probe = await _admin.OpenConnectionAsync();
    }

    /// <summary>Creates a fresh schema and returns a connection string whose search path
    /// points at it — hand this to <c>PostgresEventStore.OpenAsync</c> /
    /// <c>PostgresReadModelStore.OpenAsync</c>.</summary>
    public async Task<string> NewSchemaAsync()
    {
        var schema = "s_" + Guid.NewGuid().ToString("N");
        await _gate.WaitAsync();
        try
        {
            await using var cmd = _admin!.CreateCommand($"CREATE SCHEMA \"{schema}\"");
            await cmd.ExecuteNonQueryAsync();
            _schemas.Add(schema);
        }
        finally { _gate.Release(); }

        return new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;
    }

    /// <summary>Creates a throwaway <c>LOGIN</c> role with no privileges beyond
    /// <c>CONNECT</c> and <c>USAGE</c> on <paramref name="schemaConnectionString"/>'s
    /// schema, and returns a connection string that authenticates as it — the Postgres
    /// analogue of "a different connection to the same file".</summary>
    public async Task<string> NewUnprivilegedRoleAsync(string schemaConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(schemaConnectionString);
        var role = "r_" + Guid.NewGuid().ToString("N");

        await _gate.WaitAsync();
        try
        {
            await using var conn = await _admin!.OpenConnectionAsync();
            await using (var create = conn.CreateCommand())
            {
                create.CommandText = $"""
                    CREATE ROLE "{role}" LOGIN PASSWORD '{RolePassword}';
                    GRANT CONNECT ON DATABASE "{builder.Database}" TO "{role}";
                    GRANT USAGE ON SCHEMA "{builder.SearchPath}" TO "{role}";
                    """;
                await create.ExecuteNonQueryAsync();
            }
            _roles.Add(role);
        }
        finally { _gate.Release(); }

        return new NpgsqlConnectionStringBuilder(schemaConnectionString)
        {
            Username = role,
            Password = RolePassword,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_admin is null) return;

        await using (var conn = await _admin.OpenConnectionAsync())
        {
            foreach (var schema in _schemas)
                await Exec(conn, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            foreach (var role in _roles)
            {
                await Exec(conn, $"DROP OWNED BY \"{role}\"");
                await Exec(conn, $"DROP ROLE IF EXISTS \"{role}\"");
            }
        }

        await _admin.DisposeAsync();
        _gate.Dispose();

        static async Task Exec(NpgsqlConnection conn, string sql)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* best-effort teardown */ }
        }
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
