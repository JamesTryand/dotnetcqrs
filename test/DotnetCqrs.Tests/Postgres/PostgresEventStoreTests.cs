using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="EventStoreTests"/>, plus one Postgres-only
/// test that the transaction-scoped advisory lock keeps <c>position</c> gapless and
/// commit-ordered under genuinely concurrent, pooled appends.</summary>
[Collection("postgres")]
public class PostgresEventStoreTests(PostgresFixture fx)
{
    private async Task<PostgresEventStore> OpenAsync()
        => await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());

    [SkippableFact]
    public async Task Append_then_LoadStream_round_trips_in_sequence_order()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();

        await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", """{"title":"a"}""")]);
        await store.AppendAsync("task", "t1", 1, [new NewEvent("TaskCompleted", "{}")]);

        var stream = await store.LoadStreamAsync("task", "t1");

        Assert.Equal(2, stream.Count);
        Assert.Equal(1, stream[0].Sequence);
        Assert.Equal("TaskCreated", stream[0].Type);
        Assert.Equal(2, stream[1].Sequence);
        Assert.Equal("TaskCompleted", stream[1].Type);
    }

    [SkippableFact]
    public async Task Append_assigns_a_contiguous_global_position_and_a_fresh_id_per_event()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();

        var appended = await store.AppendAsync("task", "t1", 0,
            [new NewEvent("TaskCreated", "{}"), new NewEvent("TaskCompleted", "{}")]);

        Assert.Equal(2, appended.Count);
        Assert.True(appended[1].Position > appended[0].Position);
        Assert.NotEqual(appended[0].Id, appended[1].Id);
    }

    [SkippableFact]
    public async Task Two_streams_do_not_contend_with_each_other()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();

        await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);
        await store.AppendAsync("task", "t2", 0, [new NewEvent("TaskCreated", "{}")]);

        var t1 = await store.LoadStreamAsync("task", "t1");
        var t2 = await store.LoadStreamAsync("task", "t2");

        Assert.Single(t1);
        Assert.Single(t2);
    }

    [SkippableFact]
    public async Task Append_rejects_a_stale_expected_sequence()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();
        await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCompleted", "{}")]));

        Assert.Equal(1, ex.ActualSequence);
        Assert.Equal(0, ex.ExpectedSequence);
    }

    [SkippableFact]
    public async Task A_poller_running_during_concurrent_appends_never_steps_over_a_position()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();

        // This is the failure the advisory lock exists to prevent, reproduced the way
        // ConsumerEngine would hit it. Identity assigns `position` at INSERT time, but
        // without pg_advisory_xact_lock two overlapping transactions can COMMIT out of
        // that order. A consumer polling `WHERE position > seen ORDER BY position` mid-run
        // then advances `seen` past the higher position while the lower one is still
        // uncommitted -- and never sees the lower one again. The lock makes commit order
        // equal position order, so this poll loop sees every position exactly once.
        const int writers = 60;
        using var done = new CancellationTokenSource();

        var seenPositions = new List<long>();
        var poller = Task.Run(async () =>
        {
            long seen = 0;
            while (!done.IsCancellationRequested)
            {
                foreach (var ev in await store.PollAsync(seen, 1000))
                {
                    seenPositions.Add(ev.Position);
                    seen = ev.Position;
                }
                await Task.Delay(5);
            }
            foreach (var ev in await store.PollAsync(seen, 1000)) // final drain
                seenPositions.Add(ev.Position);
        });

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            store.AppendAsync("task", $"t{i}", 0, [new NewEvent("TaskCreated", $$"""{"n":{{i}}}""")])));
        await Task.Delay(100);
        done.Cancel();
        await poller;

        // Fresh schema => positions are exactly 1..writers. The poll loop must have seen
        // every one, in ascending order, with no gap and no duplicate.
        Assert.Equal(Enumerable.Range(1, writers).Select(n => (long)n), seenPositions);
    }
}
