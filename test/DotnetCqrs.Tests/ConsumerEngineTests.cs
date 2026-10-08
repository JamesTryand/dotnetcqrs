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
    public async Task Overlapping_passes_take_turns_so_no_event_is_delivered_twice_or_concurrently()
    {
        // A host runs StartAsync's background loop AND may call RunOnceAsync itself (a batch step
        // that needs a deterministic catch-up). Two passes reading the same checkpoint would each
        // apply every pending event.
        await using var store = await SeededStoreAsync("c1", 5);
        var engine = new ConsumerEngine(store, store);
        var inFlight = 0;
        var maxInFlight = 0;
        var delivered = new ConcurrentQueue<long>();
        engine.Register(new DelegateConsumer("proj-a", async ev =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            await Task.Delay(15);
            delivered.Enqueue(ev.Sequence);
            Interlocked.Decrement(ref inFlight);
        }));

        await Task.WhenAll(engine.RunOnceAsync(), engine.RunOnceAsync(), engine.RunOnceAsync());

        Assert.Equal(1, maxInFlight);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], delivered.OrderBy(x => x));
        Assert.Equal(5, await store.CheckpointAsync("proj-a"));
    }

    [Fact]
    public async Task A_pass_waiting_its_turn_can_be_cancelled()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store);
        var release = new TaskCompletionSource();
        engine.Register(new DelegateConsumer("proj-a", _ => release.Task));

        var first = engine.RunOnceAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunOnceAsync(cts.Token));

        release.SetResult();
        await first;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
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

    [Fact]
    public async Task A_blocked_consumer_is_logged_once_per_pass_naming_the_consumer_and_the_event_position()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var log = new ConcurrentQueue<string>();
        var engine = new ConsumerEngine(store, store, logger: log.Enqueue);
        engine.Register(new FailingConsumer("failing", failAtPosition: 2));

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());

        var line = Assert.Single(log);
        Assert.Contains("consumer=failing", line);
        Assert.Contains("position=2", line);
        Assert.Contains("boom", line);
        Assert.Contains("position 2", Assert.Single(thrown.InnerExceptions).Message);
    }

    private sealed class TimingOutConsumer : IConsumer
    {
        public string Name => "timing-out";
        public Task ApplyAsync(Event ev, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
    }

    [Fact]
    public async Task A_consumer_raising_its_own_cancellation_is_a_blocked_consumer_not_an_engine_shutdown()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var log = new ConcurrentQueue<string>();
        var engine = new ConsumerEngine(store, store, logger: log.Enqueue);
        engine.Register(new TimingOutConsumer());

        // Before: the TaskCanceledException escaped RunOnceAsync as cancellation, which
        // StartAsync's loop treats as shutdown -- every consumer silently stopped.
        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());
        Assert.Contains("consumer=timing-out", Assert.Single(log));
        Assert.Equal(0, await store.CheckpointAsync("timing-out"));
    }

    [Fact]
    public async Task StartAsync_does_not_log_a_blocked_consumer_twice_per_pass()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var log = new ConcurrentQueue<string>();
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromSeconds(30), logger: log.Enqueue);
        engine.Register(new FailingConsumer("failing", failAtPosition: 1));

        using var cts = new CancellationTokenSource();
        var run = engine.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (log.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200); // give a duplicate outer log line time to appear
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Single(log); // one pass (30s tick, no nudge) -> one line, not a second "run error" copy
    }

    // --- Status: health/telemetry contract 4.4-4.5, STATE-MACHINES.md machine 2 ---

    private sealed class RecordingProjection(string name) : DotnetCqrs.Projections.IProjection
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Tables => [];
        public Task ApplyAsync(Event ev, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A clock set an hour after the events were written, so anything pending is well
    /// past any lag threshold.</summary>
    private sealed class LaterClock() : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddHours(1);
    }

    [Fact]
    public async Task Status_before_the_first_pass_is_behind_with_unknown_lag()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new RecordingConsumer("proj-a"));

        var status = Assert.Single(engine.Status());

        Assert.Equal(new ConsumerStatus("proj-a", false, ConsumerState.Behind, null, null, null), status);
    }

    [Fact]
    public async Task Status_after_catching_up_is_current_with_zero_lag()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store, timeProvider: new LaterClock());
        engine.Register(new RecordingConsumer("proj-a"));

        await engine.RunOnceAsync();

        var status = Assert.Single(engine.Status());
        Assert.Equal(ConsumerState.Current, status.State);
        Assert.Equal(3, status.Checkpoint);
        Assert.Equal(0, status.LagPositions);
        Assert.Equal(0, status.LagSeconds);
    }

    [Fact]
    public async Task Only_projections_and_search_indexes_are_read_models()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new RecordingProjection("orders"));
        engine.Register(new RecordingConsumer("reactor"));

        Assert.Equal([("orders", true), ("reactor", false)], engine.Status().Select(s => (s.Name, s.IsReadModel)));
    }

    [Fact]
    public async Task A_failing_consumer_is_blocked_at_the_failing_event_with_its_lag_growing()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store, timeProvider: new LaterClock());
        engine.Register(new FailingConsumer("proj-a", failAtPosition: 2));

        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());
        // A retry pass keeps it blocked (it fails at the same event again).
        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());

        var status = Assert.Single(engine.Status());
        Assert.Equal(ConsumerState.Blocked, status.State);
        Assert.Equal(1, status.Checkpoint);
        Assert.Equal(2, status.LagPositions);
        Assert.InRange(status.LagSeconds!.Value, 3500, 3700);
    }

    [Fact]
    public async Task A_blocked_consumer_that_gets_past_the_event_is_current_again()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store);
        var failing = true;
        engine.Register(new DelegateConsumer("proj-a", ev =>
            failing && ev.Position == 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask));

        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());
        Assert.Equal(ConsumerState.Blocked, Assert.Single(engine.Status()).State);

        failing = false;
        await engine.RunOnceAsync();

        Assert.Equal(ConsumerState.Current, Assert.Single(engine.Status()).State);
    }

    [Fact]
    public async Task Mid_pass_a_consumer_is_behind_by_the_age_of_the_event_it_is_applying()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var engine = new ConsumerEngine(store, store, timeProvider: new LaterClock());
        ConsumerStatus? seen = null;
        engine.Register(new DelegateConsumer("proj-a", ev =>
        {
            if (ev.Position == 1) seen = Assert.Single(engine.Status());
            return Task.CompletedTask;
        }));

        await engine.RunOnceAsync();

        Assert.NotNull(seen);
        Assert.Equal(ConsumerState.Behind, seen.State);
        Assert.Equal(0, seen.Checkpoint);
        Assert.Equal(3, seen.LagPositions);
        Assert.InRange(seen.LagSeconds!.Value, 3500, 3700);
    }

    [Fact]
    public async Task Lag_within_the_threshold_is_current()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store, timeProvider: new LaterClock()) { LagThreshold = TimeSpan.FromHours(2) };
        ConsumerState? seen = null;
        engine.Register(new DelegateConsumer("proj-a", _ =>
        {
            seen = Assert.Single(engine.Status()).State;
            return Task.CompletedTask;
        }));

        await engine.RunOnceAsync();

        Assert.Equal(ConsumerState.Current, seen);
    }

    [Fact]
    public async Task Unregistering_drops_the_consumer_from_status()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new RecordingConsumer("proj-a"));
        await engine.RunOnceAsync();

        engine.Unregister("proj-a");

        Assert.Empty(engine.Status());
    }

    // --- Draining (StopAsync) ---

    private sealed class WaitsForCancellationConsumer(string name) : IConsumer
    {
        public string Name { get; } = name;
        public TaskCompletionSource InHand { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            InHand.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }
    }

    [Fact]
    public async Task StopAsync_lets_the_event_in_hand_finish_checkpoints_it_and_leaves_the_rest()
    {
        await using var store = await SeededStoreAsync("c1", 5);
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromSeconds(30));
        var inHand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new ConcurrentQueue<long>();
        engine.Register(new DelegateConsumer("proj-a", async ev =>
        {
            if (ev.Position == 2)
            {
                inHand.SetResult();
                await release.Task;
            }
            applied.Enqueue(ev.Position);
        }));
        var run = engine.StartAsync(CancellationToken.None);
        await inHand.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = engine.StopAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.False(stop.IsCompleted); // still waiting for the event in hand

        release.SetResult();

        Assert.True(await stop.WaitAsync(TimeSpan.FromSeconds(10)));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1L, 2L], applied);
        Assert.Equal(2, await store.CheckpointAsync("proj-a")); // resumes at event 3
    }

    [Fact]
    public async Task StopAsync_past_its_deadline_cancels_the_consumer_mid_event_and_returns_false()
    {
        await using var store = await SeededStoreAsync("c1", 3);
        var log = new ConcurrentQueue<string>();
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromSeconds(30), logger: log.Enqueue);
        var consumer = new WaitsForCancellationConsumer("proj-a");
        engine.Register(consumer);
        var run = engine.StartAsync(CancellationToken.None);
        await consumer.InHand.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var graceful = await engine.StopAsync(TimeSpan.FromMilliseconds(100));

        Assert.False(graceful);
        Assert.True(consumer.Cancelled);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await store.CheckpointAsync("proj-a")); // the interrupted event is redone
        Assert.Contains(log, l => l.Contains("drain deadline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsync_stops_an_idle_engine_at_once_not_after_the_tick()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromSeconds(30));
        engine.Register(new RecordingConsumer("proj-a"));
        var run = engine.StartAsync(CancellationToken.None);
        await Task.Delay(200); // caught up and waiting on the tick

        var started = DateTime.UtcNow;
        Assert.True(await engine.StopAsync(TimeSpan.FromSeconds(10)));

        Assert.True(run.IsCompleted);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopAsync_on_an_engine_that_never_started_returns_true()
    {
        await using var store = await SeededStoreAsync("c1", 1);
        var engine = new ConsumerEngine(store, store);

        Assert.True(await engine.StopAsync(TimeSpan.FromSeconds(1)));
    }

    private sealed class DelegateConsumer(string name, Func<Event, Task> apply) : IConsumer
    {
        public string Name { get; } = name;
        public Task ApplyAsync(Event ev, CancellationToken ct) => apply(ev);
    }
}
