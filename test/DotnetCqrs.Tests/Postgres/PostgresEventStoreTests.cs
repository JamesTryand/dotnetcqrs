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
    public async Task Concurrent_pooled_appends_produce_gapless_commit_ordered_positions()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();

        const int writers = 25;
        // Each writer appends to its own stream, so nothing here is a real concurrency
        // conflict -- the point is that the identity-assigned `position` values still
        // come out contiguous and in commit order despite overlapping transactions on
        // pooled connections. Without pg_advisory_xact_lock a later txn could commit
        // first and ConsumerEngine's `position > checkpoint` poll would skip the earlier
        // one for good.
        await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            store.AppendAsync("task", $"t{i}", 0, [new NewEvent("TaskCreated", $$"""{"n":{{i}}}""")])));

        var all = await store.PollAsync(0, 1000);

        Assert.Equal(writers, all.Count);
        // Fresh schema => identity starts at 1 => positions are exactly 1..writers,
        // already in ascending order (PollAsync orders by position), no gaps, no dupes.
        Assert.Equal(Enumerable.Range(1, writers).Select(n => (long)n), all.Select(e => e.Position));
    }
}
