using DotnetCqrs.ReadModels;
using DotnetCqrs.WriteGuards;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.Tests;

public class WriteGuardTests
{
    private static async Task<string> NewTempDbPathAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-writeguard-{Guid.NewGuid():N}.db");
        // touch it via a real open/close so every test starts from a clean file
        await using var seed = await ReadModelDb.OpenAsync(path);
        return path;
    }

    private static async Task CreateTasksTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS tasks (task_id TEXT PRIMARY KEY, title TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTaskAsync(SqliteConnection connection, string id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tasks (task_id, title) VALUES ($id, 'x')";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountTasksAsync(SqliteConnection connection)
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
            await using var connection = await ReadModelDb.OpenAsync(path);
            await CreateTasksTableAsync(connection);
            await WriteGuard.InstallAsync(connection, ["tasks"]);

            var ex = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(connection, "t1"));
            Assert.Contains("direct writes to 'tasks' are disabled", ex.Message);
            Assert.Equal(0, await CountTasksAsync(connection));
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
            await using var connection = await ReadModelDb.OpenAsync(path);
            await CreateTasksTableAsync(connection);
            await WriteGuard.InstallAsync(connection, ["tasks"]);

            await using (await WriteGuard.BeginBypassAsync(connection))
            {
                await InsertTaskAsync(connection, "t1");
            }

            Assert.Equal(1, await CountTasksAsync(connection));

            // scope disposed -- guard is back on
            await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(connection, "t2"));
            Assert.Equal(1, await CountTasksAsync(connection));
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
            await using var owner = await ReadModelDb.OpenAsync(path);
            await CreateTasksTableAsync(owner);
            await WriteGuard.InstallAsync(owner, ["tasks"]);
            await using (await WriteGuard.BeginBypassAsync(owner))
                await InsertTaskAsync(owner, "seeded");

            await using var other = await ReadModelDb.OpenAsync(path);
            await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(other, "intruder"));

            // reads are unaffected by the guard -- only writes are denied
            Assert.Equal(1, await CountTasksAsync(other));
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
            await using var connection = await ReadModelDb.OpenAsync(path);
            await CreateTasksTableAsync(connection);
            await WriteGuard.InstallAsync(connection, []);

            await InsertTaskAsync(connection, "t1"); // must not throw
            Assert.Equal(1, await CountTasksAsync(connection));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
