using System.Data.Common;
using DotnetCqrs.ReadModels;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.Tests;

public class WriteGuardTests
{
    private static async Task<string> NewTempDbPathAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-writeguard-{Guid.NewGuid():N}.db");
        // touch it via a real open/close so every test starts from a clean file
        await using var seed = await SqliteReadModelStore.OpenAsync(path);
        return path;
    }

    private static async Task CreateTasksTableAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS tasks (task_id TEXT PRIMARY KEY, title TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTaskAsync(DbConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tasks (task_id, title) VALUES (@id, 'x')";
        command.AddParam("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountTasksAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Direct_writes_are_rejected_without_a_bypass_scope()
    {
        var path = await NewTempDbPathAsync();
        try
        {
            await using var store = await SqliteReadModelStore.OpenAsync(path);
            await CreateTasksTableAsync(store.Connection);
            await store.InstallWriteGuardAsync(["tasks"]);

            var ex = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(store.Connection, "t1"));
            Assert.Contains("direct writes to 'tasks' are disabled", ex.Message);
            Assert.Equal(0, await CountTasksAsync(store.Connection));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Writes_inside_a_bypass_scope_are_allowed_and_the_guard_re_closes_after()
    {
        var path = await NewTempDbPathAsync();
        try
        {
            await using var store = await SqliteReadModelStore.OpenAsync(path);
            await CreateTasksTableAsync(store.Connection);
            await store.InstallWriteGuardAsync(["tasks"]);

            await using (await store.BeginBypassAsync())
            {
                await InsertTaskAsync(store.Connection, "t1");
            }

            Assert.Equal(1, await CountTasksAsync(store.Connection));

            // scope disposed -- guard is back on
            await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(store.Connection, "t2"));
            Assert.Equal(1, await CountTasksAsync(store.Connection));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_different_connection_to_the_same_file_cannot_bypass_the_guard()
    {
        var path = await NewTempDbPathAsync();
        try
        {
            await using var owner = await SqliteReadModelStore.OpenAsync(path);
            await CreateTasksTableAsync(owner.Connection);
            await owner.InstallWriteGuardAsync(["tasks"]);
            await using (await owner.BeginBypassAsync())
                await InsertTaskAsync(owner.Connection, "seeded");

            // A second store never called InstallWriteGuardAsync, so its connection has
            // no writeguard_bypass_active() function -- the persisted trigger fires on
            // its write and fails with "no such function". The guard is a property of
            // the connection that installed it, not app-level discipline.
            await using var other = await SqliteReadModelStore.OpenAsync(path);
            await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(other.Connection, "intruder"));

            // reads are unaffected by the guard -- only writes are denied
            Assert.Equal(1, await CountTasksAsync(other.Connection));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Installing_with_no_tables_guards_nothing()
    {
        var path = await NewTempDbPathAsync();
        try
        {
            await using var store = await SqliteReadModelStore.OpenAsync(path);
            await CreateTasksTableAsync(store.Connection);
            await store.InstallWriteGuardAsync([]);

            await InsertTaskAsync(store.Connection, "t1"); // must not throw
            Assert.Equal(1, await CountTasksAsync(store.Connection));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
