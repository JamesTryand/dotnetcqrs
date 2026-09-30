// Seeds an event store through the real write path, for anything that needs a store written by
// dotnetcqrs itself: the eventstore-viewer's tests, the events-read contract probe, demos.
//
//   dotnet run --project samples/EventStoreFixtures -- --sqlite path/to/events.db
//   dotnet run --project samples/EventStoreFixtures -- --postgres "Host=...;Database=...;Username=...;Password=..."
//   (both flags together seed both stores with the same scenario)
//
// It dispatches commands through DeciderRegistry, the same call MapCqrsGateway makes, so events are
// written exactly as a host writes them (envelope, metadata, `created`). The Postgres role needs to
// be able to create the events table. Personal data is protected by the real IPiiProtector path, but
// with InMemoryKmsClient, whose "ciphertext" is reversible: fine for synthetic fixtures, never for a host.

using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using EventStoreFixtures;
using OrderFulfillment;

string? sqlitePath = null, postgres = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sqlite" when i + 1 < args.Length: sqlitePath = args[++i]; break;
        case "--postgres" when i + 1 < args.Length: postgres = args[++i]; break;
        default:
            Console.Error.WriteLine("usage: EventStoreFixtures [--sqlite <events.db>] [--postgres <connection string>]");
            return 2;
    }
}
if (sqlitePath is null && postgres is null)
{
    Console.Error.WriteLine("give --sqlite and/or --postgres");
    return 2;
}

// (aggregate, id, command, payload, expected outcome). A drifting stack must fail the seed, not ship a wrong fixture.
var scenario = new List<(string Agg, string Id, string Cmd, string Payload, bool Accepted)>();
for (var i = 1; i <= 10; i++)
    scenario.Add(("order", $"o-{i}", "PlaceOrder", $$"""{"customerRef":"cust-{{1 + i % 3}}"}""", true));
for (var i = 1; i <= 6; i++)
{
    scenario.Add(("order", $"o-{i}", "ConfirmOrder", "{}", true));
    scenario.Add(("task", $"fulfill-o-{i}", "CreateTask", $$"""{"title":"fulfil order o-{{i}}"}""", true));
}
scenario.Add(("order", "o-1", "ConfirmOrder", "{}", false));      // already confirmed
scenario.Add(("order", "o-nope", "ConfirmOrder", "{}", false));   // does not exist
foreach (var i in new[] { 1, 2, 3 })
    scenario.Add(("task", $"fulfill-o-{i}", "CompleteTask", "{}", true));
// Personal data: plaintext in, an encrypted $pii envelope stored.
string[] people = ["ada", "grace", "alan", "edsger"];
for (var i = 0; i < people.Length; i++)
    scenario.Add(("customer", $"cust-{i + 1}", "Register", $$"""{"email":"{{people[i]}}@example.com","plan":"{{(i % 2 == 0 ? "pro" : "free")}}"}""", true));
scenario.Add(("customer", "cust-1", "ChangeEmail", """{"email":"ada@example.org"}""", true));
scenario.Add(("customer", "cust-1", "Register", """{"email":"again@example.com","plan":"pro"}""", false)); // already registered
scenario.Add(("customer", "cust-nope", "ChangeEmail", """{"email":"nobody@example.com"}""", false));       // does not exist

var meta = new Dictionary<string, object> { ["actor"] = "seed" };
var kms = new InMemoryKmsClient();

async Task<int> Seed(IEventStore store, string label)
{
    var registry = new DeciderRegistry(store);
    registry.Register(Orders.Aggregate, Orders.Decider());
    registry.Register(Tasks.Aggregate, Tasks.Decider());
    registry.Register(Customers.Aggregate, Customers.Decider(), new Customers.Protector(kms));

    int events = 0, rejected = 0;
    foreach (var (agg, id, cmd, payload, accepted) in scenario)
    {
        try
        {
            var written = await registry.HandleWithMetaAsync(agg, id, new Command(cmd, payload), meta);
            if (!accepted)
                throw new InvalidOperationException($"{agg}/{id} {cmd} should have been rejected");
            events += written.Count;
        }
        catch (InvalidOperationException e) when (!accepted && !e.Message.Contains("should have been rejected"))
        {
            rejected++;
        }
    }
    Console.WriteLine($"{label}: {scenario.Count} commands, {events} events written, {rejected} rejected as expected");
    return events;
}

if (sqlitePath is not null)
{
    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(sqlitePath))!, Path.GetFileName(sqlitePath) + "*"))
        File.Delete(f);
    await using var sqlite = await SqliteEventStore.OpenAsync(sqlitePath);
    await Seed(sqlite, $"sqlite {sqlitePath}");
}
if (postgres is not null)
{
    await using var pg = await DotnetCqrs.Postgres.PostgresEventStore.OpenAsync(postgres);
    await Seed(pg, "postgres");
}
return 0;
