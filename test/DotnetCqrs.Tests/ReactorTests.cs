using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Reactors;

namespace DotnetCqrs.Tests;

public class ReactorTests
{
    // A minimal order decider — just enough to produce OrderConfirmed — and a
    // minimal task decider, used only to exercise the reactor pattern; not a
    // shipped domain example (that's Milestone 8's worked example).
    private static Decider<bool> OrderDecider() => new()
    {
        InitialState = () => false,
        Decide = (_, cmd) => cmd.Name switch
        {
            "ConfirmOrder" => [new NewEvent("OrderConfirmed", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (_, ev) => ev.Type == "OrderConfirmed",
    };

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) => cmd.Name switch
        {
            "CreateTask" when exists => throw new InvalidOperationException("task already exists"),
            "CreateTask" => [new NewEvent("TaskCreated", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (_, ev) => ev.Type == "TaskCreated",
    };

    // Opens a fulfillment task for every confirmed order, on the task
    // aggregate "fulfill-<orderId>" -- the deterministic target id that makes
    // replays idempotent (mirrors pocketcqrs's reactors.Fulfillment).
    private sealed class FulfillmentReactor : IReactor
    {
        public string Name => "fulfillment";

        public IReadOnlyList<Reaction> React(Event ev)
        {
            if (ev.Aggregate != "order" || ev.Type != "OrderConfirmed") return [];
            return [new Reaction("task", $"fulfill-{ev.AggregateId}", new Command("CreateTask", "{}"))];
        }
    }

    private static (DeciderRegistry Registry, SqliteEventStore Store) SetUpRegistry(SqliteEventStore store)
    {
        var registry = new DeciderRegistry(store);
        registry.Register("order", OrderDecider());
        registry.Register("task", TaskDecider());
        return (registry, store);
    }

    [Fact]
    public async Task Apply_dispatches_the_reaction_as_a_real_command_through_the_registry()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (registry, _) = SetUpRegistry(store);
        var consumer = new ReactorConsumer(new FulfillmentReactor(), registry);

        var confirmed = (await registry.HandleAsync("order", "o1", new Command("ConfirmOrder", "{}"))).Single();
        await consumer.ApplyAsync(confirmed, CancellationToken.None);

        var taskStream = await store.LoadStreamAsync("task", "fulfill-o1");
        Assert.Single(taskStream);
        Assert.Equal("TaskCreated", taskStream[0].Type);
    }

    [Fact]
    public async Task Redelivering_the_same_cause_is_idempotent_via_the_target_deciders_own_rejection()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (registry, _) = SetUpRegistry(store);
        var consumer = new ReactorConsumer(new FulfillmentReactor(), registry);

        var confirmed = (await registry.HandleAsync("order", "o1", new Command("ConfirmOrder", "{}"))).Single();
        await consumer.ApplyAsync(confirmed, CancellationToken.None);
        await consumer.ApplyAsync(confirmed, CancellationToken.None); // redelivered

        var taskStream = await store.LoadStreamAsync("task", "fulfill-o1");
        Assert.Single(taskStream); // second dispatch hit "task already exists" and was skipped
    }

    [Fact]
    public async Task React_ignores_events_it_does_not_care_about()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (registry, _) = SetUpRegistry(store);
        var consumer = new ReactorConsumer(new FulfillmentReactor(), registry);

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("SomethingElse", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None);

        Assert.Empty(await store.LoadStreamAsync("task", "fulfill-o1"));
    }

    [Fact]
    public async Task A_reaction_targeting_an_unregistered_aggregate_is_logged_and_skipped_not_thrown()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store); // no "order"/"task" registered at all
        var logs = new List<string>();
        var consumer = new ReactorConsumer(new FulfillmentReactor(), registry, logs.Add);

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderConfirmed", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None); // must not throw

        Assert.Contains(logs, l => l.Contains("reaction dropped"));
    }

    [Fact]
    public async Task Registered_via_ConsumerEngine_the_reactor_checkpoints_under_reactor_prefixed_name()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (registry, _) = SetUpRegistry(store);
        var consumer = new ReactorConsumer(new FulfillmentReactor(), registry);
        var engine = new ConsumerEngine(store, store);
        engine.Register(consumer);

        await registry.HandleAsync("order", "o1", new Command("ConfirmOrder", "{}"));
        await engine.RunOnceAsync();

        Assert.Equal(["reactor:fulfillment"], engine.Names);
        Assert.True(await store.CheckpointAsync("reactor:fulfillment") > 0);
        Assert.Single(await store.LoadStreamAsync("task", "fulfill-o1"));
    }
}
