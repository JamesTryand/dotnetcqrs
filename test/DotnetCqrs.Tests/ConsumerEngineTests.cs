using System.Collections.Concurrent;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

public class ConsumerEngineTests
{
    private sealed class RecordingConsumer(string name) : IConsumer
    {
        public string Name { get; } = name;
        public ConcurrentQueue<Event> Applied { get; } = new();

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            Applied.Enqueue(ev);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingConsumer(string name, long failAtPosition) : IConsumer
    {
        public string Name { get; } = name;
        public ConcurrentQueue<Event> Applied { get; } = new();

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            if (ev.Position == failAtPosition)
                throw new InvalidOperationException("boom");
            Applied.Enqueue(ev);
            return Task.CompletedTask;
        }
    }

    private static async Task<SqliteEventStore> SeededStoreAsync(string aggregateId, int count)
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var events = Enumerable.Range(1, count).Select(i => new NewEvent("Ticked", $$"""{"n":{{i}}}""")).ToList();
        await store.AppendAsync("counter", aggregateId, 0, events);
        return store;
    }

    [Fact]
    public async Task RunOnce_delivers_pending_events_in_position_order_and_advances_the_checkpoint()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store);
        var consumer = new RecordingConsumer("proj-a");
        engine.Register(consumer);

        await engine.RunOnceAsync();

        Assert.Equal(3, consumer.Applied.Count);
        Assert.Equal([1L, 2L, 3L], consumer.Applied.Select(e => e.Sequence));
        Assert.Equal(3, await store.CheckpointAsync("proj-a"));
    }

    [Fact]
    public async Task RunOnce_only_delivers_new_events_on_a_second_pass()
    {
        await using var store = await SeededStoreAsync("c1", 2);
        var engine = new ConsumerEngine(store, store);
        var consumer = new RecordingConsumer("proj-a");
        engine.Register(consumer);

        await engine.RunOnceAsync();
        await store.AppendAsync("counter", "c1", 2, [new NewEvent("Ticked", """{"n":3}""")]);
        await engine.RunOnceAsync();

        Assert.Equal(3, consumer.Applied.Count);
    }

    [Fact]
    public async Task A_failing_consumer_does_not_block_other_consumers_and_retries_from_the_failed_event()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store);
        var healthy = new RecordingConsumer("healthy");
        var failing = new FailingConsumer("failing", failAtPosition: 2);
        engine.Register(healthy);
        engine.Register(failing);

        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());

        Assert.Equal(3, healthy.Applied.Count);
        Assert.Single(failing.Applied); // stopped at the failing event; checkpoint not advanced past it
        Assert.Equal(1, await store.CheckpointAsync("failing"));
    }

    [Fact]
    public async Task Unregister_stops_delivery_but_keeps_the_checkpoint()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store);
        var consumer = new RecordingConsumer("proj-a");
        engine.Register(consumer);
        await engine.RunOnceAsync();

        engine.Unregister("proj-a");
        Assert.Empty(engine.Names);

        await store.AppendAsync("counter", "c1", 1, [new NewEvent("Ticked", """{"n":2}""")]);
        await engine.RunOnceAsync();

        Assert.Single(consumer.Applied); // never re-registered, so never sees event #2
        Assert.Equal(1, await store.CheckpointAsync("proj-a")); // checkpoint untouched
    }

    [Fact]
    public async Task StartAsync_delivers_a_committed_event_promptly_via_the_nudge_not_the_slow_tick()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromSeconds(30));
        var consumer = new RecordingConsumer("proj-a");
        engine.Register(consumer);

        using var cts = new CancellationTokenSource();
        var run = engine.StartAsync(cts.Token);

        await store.AppendAsync("counter", "c1", 0, [new NewEvent("Ticked", "{}")]);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (consumer.Applied.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Single(consumer.Applied);
    }

    [Fact]
    public async Task A_consumer_given_its_own_checkpoint_store_never_touches_the_engines()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store);
        var local = new InMemoryCheckpointStore(start: 1);
        var consumer = new RecordingConsumer("local");
        engine.Register(consumer, local);

        await engine.RunOnceAsync();

        Assert.Equal([2L, 3L], consumer.Applied.Select(e => e.Position));
        Assert.Equal(3, await local.CheckpointAsync("local"));
        Assert.Equal(0, await store.CheckpointAsync("local"));
    }
}
