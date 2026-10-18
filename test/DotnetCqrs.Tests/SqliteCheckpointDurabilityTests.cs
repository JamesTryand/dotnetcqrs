using System.Diagnostics;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

/// <summary>
/// A checkpoint is a consumer's progress, and losing the last few in a power cut only rewinds it: the consumer applies
/// those events again, which delivery is already at-least-once for. So a checkpoint is committed without flushing the
/// disk (<c>synchronous = NORMAL</c> for its statement alone; with WAL a later flushing commit makes it durable too),
/// while an append keeps the store's full flush. Catch-up writes a checkpoint per consumer per event, so a flush each
/// made a host with 39 consumers catch up at 0.8 s an event (timesheets, 2026-10-17).
/// </summary>
public class SqliteCheckpointDurabilityTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-checkpoint-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_checkpoint_commits_without_waiting_for_the_disk_as_an_append_does()
    {
        await using var store = await SqliteEventStore.OpenAsync(Path.Combine(_dir, "events.db"));
        const int n = 200;

        var appends = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            await store.AppendAsync("counter", $"c{i}", 0, [new NewEvent("Ticked", "{}")]);
        appends.Stop();

        var checkpoints = Stopwatch.StartNew();
        for (var i = 1; i <= n; i++)
            await store.SaveCheckpointAsync("proj", i);
        checkpoints.Stop();

        Assert.Equal(n, await store.CheckpointAsync("proj"));
        // Both flushing, the two take about as long (each is one small commit). Without the flush a checkpoint is
        // an order of magnitude cheaper.
        Assert.True(checkpoints.Elapsed < appends.Elapsed / 2,
            $"{n} checkpoints took {checkpoints.ElapsedMilliseconds} ms, {n} appends {appends.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task After_a_checkpoint_the_connection_flushes_again_even_when_the_checkpoint_failed()
    {
        await using var store = await SqliteEventStore.OpenAsync(Path.Combine(_dir, "events.db"));
        var full = await store.SynchronousAsync();

        await store.SaveCheckpointAsync("proj", 1);
        Assert.Equal(full, await store.SynchronousAsync());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveCheckpointAsync("proj", 2, cancelled.Token));
        Assert.Equal(full, await store.SynchronousAsync());
        Assert.Equal(1, await store.CheckpointAsync("proj"));
    }
}
