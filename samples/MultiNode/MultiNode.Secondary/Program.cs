// dotnetcqrs-multi-node Milestone 3: the read + forwarding side of the cross-host
// replication smoke test. Opens the SAME events.db path LiteFS replicates into this
// host's own FUSE mount -- Milestone 1's SqliteEventStore.OpenReadOnlyAsync, byte for
// byte unchanged. The only thing that differs between "same host" (Milestone 1) and
// "cross host" (this milestone) is how the shared file gets onto this filesystem;
// LiteFS owns that, this app has no LiteFS-specific code at all. Writes are forwarded
// to the primary over a real network hop via Milestone 2's
// CqrsGatewayEndpoints.ForwardTo, also unchanged. See docs/cross-host-replication.md.
using System.Data.Common;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

var eventsPath = Environment.GetEnvironmentVariable("EVENTS_DB_PATH") ?? "/litefs/events.db";
var checkpointsPath = Environment.GetEnvironmentVariable("CHECKPOINTS_DB_PATH") ?? "/data/checkpoints.db";
var readModelPath = Environment.GetEnvironmentVariable("READMODEL_DB_PATH") ?? "/data/readmodel.db";
var primaryUrl = Environment.GetEnvironmentVariable("PRIMARY_URL") ?? "http://primary:8080";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(checkpointsPath))!);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(readModelPath))!);

var replica = await SqliteEventStore.OpenReadOnlyAsync(eventsPath);
var checkpoints = await SqliteEventStore.OpenAsync(checkpointsPath);
var readModel = await SqliteReadModelStore.OpenAsync(readModelPath);
var projection = new TasksProjection(readModel);
await projection.InitAsync();

var engine = new ConsumerEngine(replica, checkpoints);
engine.Register(projection);

var forwardClient = new HttpClient { BaseAddress = new Uri(primaryUrl) };

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
// MapCqrsGateway still resolves a DeciderRegistry from DI even on the forward path
// (it's just never read there) -- the checkpoints store is a convenient real
// SqliteEventStore to back it, matching CqrsGatewayForwardingTests' own established
// shape; a real secondary never decides with it.
builder.Services.AddSingleton(new DeciderRegistry(checkpoints));

var app = builder.Build();
var replicationCts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => replicationCts.Cancel());
_ = engine.StartAsync(replicationCts.Token);

app.MapCqrsGateway(forward: CqrsGatewayEndpoints.ForwardTo(forwardClient));
app.MapGet("/tasks/{id}", async (string id) =>
{
    var title = await FindTaskTitleAsync(readModel.Connection, id);
    return title is null ? Results.NotFound() : Results.Ok(new { id, title });
});
app.MapGet("/healthz", () => Results.Ok("secondary"));
app.Run();

static async Task<string?> FindTaskTitleAsync(DbConnection connection, string taskId)
{
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT title FROM tasks WHERE task_id = @id";
    command.AddParam("@id", taskId);
    return (string?)await command.ExecuteScalarAsync();
}

// Same fixture shape as ReadOnlyReplicaTests' own TasksProjection -- kept local
// rather than shared, matching this project's existing per-file-fixture convention.
sealed class TasksProjection(IReadModelStore store) : IProjection
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
