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
}
