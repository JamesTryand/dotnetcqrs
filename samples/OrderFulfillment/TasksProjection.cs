using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.WriteGuards;
using Microsoft.Data.Sqlite;

namespace OrderFulfillment;

/// <summary>Folds task events into the "tasks" read-model table.</summary>
public sealed class TasksProjection(SqliteConnection connection) : IProjection
{
    public string Name => "tasks";
    public IReadOnlyList<string> Tables => ["tasks"];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                task_id   TEXT PRIMARY KEY,
                title     TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        await using var bypass = await WriteGuard.BeginBypassAsync(connection, ct);

        switch (ev.Type)
        {
            case "TaskCreated":
                var created = JsonSerializer.Deserialize<JsonElement>(ev.Data);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO tasks (task_id, title, completed) VALUES ($id, $title, 0)
                        ON CONFLICT (task_id) DO NOTHING
                        """;
                    command.Parameters.AddWithValue("$id", ev.AggregateId);
                    command.Parameters.AddWithValue("$title", created.GetProperty("title").GetString());
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;

            case "TaskCompleted":
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE tasks SET completed = 1 WHERE task_id = $id";
                    command.Parameters.AddWithValue("$id", ev.AggregateId);
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;
        }
    }
}
