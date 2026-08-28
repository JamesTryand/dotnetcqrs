using System.Data.Common;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="ProjectionTests"/> — the same
/// <c>IProjection</c> / <c>ConsumerEngine</c> lifecycle, driven against a
/// <see cref="PostgresReadModelStore"/>. The projection body is provider-neutral
/// (<see cref="DbConnection"/>, <c>@</c> params, <c>AddParam</c>); only the read-model
/// DDL is Postgres-flavoured (<c>boolean</c>, not <c>INTEGER</c> 0/1).</summary>
[Collection("postgres")]
public class PostgresProjectionTests(PostgresFixture fx)
{
    private sealed class TasksProjection(IReadModelStore store) : IProjection
    {
        public string Name => "tasks";
        public IReadOnlyList<string> Tables => ["tasks"];

        public async Task InitAsync(CancellationToken ct = default)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tasks (
                    task_id   text PRIMARY KEY,
                    title     text NOT NULL,
                    completed boolean NOT NULL DEFAULT false
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
                        command.CommandText = """
                            INSERT INTO tasks (task_id, title, completed) VALUES (@id, @title, false)
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
                        command.CommandText = "UPDATE tasks SET completed = true WHERE task_id = @id";
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

    private static async Task<(string Title, bool Completed)?> FindTaskAsync(DbConnection connection, string taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title, completed FROM tasks WHERE task_id = @id";
        command.AddParam("@id", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetBoolean(1));
    }

    private async Task<(PostgresEventStore Events, PostgresReadModelStore ReadModel)> OpenAsync()
        => (await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync()),
            await PostgresReadModelStore.OpenAsync(await fx.NewSchemaAsync()));

    [SkippableFact]
    public async Task Projection_materializes_a_task_lifecycle_and_the_result_is_queryable_with_plain_SQL()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (eventStore, readModel) = await OpenAsync();
        await using var _ = eventStore;
        await using var __ = readModel;

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

    [SkippableFact]
    public async Task Apply_is_idempotent_under_redelivery_of_the_same_event()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (eventStore, readModel) = await OpenAsync();
        await using var _ = eventStore;
        await using var __ = readModel;

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

    [SkippableFact]
    public async Task Resetting_the_checkpoint_and_wiping_the_table_rebuilds_identical_state()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var (eventStore, readModel) = await OpenAsync();
        await using var _ = eventStore;
        await using var __ = readModel;

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

        await projection.ResetAsync();
        await eventStore.SaveCheckpointAsync("tasks", 0);
        await engine.RunOnceAsync();
        var after = await FindTaskAsync(readModel.Connection, "t1");

        Assert.Equal(before, after);
    }
}
