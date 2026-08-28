using System.Data.Common;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.Tests;

/// <summary>
/// dotnetcqrs-multi-node Milestone 1: a same-host secondary opens the master's real
/// events.db read-only and runs its own local projections against it -- the "single
/// master, multiple read models" story, with no replication tooling, that
/// journal_mode=WAL concurrent readers already make safe. Uses real files on disk
/// (not :memory:), since the whole point is two independent SqliteEventStore
/// connections seeing the same file the way two separate processes would.
/// </summary>
public sealed class ReadOnlyReplicaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-replica-{Guid.NewGuid():N}");

    public ReadOnlyReplicaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // A minimal projection folding task events into a "tasks" read-model table --
    // same shape as ProjectionTests' own fixture, kept local to this file rather than
    // shared, matching this project's existing per-file-fixture convention.
    private sealed class TasksProjection(IReadModelStore store) : IProjection
    {
        public string Name => "tasks";
        public IReadOnlyList<string> Tables => ["tasks"];

        public async Task InitAsync(CancellationToken ct = default)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS tasks (task_id TEXT PRIMARY KEY, title TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync(ct);
        }

        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            if (ev.Type != "TaskCreated") return;
            var data = JsonSerializer.Deserialize<JsonElement>(ev.Data);
            await using var command = store.Connection.CreateCommand();
            command.CommandText = "INSERT INTO tasks (task_id, title) VALUES (@id, @title) ON CONFLICT (task_id) DO NOTHING";
            command.AddParam("@id", ev.AggregateId);
            command.AddParam("@title", data.GetProperty("title").GetString());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (_, cmd) => [new NewEvent("TaskCreated", cmd.Payload)],
        Evolve = (_, _) => true,
    };

    private static async Task<string?> FindTaskTitleAsync(DbConnection connection, string taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM tasks WHERE task_id = @id";
        command.AddParam("@id", taskId);
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task A_secondary_replays_the_masters_writes_into_its_own_local_read_model()
    {
        var eventsPath = Path.Combine(_dir, "events.db");
        var checkpointsPath = Path.Combine(_dir, "checkpoints.db");

        await using var master = await SqliteEventStore.OpenAsync(eventsPath);
        var registry = new DeciderRegistry(master);
        registry.Register("task", TaskDecider());
        await registry.HandleAsync("task", "t1", new Command("CreateTask", """{"title":"write the docs"}"""));

        // The secondary: a read-only handle on the SAME file (same-host, no
        // replication tooling), plus a SEPARATE, ordinary writable store for its own
        // checkpoints (its own consumer position can't live in a store that rejects
        // writes), plus its own local read model.
        await using var replica = await SqliteEventStore.OpenReadOnlyAsync(eventsPath);
        await using var secondaryCheckpoints = await SqliteEventStore.OpenAsync(checkpointsPath);
        await using var readModel = await SqliteReadModelStore.OpenAsync(":memory:");

        var projection = new TasksProjection(readModel);
        await projection.InitAsync();
        var engine = new ConsumerEngine(replica, secondaryCheckpoints);
        engine.Register(projection);

        await engine.RunOnceAsync();
        Assert.Equal("write the docs", await FindTaskTitleAsync(readModel.Connection, "t1"));

        // Prove this is genuine ongoing replication, not a one-shot snapshot taken at
        // open time: the master appends AFTER the secondary already opened its
        // read-only handle, and a second catch-up pass must still see it.
        await registry.HandleAsync("task", "t2", new Command("CreateTask", """{"title":"ship it"}"""));
        await engine.RunOnceAsync();
        Assert.Equal("ship it", await FindTaskTitleAsync(readModel.Connection, "t2"));
    }

    [Fact]
    public async Task A_replica_refuses_every_write_method_instead_of_surfacing_a_raw_sqlite_error()
    {
        var eventsPath = Path.Combine(_dir, "events.db");
        await using (var master = await SqliteEventStore.OpenAsync(eventsPath))
            await master.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);

        await using var replica = await SqliteEventStore.OpenReadOnlyAsync(eventsPath);

        await Assert.ThrowsAsync<ReadOnlyStoreException>(() =>
            replica.AppendAsync("task", "t2", 0, [new NewEvent("TaskCreated", "{}")]));
        await Assert.ThrowsAsync<ReadOnlyStoreException>(() => replica.SaveCheckpointAsync("tasks", 1));
        var stream = await replica.LoadStreamAsync("task", "t1");
        await Assert.ThrowsAsync<ReadOnlyStoreException>(() => replica.AddDeadLetterAsync("tasks", stream[0], "boom"));
        await Assert.ThrowsAsync<ReadOnlyStoreException>(() => replica.ResolveDeadLetterAsync(1));
    }

    [Fact]
    public async Task Opening_a_missing_file_read_only_throws_and_creates_nothing()
    {
        var path = Path.Combine(_dir, "does-not-exist.db");

        await Assert.ThrowsAsync<SqliteException>(() => SqliteEventStore.OpenReadOnlyAsync(path));

        Assert.False(File.Exists(path));
    }
}
