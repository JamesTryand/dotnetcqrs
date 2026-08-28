// dotnetcqrs-multi-node Milestone 3: the write side of the cross-host replication
// smoke test. Deliberately minimal -- this exists to prove LiteFS plus the
// already-built Milestone 1 (SqliteEventStore.OpenReadOnlyAsync) and Milestone 2
// (CqrsGatewayEndpoints.ForwardTo) machinery compose across two real network hosts,
// not to demo a feature. Run inside ops/litefs/'s docker-compose pair; see
// docs/cross-host-replication.md for how the two nodes fit together and why nothing
// here is LiteFS-aware -- the app talks to an ordinary file path, same as Milestone 1.
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;

var eventsPath = Environment.GetEnvironmentVariable("EVENTS_DB_PATH") ?? "/litefs/events.db";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(eventsPath))!);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var eventStore = await SqliteEventStore.OpenAsync(eventsPath);
var registry = new DeciderRegistry(eventStore);
registry.Register("task", TaskDecider());
builder.Services.AddSingleton(registry);

var app = builder.Build();
app.MapCqrsGateway();
app.MapGet("/healthz", () => Results.Ok("primary"));
app.Run();

static Decider<bool> TaskDecider() => new()
{
    InitialState = () => false,
    Decide = (exists, cmd) => cmd.Name switch
    {
        "CreateTask" when exists => throw new InvalidOperationException("task already exists"),
        "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
        _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
    },
    Evolve = (_, ev) => ev.Type == "TaskCreated",
};
