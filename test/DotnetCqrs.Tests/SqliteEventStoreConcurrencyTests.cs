using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

/// <summary>
/// The store holds ONE connection for its whole lifetime, and an append keeps a transaction open on it.
/// Reads, checkpoint saves and dead-letter writes used to run on that connection without taking the same
/// lock, so under a busy host (commands, consumer polling and checkpoint saves at once) they could land
/// inside an append's transaction and fail, intermittently, as a 500/503 on a perfectly good request.
/// </summary>
public class SqliteEventStoreConcurrencyTests
{
    [Fact]
    public async Task Appends_polls_reads_checkpoints_and_dead_letters_can_all_run_at_once()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        const int writers = 4, perWriter = 150;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        var stop = false;

        var writing = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
                await store.AppendAsync("agg", $"s{w}", i, [new NewEvent("E", "{}")], ct);
        }, ct)).ToList();

        // Everything else that shares the connection, hammering it for as long as the writers run.
        var others = new List<Task>
        {
            Task.Run(async () => { while (!Volatile.Read(ref stop)) await store.PollAsync(0, 50, ct); }, ct),
            Task.Run(async () => { while (!Volatile.Read(ref stop)) await store.HeadPositionAsync(ct); }, ct),
            Task.Run(async () => { while (!Volatile.Read(ref stop)) await store.LoadStreamAsync("agg", "s0", ct); }, ct),
            Task.Run(async () =>
            {
                long n = 0;
                while (!Volatile.Read(ref stop)) { await store.SaveCheckpointAsync("c", ++n, ct); await store.CheckpointAsync("c", ct); }
            }, ct),
            Task.Run(async () =>
            {
                var ev = new Event(1, "id", "agg", "s0", 1, "E", "{}", "{}", "2026-01-01T00:00:00.000Z");
                while (!Volatile.Read(ref stop)) { await store.AddDeadLetterAsync("consumer", ev, "boom", ct); await store.ListDeadLettersAsync(ct: ct); }
            }, ct),
        };

        await Task.WhenAll(writing);
        Volatile.Write(ref stop, true);
        await Task.WhenAll(others); // any failure on the shared connection surfaces here

        for (var w = 0; w < writers; w++)
            Assert.Equal(perWriter, (await store.LoadStreamAsync("agg", $"s{w}", ct)).Count);
    }
}
