using System.Data.Common;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

public class ProjectionTests
{
    // A minimal projection — folds task events into a "tasks" read-model table —
    // used only to exercise the IProjection/ConsumerEngine pattern; not a shipped
    // domain example (that's Milestone 8's worked example).
    private sealed class TasksProjection(IReadModelStore store) : IProjection
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

        public async Task ResetAsync(CancellationToken ct = default)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = "DELETE FROM tasks";
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            switch (ev.Type)
            {
                case "TaskCreated":
                    var created = JsonSerializer.Deserialize<JsonElement>(ev.Data);
                    await using (var command = store.Connection.CreateCommand())
                    {
                        // ON CONFLICT DO NOTHING makes this idempotent under at-least-once
                        // redelivery, without pocketcqrs's PocketBase-driven find-then-check
                        // (see events/decider.go's own Apply) -- SQLite's native upsert covers it.
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
                        // A no-op if the row doesn't exist yet (out-of-order replay) --
                        // matches pocketcqrs's tasksProjection guard for the same case.
                        command.CommandText = "UPDATE tasks SET completed = 1 WHERE task_id = @id";
                        command.AddParam("@id", ev.AggregateId);
                        await command.ExecuteNonQueryAsync(ct);
                    }
                    break;
            }
        }
    }

    private static Decider<(bool Exists, bool Completed)> TaskDecider() => new()
    {
        InitialState = () => (false, false),
        Decide = (state, cmd) => cmd.Name switch
        {
            "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
            "CompleteTask" => [new NewEvent("TaskCompleted", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (state, ev) => ev.Type switch
        {
            "TaskCreated" => (true, state.Completed),
            "TaskCompleted" => (state.Exists, true),
            _ => state,
        },
    };

    private static async Task<(long Id, string Title, bool Completed)?> FindTaskAsync(DbConnection connection, string taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, completed FROM tasks WHERE task_id = @id";
        command.AddParam("@id", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (0, reader.GetString(0), reader.GetInt64(1) != 0);
    }

    [Fact]
    public async Task Projection_materializes_a_task_lifecycle_and_the_result_is_queryable_with_plain_SQL()
    {
        await using var eventStore = await SqliteEventStore.OpenAsync(":memory:");
        await using var readModel = await SqliteReadModelStore.OpenAsync(":memory:");

        var registry = new DeciderRegistry(eventStore);
        registry.Register("task", TaskDecider());

        var projection = new TasksProjection(readModel);
        await projection.InitAsync();
        var engine = new ConsumerEngine(eventStore, eventStore);
        engine.Register(projection);

        await registry.HandleAsync("task", "t1", new Command("CreateTask", """{"title":"write the docs"}"""));
        await registry.HandleAsync("task", "t1", new Command("CompleteTask", "{}"));
        await engine.RunOnceAsync();

        var task = await FindTaskAsync(readModel.Connection, "t1");
        Assert.NotNull(task);
        Assert.Equal("write the docs", task!.Value.Title);
        Assert.True(task.Value.Completed);
    }

    [Fact]
    public async Task Apply_is_idempotent_under_redelivery_of_the_same_event()
    {
        await using var eventStore = await SqliteEventStore.OpenAsync(":memory:");
        await using var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new TasksProjection(readModel);
        await projection.InitAsync();

        var appended = await eventStore.AppendAsync("task", "t1", 0,
            [new NewEvent("TaskCreated", """{"title":"a"}""")]);

        await projection.ApplyAsync(appended[0], CancellationToken.None);
        await projection.ApplyAsync(appended[0], CancellationToken.None); // redelivered

        await using var count = readModel.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM tasks WHERE task_id = 't1'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Resetting_the_checkpoint_and_wiping_the_table_rebuilds_identical_state()
    {
        await using var eventStore = await SqliteEventStore.OpenAsync(":memory:");
        await using var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(eventStore);
        registry.Register("task", TaskDecider());

        var projection = new TasksProjection(readModel);
        await projection.InitAsync();
        var engine = new ConsumerEngine(eventStore, eventStore);
        engine.Register(projection);

        await registry.HandleAsync("task", "t1", new Command("CreateTask", """{"title":"write the docs"}"""));
        await registry.HandleAsync("task", "t1", new Command("CompleteTask", "{}"));
        await engine.RunOnceAsync();
        var before = await FindTaskAsync(readModel.Connection, "t1");

        // "you don't migrate the row -- you fix the code and rebuild the projection"
        await projection.ResetAsync();
        await eventStore.SaveCheckpointAsync("tasks", 0);
        await engine.RunOnceAsync();
        var after = await FindTaskAsync(readModel.Connection, "t1");

        Assert.Equal(before, after);
    }
}
