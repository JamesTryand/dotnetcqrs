using System.Collections.Concurrent;
using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;

namespace DotnetCqrs.Tests;

/// <summary>D27 live views, L1: the engine tells live subscribers when a projection changes a table they depend
/// on. One notification per batch per table, only from projections, only after the checkpoint is saved, and a
/// subscriber can never hold the engine up.</summary>
public class ReadModelChangeFeedTests
{
    private sealed class TableProjection(string name, params string[] tables) : IProjection
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Tables { get; } = tables;
        public int Applied;

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            Interlocked.Increment(ref Applied);
            return Task.CompletedTask;
        }
    }

    private sealed class Automation(string name) : IConsumer
    {
        public string Name { get; } = name;
        public Task ApplyAsync(Event ev, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FailingProjection(string name, string table, long failAtPosition) : IProjection
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Tables { get; } = [table];

        public Task ApplyAsync(Event ev, CancellationToken ct) =>
            ev.Position == failAtPosition ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }

    private static async Task<SqliteEventStore> SeededStoreAsync(int count)
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        if (count > 0)
            await store.AppendAsync("counter", "c1", 0,
                Enumerable.Range(1, count).Select(i => new NewEvent("Ticked", $$"""{"n":{{i}}}""")).ToList());
        return store;
    }

    [Fact]
    public async Task A_projection_publishes_one_change_per_table_per_batch_with_the_batch_last_position()
    {
        await using var store = await SeededStoreAsync(3);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new TableProjection("timeEntries", "timeEntries", "timeEntriesIndex"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["timeEntries", "timeEntriesIndex"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Equal(
            [new ReadModelChanged("timeEntries", 3), new ReadModelChanged("timeEntriesIndex", 3)],
            changes.ToArray());
    }

    [Fact]
    public async Task A_long_catch_up_publishes_once_per_batch_not_per_event()
    {
        // The engine reads 100 events at a time: 250 events are three batches.
        await using var store = await SeededStoreAsync(250);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new TableProjection("p", "t"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["t"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Equal([100L, 200L, 250L], changes.Select(c => c.Position));
    }

    [Fact]
    public async Task Nothing_is_published_when_nothing_new_was_applied()
    {
        await using var store = await SeededStoreAsync(2);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new TableProjection("p", "t"));
        await engine.RunOnceAsync();
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["t"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task An_automation_publishes_nothing_because_it_changes_no_view()
    {
        await using var store = await SeededStoreAsync(2);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new Automation("consumer:flagOnOverallotment"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["consumer:flagOnOverallotment"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task A_subscriber_hears_only_about_its_own_tables()
    {
        await using var store = await SeededStoreAsync(1);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new TableProjection("a", "tableA"));
        engine.Register(new TableProjection("b", "tableB"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["tableB"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Equal([new ReadModelChanged("tableB", 1)], changes.ToArray());
    }

    [Fact]
    public async Task A_subscriber_that_throws_holds_up_neither_the_engine_nor_other_subscribers()
    {
        await using var store = await SeededStoreAsync(2);
        var log = new ConcurrentQueue<string>();
        var engine = new ConsumerEngine(store, store, logger: log.Enqueue);
        var projection = new TableProjection("p", "t");
        engine.Register(projection);
        using var bad = engine.Subscribe(["t"], _ => throw new InvalidOperationException("viewer gone"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var good = engine.Subscribe(["t"], changes.Enqueue);

        await engine.RunOnceAsync();

        Assert.Equal(2, projection.Applied);
        Assert.Equal(2, await store.CheckpointAsync("p"));
        Assert.Equal([new ReadModelChanged("t", 2)], changes.ToArray());
        Assert.Contains(log, l => l.Contains("read-model change subscriber failed") && l.Contains("viewer gone"));
    }

    [Fact]
    public async Task Disposing_a_subscription_ends_it()
    {
        await using var store = await SeededStoreAsync(1);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new TableProjection("p", "t"));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        engine.Subscribe(["t"], changes.Enqueue).Dispose();

        await engine.RunOnceAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task A_projection_that_blocks_mid_batch_publishes_up_to_the_last_event_it_applied()
    {
        await using var store = await SeededStoreAsync(5);
        var engine = new ConsumerEngine(store, store);
        engine.Register(new FailingProjection("p", "t", failAtPosition: 4));
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["t"], changes.Enqueue);

        await Assert.ThrowsAsync<AggregateException>(() => engine.RunOnceAsync());

        // Events 1-3 are applied and checkpointed, so that change is real; 4 and 5 are not.
        Assert.Equal([new ReadModelChanged("t", 3)], changes.ToArray());
        Assert.Equal(3, await store.CheckpointAsync("p"));
    }

    [Fact]
    public async Task A_live_engine_publishes_after_each_commit_without_anyone_polling()
    {
        await using var store = await SeededStoreAsync(0);
        var engine = new ConsumerEngine(store, store, tick: TimeSpan.FromHours(1)); // the fallback tick never fires
        engine.Register(new TableProjection("p", "t"));
        var published = new TaskCompletionSource<ReadModelChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = engine.Subscribe(["t"], c => published.TrySetResult(c));
        using var cts = new CancellationTokenSource();
        var loop = engine.StartAsync(cts.Token);

        await store.AppendAsync("counter", "c1", 0, [new NewEvent("Ticked", """{"n":1}""")]);

        // The commit nudges the engine, the projection applies, and the change is pushed: a callback, not a poll.
        var change = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new ReadModelChanged("t", 1), change);
        await engine.StopAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await loop;
    }

    [Fact]
    public async Task Erasing_someone_tells_the_viewers_of_personal_data_though_no_table_changed()
    {
        // The evictor marks the subject erased in this process's reveal cache, so the views holding personal data
        // now read back redacted: a change their live viewers must be pushed.
        await using var store = await SeededStoreAsync(0);
        var cache = new PiiRevealCache();
        var engine = new ConsumerEngine(store, store);
        await engine.RegisterPiiCacheEvictorAsync(cache, store, viewTables: ["staff", "timeEntries"]);
        var changes = new ConcurrentQueue<ReadModelChanged>();
        using var _ = engine.Subscribe(["staff", "timeEntries", "projects"], changes.Enqueue);

        // An ordinary event changes nothing the evictor knows about.
        await store.AppendAsync("counter", "c1", 0, [new NewEvent("Ticked", """{"n":1}""")]);
        await engine.RunOnceAsync();
        Assert.Empty(changes);

        await store.AppendAsync(DataSubject.Aggregate, "s1", 0, [new NewEvent(DataSubject.SubjectErasedEvent, "{}")]);
        await engine.RunOnceAsync();

        Assert.True(cache.IsErased("s1"));
        Assert.Equal([new ReadModelChanged("staff", 2), new ReadModelChanged("timeEntries", 2)], changes.ToArray());

        // Reported once: the next batch without an erasure says nothing.
        await store.AppendAsync("counter", "c1", 1, [new NewEvent("Ticked", """{"n":2}""")]);
        await engine.RunOnceAsync();
        Assert.Equal(2, changes.Count);
    }
}
