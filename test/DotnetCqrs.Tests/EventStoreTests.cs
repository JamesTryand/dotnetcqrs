using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

public class EventStoreTests
{
    private static Task<SqliteEventStore> OpenAsync() => SqliteEventStore.OpenAsync(":memory:");

    [Fact]
    public async Task Append_then_LoadStream_round_trips_in_sequence_order()
    {
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

    [Fact]
    public async Task Append_assigns_a_contiguous_global_position_and_a_fresh_id_per_event()
    {
        await using var store = await OpenAsync();

        var appended = await store.AppendAsync("task", "t1", 0,
            [new NewEvent("TaskCreated", "{}"), new NewEvent("TaskCompleted", "{}")]);

        Assert.Equal(2, appended.Count);
        Assert.True(appended[1].Position > appended[0].Position);
        Assert.NotEqual(appended[0].Id, appended[1].Id);
    }

    [Fact]
    public async Task Two_streams_do_not_contend_with_each_other()
    {
        await using var store = await OpenAsync();

        await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);
        await store.AppendAsync("task", "t2", 0, [new NewEvent("TaskCreated", "{}")]);

        var t1 = await store.LoadStreamAsync("task", "t1");
        var t2 = await store.LoadStreamAsync("task", "t2");

        Assert.Single(t1);
        Assert.Single(t2);
    }

    [Fact]
    public async Task Append_rejects_a_stale_expected_sequence()
    {
        await using var store = await OpenAsync();
        await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCompleted", "{}")]));

        Assert.Equal(1, ex.ActualSequence);
        Assert.Equal(0, ex.ExpectedSequence);
    }
}
