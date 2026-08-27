using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

public class DeciderRegistryTests
{
    // A minimal decider — increment/decrement a counter — used only to exercise
    // the registry; not a real domain aggregate.
    private static Decider<int> CounterDecider() => new()
    {
        InitialState = () => 0,
        Decide = (state, command) => command.Name switch
        {
            "Increment" => [new NewEvent("Incremented", "{}")],
            "Decrement" when state <= 0 => throw new InvalidOperationException("counter cannot go below zero"),
            "Decrement" => [new NewEvent("Decremented", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {command.Name}"),
        },
        Evolve = (state, ev) => ev.Type switch
        {
            "Incremented" => state + 1,
            "Decremented" => state - 1,
            _ => state,
        },
    };

    private static async Task<(SqliteEventStore Store, DeciderRegistry Registry)> SetUpAsync()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("counter", CounterDecider());
        return (store, registry);
    }

    [Fact]
    public async Task Handle_folds_prior_events_before_deciding()
    {
        var (store, registry) = await SetUpAsync();
        await using var _ = store;

        await registry.HandleAsync("counter", "c1", new Command("Increment", "{}"));
        await registry.HandleAsync("counter", "c1", new Command("Increment", "{}"));
        await registry.HandleAsync("counter", "c1", new Command("Decrement", "{}"));

        var stream = await store.LoadStreamAsync("counter", "c1");
        Assert.Equal(["Incremented", "Incremented", "Decremented"], stream.Select(e => e.Type));
    }

    [Fact]
    public async Task Decide_rejection_leaves_no_trace_in_the_log()
    {
        var (store, registry) = await SetUpAsync();
        await using var _ = store;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.HandleAsync("counter", "c1", new Command("Decrement", "{}")));

        var stream = await store.LoadStreamAsync("counter", "c1");
        Assert.Empty(stream);
    }

    [Fact]
    public async Task Handle_throws_for_an_unregistered_aggregate()
    {
        var (store, registry) = await SetUpAsync();
        await using var _ = store;

        await Assert.ThrowsAsync<UnknownAggregateException>(() =>
            registry.HandleAsync("nope", "x1", new Command("Anything", "{}")));
    }

    // A decider that echoes cmd.Actor/Now/Provenance into the event's data, so
    // HandleWithMetaAsync's "stamp meta onto the Command before Decide sees it" can
    // be observed from outside.
    private static Decider<int> EchoDecider() => new()
    {
        InitialState = () => 0,
        Decide = (_, cmd) => [new NewEvent("Echoed",
            $$"""{"actor":"{{cmd.Actor}}","now":"{{cmd.Now}}","provenance":"{{cmd.Provenance}}"}""")],
        Evolve = (state, _) => state,
    };

    [Fact]
    public async Task HandleWithMeta_fills_Command_Actor_Now_Provenance_from_meta_before_Decide_sees_it()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        await using var _ = store;
        var registry = new DeciderRegistry(store);
        registry.Register("echo", EchoDecider());

        await registry.HandleWithMetaAsync("echo", "e1", new Command("Do", "{}"),
            new Dictionary<string, object> { ["actor"] = "user:alice", ["provenance"] = "peer:foo" });

        var ev = (await store.LoadStreamAsync("echo", "e1")).Single();
        Assert.Contains("\"actor\":\"user:alice\"", ev.Data);
        Assert.Contains("\"provenance\":\"peer:foo\"", ev.Data);
        Assert.DoesNotContain("\"now\":\"\"", ev.Data); // stamped automatically since absent from meta
    }

    [Fact]
    public async Task HandleAsync_without_meta_leaves_Actor_Now_Provenance_empty()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        await using var _ = store;
        var registry = new DeciderRegistry(store);
        registry.Register("echo", EchoDecider());

        await registry.HandleAsync("echo", "e1", new Command("Do", "{}"));

        var ev = (await store.LoadStreamAsync("echo", "e1")).Single();
        Assert.Contains("\"actor\":\"\"", ev.Data);
        Assert.Contains("\"provenance\":\"\"", ev.Data);
    }

    [Fact]
    public async Task HandleWithMeta_merges_meta_onto_event_metadata_with_meta_winning_on_collision()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        await using var _ = store;
        var registry = new DeciderRegistry(store);
        registry.Register("counter", new Decider<int>
        {
            InitialState = () => 0,
            Decide = (_, _) => [new NewEvent("Incremented", "{}", Metadata: """{"source":"decider","actor":"decider-set"}""")],
            Evolve = (state, _) => state,
        });

        await registry.HandleWithMetaAsync("counter", "c1", new Command("Increment", "{}"),
            new Dictionary<string, object> { ["actor"] = "meta-set", ["causationId"] = "cause-1" });

        var ev = (await store.LoadStreamAsync("counter", "c1")).Single();
        Assert.Contains("\"source\":\"decider\"", ev.Metadata);   // kept: not overridden
        Assert.Contains("\"actor\":\"meta-set\"", ev.Metadata);   // meta wins on collision
        Assert.Contains("\"causationId\":\"cause-1\"", ev.Metadata);
    }
}
