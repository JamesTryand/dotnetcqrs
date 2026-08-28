// One hand-built system, end to end: write side (deciders) -> event log ->
// read side (projections) and a reactor, using every piece the dotnetcqrs
// baseline built across Milestones 2-7. Run with `dotnet run` from this
// directory; each run starts from a fresh pair of SQLite files.

using System.Data.Common;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;
using DotnetCqrs.Reactors;
using Microsoft.Data.Sqlite;
using OrderFulfillment;

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var eventsPath = Path.Combine(dataDir, "events.db");
var readModelPath = Path.Combine(dataDir, "readmodel.db");
foreach (var path in new[] { eventsPath, readModelPath })
    foreach (var f in Directory.GetFiles(dataDir, Path.GetFileName(path) + "*"))
        File.Delete(f);

Console.WriteLine($"event store: {eventsPath}");
Console.WriteLine($"read models: {readModelPath}");
Console.WriteLine();

await using var eventStore = await SqliteEventStore.OpenAsync(eventsPath);
await using var readModelDb = await SqliteReadModelStore.OpenAsync(readModelPath);

var registry = new DeciderRegistry(eventStore);
registry.Register(Orders.Aggregate, Orders.Decider());
registry.Register(Tasks.Aggregate, Tasks.Decider());

var ordersProjection = new OrdersProjection(readModelDb);
var tasksProjection = new TasksProjection(readModelDb);
await ordersProjection.InitAsync();
await tasksProjection.InitAsync();

// The tables must exist before the guard's triggers can be created on them,
// so InitAsync runs first.
await readModelDb.InstallWriteGuardAsync([.. ordersProjection.Tables, .. tasksProjection.Tables]);

var engine = new ConsumerEngine(eventStore, eventStore);
engine.Register(ordersProjection);
engine.Register(tasksProjection);
engine.Register(new ReactorConsumer(new FulfillmentReactor(), registry, msg => Console.WriteLine($"  [reactor] {msg}")));

Console.WriteLine("-- write side --");
Console.WriteLine("placing order o1...");
await registry.HandleAsync(Orders.Aggregate, "o1", new Command("PlaceOrder", """{"title":"widget x 3"}"""));
Console.WriteLine("confirming order o1...");
await registry.HandleAsync(Orders.Aggregate, "o1", new Command("ConfirmOrder", "{}"));

Console.WriteLine();
Console.WriteLine("-- read side: catching up (first pass projects OrderPlaced/OrderConfirmed and");
Console.WriteLine("   runs the reactor, which appends TaskCreated; a second pass is needed for the");
Console.WriteLine("   tasks projection to see it -- this IS the eventual-consistency gap the");
Console.WriteLine("   concepts doc describes, not a bug) --");
await engine.RunOnceAsync();
await engine.RunOnceAsync();

Console.WriteLine();
Console.WriteLine("-- read models after the round-trip --");
await PrintTableAsync(readModelDb.Connection, "orders", "order_id", "title", "confirmed");
await PrintTableAsync(readModelDb.Connection, "tasks", "task_id", "title", "completed");

Console.WriteLine();
Console.WriteLine("-- the write-guard, for real: a direct write to a projection-owned table --");
try
{
    await using var direct = readModelDb.Connection.CreateCommand();
    direct.CommandText = "UPDATE tasks SET completed = 1 WHERE task_id = 'fulfill-o1'";
    await direct.ExecuteNonQueryAsync();
    Console.WriteLine("  FAIL: direct write was not blocked");
}
catch (SqliteException ex)
{
    Console.WriteLine($"  direct write correctly rejected: {ex.Message}");
}

static async Task PrintTableAsync(DbConnection db, string table, params string[] columns)
{
    await using var command = db.CreateCommand();
    command.CommandText = $"SELECT {string.Join(", ", columns)} FROM {table}";
    await using var reader = await command.ExecuteReaderAsync();
    Console.WriteLine($"{table}:");
    while (await reader.ReadAsync())
    {
        var values = Enumerable.Range(0, columns.Length).Select(i => $"{columns[i]}={reader.GetValue(i)}");
        Console.WriteLine($"  {string.Join(" ", values)}");
    }
}
