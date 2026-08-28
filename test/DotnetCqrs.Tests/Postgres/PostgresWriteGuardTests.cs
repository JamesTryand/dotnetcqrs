using DotnetCqrs.Postgres;
using DotnetCqrs.ReadModels;
using Npgsql;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>
/// The Postgres write-guard is the database's grant system, not connection-scoped
/// triggers, so it does <b>not</b> port <see cref="WriteGuardTests"/> statement for
/// statement — the behavioural difference is deliberate and documented in
/// <c>docs/postgres-backend.md</c>:
/// <list type="bullet">
///   <item>SQLite blocks the owning connection too, unless inside a bypass scope;
///     Postgres never blocks the table owner, so <c>BeginBypassAsync</c> is a no-op and
///     projection bodies are byte-identical across providers.</item>
///   <item>An out-of-band writer is refused by the database with a privilege error
///     (SQLSTATE 42501), not a trigger <c>RAISE</c>.</item>
/// </list>
/// </summary>
[Collection("postgres")]
public class PostgresWriteGuardTests(PostgresFixture fx)
{
    private static async Task CreateTasksTableAsync(IReadModelStore store)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS tasks (task_id text PRIMARY KEY, title text NOT NULL)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTaskAsync(IReadModelStore store, string id)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = "INSERT INTO tasks (task_id, title) VALUES (@id, 'x')";
        command.AddParam("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Owner_writes_are_never_blocked_by_the_guard_so_no_bypass_scope_is_needed()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresReadModelStore.OpenAsync(await fx.NewSchemaAsync());
        await CreateTasksTableAsync(store);
        await store.InstallWriteGuardAsync(["tasks"]);

        // No BeginBypassAsync here -- the owning role keeps full rights on tables it
        // created. (SqliteReadModelStore would reject this write outside a bypass scope.)
        await InsertTaskAsync(store, "t1");

        await using var count = store.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tasks";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task An_unprivileged_role_cannot_write_the_guarded_table_but_can_still_read_it()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var schemaConnectionString = await fx.NewSchemaAsync();
        var intruderConnectionString = await fx.NewUnprivilegedRoleAsync(schemaConnectionString);
        var intruderRole = new NpgsqlConnectionStringBuilder(intruderConnectionString).Username!;

        await using var store = await PostgresReadModelStore.OpenAsync(schemaConnectionString, intruderRole);
        await CreateTasksTableAsync(store);
        await store.InstallWriteGuardAsync(["tasks"]); // grants SELECT (only) to the intruder role
        await InsertTaskAsync(store, "seeded");

        await using var intruder = new NpgsqlConnection(intruderConnectionString);
        await intruder.OpenAsync();

        // reads: allowed via the reader grant
        await using (var read = intruder.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM tasks";
            Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
        }

        // writes: refused by the database
        foreach (var dml in new[]
        {
            "INSERT INTO tasks (task_id, title) VALUES ('x', 'x')",
            "UPDATE tasks SET title = 'y' WHERE task_id = 'seeded'",
            "DELETE FROM tasks WHERE task_id = 'seeded'",
        })
        {
            await using var write = intruder.CreateCommand();
            write.CommandText = dml;
            var ex = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // insufficient_privilege
        }

        await using (var stillThere = store.Connection.CreateCommand())
        {
            stillThere.CommandText = "SELECT COUNT(*) FROM tasks";
            Assert.Equal(1L, (long)(await stillThere.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task BeginBypassAsync_is_a_usable_no_op_scope()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresReadModelStore.OpenAsync(await fx.NewSchemaAsync());
        await CreateTasksTableAsync(store);
        await store.InstallWriteGuardAsync(["tasks"]);

        await using (await store.BeginBypassAsync())
        await using (await store.BeginBypassAsync()) // nests
        {
            await InsertTaskAsync(store, "inside");
        }

        await InsertTaskAsync(store, "after"); // still fine -- scope was a no-op

        await using var count = store.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tasks";
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Installing_with_no_tables_guards_nothing()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresReadModelStore.OpenAsync(await fx.NewSchemaAsync());
        await CreateTasksTableAsync(store);
        await store.InstallWriteGuardAsync([]);

        await InsertTaskAsync(store, "t1"); // must not throw

        await using var count = store.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tasks";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }
}
