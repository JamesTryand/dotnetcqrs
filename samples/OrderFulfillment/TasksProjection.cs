using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace OrderFulfillment;

/// <summary>Folds task events into the "tasks" read-model table.</summary>
public sealed class TasksProjection(IReadModelStore store) : IProjection
{
    public string Name => "tasks";
    public IReadOnlyList<string> Tables => ["tasks"];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = store.Connection.CreateCommand();
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
        await using var bypass = await store.BeginBypassAsync(ct);

        switch (ev.Type)
        {
            case "TaskCreated":
                var created = JsonSerializer.Deserialize<JsonElement>(ev.Data);
                await using (var command = store.Connection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO tasks (task_id, title, completed) VALUES (@id, @title, 0)
                        ON CONFLICT (task_id) DO NOTHING
                        """;
                    command.AddParam("@id", ev.AggregateId);
                    command.AddParam("@title", created.GetProperty("title").GetString());
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;

            case "TaskCompleted":
                await using (var command = store.Connection.CreateCommand())
                {
                    command.CommandText = "UPDATE tasks SET completed = 1 WHERE task_id = @id";
                    command.AddParam("@id", ev.AggregateId);
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;
        }
    }
}
