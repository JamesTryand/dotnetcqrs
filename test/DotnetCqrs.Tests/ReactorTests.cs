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

    /// <summary>Stands in for a key service that is down: every protect call fails the way
    /// <c>KmsClient</c> does, until <see cref="Healthy"/> is set.</summary>
    private sealed class FlakyProtector(Func<Exception> failure) : IPiiProtector
    {
        public bool Healthy { get; set; }

        public Task<object> RevealAsync(object state, CancellationToken ct) => Task.FromResult(state);

        public Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct) =>
            Healthy ? Task.FromResult(events) : throw failure();
    }

    public static TheoryData<string> InfrastructureFailures => ["http", "kms-protocol", "timeout", "read-only", "shell-bug"];

    private static Exception MakeFailure(string kind) => kind switch
    {
        "http" => new HttpRequestException("Connection refused (kms:8200)"),
        "kms-protocol" => new DotnetCqrs.Crypto.KmsProtocolException("encrypt response body was empty"),
        "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        "read-only" => new ReadOnlyStoreException("append"),
        // Not a dependency type, but not thrown by Decide either: still not a rejection.
        "shell-bug" => new InvalidOperationException("protector bug"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(InfrastructureFailures))]
    public async Task An_infrastructure_failure_blocks_the_reactor_instead_of_dropping_the_reaction(string kind)
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("order", OrderDecider());
        var protector = new FlakyProtector(() => MakeFailure(kind));
        registry.Register("task", TaskDecider(), protector);
        var logs = new List<string>();
        var engine = new ConsumerEngine(store, store);
        engine.Register(new ReactorConsumer(new FulfillmentReactor(), registry, logs.Add));

        await registry.HandleAsync("order", "o1", new Command("ConfirmOrder", "{}"));
        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());

        Assert.Equal(0, await store.CheckpointAsync("reactor:fulfillment")); // not advanced past the cause
        Assert.DoesNotContain(logs, l => l.Contains("reaction rejected"));
        Assert.Empty(await store.LoadStreamAsync("task", "fulfill-o1"));

        protector.Healthy = true; // the dependency comes back: the next pass delivers it
        await engine.RunOnceAsync();
        Assert.Single(await store.LoadStreamAsync("task", "fulfill-o1"));
        Assert.True(await store.CheckpointAsync("reactor:fulfillment") > 0);
    }

    [Fact]
    public async Task A_domain_rejection_is_still_dropped_and_the_checkpoint_advances()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (registry, _) = SetUpRegistry(store);
        var logs = new List<string>();
        var engine = new ConsumerEngine(store, store);
        engine.Register(new ReactorConsumer(new FulfillmentReactor(), registry, logs.Add));

        await registry.HandleAsync("task", "fulfill-o1", new Command("CreateTask", "{}")); // target already exists
        await registry.HandleAsync("order", "o1", new Command("ConfirmOrder", "{}"));
        await engine.RunOnceAsync(); // must not throw

        Assert.Contains(logs, l => l.Contains("reaction rejected") && l.Contains("task already exists"));
        Assert.True(await store.CheckpointAsync("reactor:fulfillment") > 0);
    }
}
