using DotnetCqrs.EventStore;
using DotnetCqrs.Outbox;
using Xunit;

namespace DotnetCqrs.Tests;

/// <summary>The general due-time outbox: work a consumer records now and something does when it falls due (a review
/// on its date, a call made outside the engine). Kept by the event stores; <see cref="OutboxWorker"/> takes what is
/// due, waking when nudged or at the next due time, never polling for it.</summary>
public class OutboxTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Only_a_consumers_entries_that_are_due_come_back_soonest_first()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        await ((IOutbox)store).EnqueueAsync("review", "b", "{\"n\":2}", T0.AddDays(2));
        await ((IOutbox)store).EnqueueAsync("review", "a", "{\"n\":1}", T0.AddDays(1));
        await ((IOutbox)store).EnqueueAsync("review", "later", "{}", T0.AddDays(30));
        await ((IOutbox)store).EnqueueAsync("other", "x", "{}", T0);

        var due = await ((IOutbox)store).DueAsync("review", T0.AddDays(2), 10);

        Assert.Equal(["a", "b"], due.Select(e => e.Key));
        Assert.Equal("{\"n\":1}", due[0].Payload);
        Assert.Equal(T0.AddDays(1), due[0].Due);
        Assert.Equal(0, due[0].Attempts);
        Assert.Equal("review", due[0].Consumer);
        Assert.Single(await ((IOutbox)store).DueAsync("review", T0.AddDays(2), 1));
        Assert.Equal(T0.AddDays(1), await ((IOutbox)store).NextDueAsync("review"));
        Assert.Null(await ((IOutbox)store).NextDueAsync("nobody"));
    }

    [Fact]
    public async Task Recording_the_same_key_again_does_nothing()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        await ((IOutbox)store).EnqueueAsync("review", "s1", "{\"first\":true}", T0);
        await ((IOutbox)store).EnqueueAsync("review", "s1", "{\"first\":false}", T0.AddDays(-1));
        await ((IOutbox)store).EnqueueAsync("other", "s1", "{}", T0); // keys are per consumer

        var due = await ((IOutbox)store).DueAsync("review", T0, 10);
        Assert.Equal("{\"first\":true}", Assert.Single(due).Payload);
        Assert.Equal(T0, due[0].Due);
        Assert.Single(await ((IOutbox)store).DueAsync("other", T0, 10));
    }

    [Fact]
    public async Task An_entry_is_retried_later_or_removed_and_survives_reopening_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = await SqliteEventStore.OpenAsync(path))
            {
                await ((IOutbox)store).EnqueueAsync("review", "s1", "{}", T0);
                await ((IOutbox)store).EnqueueAsync("review", "s2", "{}", T0);
                await ((IOutbox)store).RetryAsync("review", "s1", 3, T0.AddHours(1));
                await ((IOutbox)store).RemoveAsync("review", "s2");
            }
            await using (var reopened = await SqliteEventStore.OpenAsync(path))
            {
                Assert.Empty(await ((IOutbox)reopened).DueAsync("review", T0, 10));
                var later = Assert.Single(await ((IOutbox)reopened).DueAsync("review", T0.AddHours(1), 10));
                Assert.Equal(("s1", 3), (later.Key, later.Attempts));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_read_only_store_refuses_to_record()
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbox-ro-{Guid.NewGuid():N}.db");
        try
        {
            await using (await SqliteEventStore.OpenAsync(path)) { }
            await using var reader = await SqliteEventStore.OpenReadOnlyAsync(path);
            await Assert.ThrowsAsync<ReadOnlyStoreException>(() => ((IOutbox)reader).EnqueueAsync("review", "s1", "{}", T0));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_worker_does_what_is_due_and_reschedules_what_asks_to_be_retried()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var clock = new ManualClock(T0);
        var seen = new List<string>();
        var worker = new OutboxWorker(store, "review", (entry, _) =>
        {
            seen.Add($"{entry.Key}#{entry.Attempts}");
            return Task.FromResult(entry.Key == "again" ? OutboxOutcome.Retry(T0.AddHours(2), "not yet") : OutboxOutcome.Done);
        }, clock);
        await ((IOutbox)store).EnqueueAsync("review", "done", "{}", T0);
        await ((IOutbox)store).EnqueueAsync("review", "again", "{}", T0);
        await ((IOutbox)store).EnqueueAsync("review", "future", "{}", T0.AddDays(1));

        Assert.Equal(2, await worker.ProcessDueAsync());

        Assert.Equal(["again#0", "done#0"], seen.Order());
        Assert.Equal(["future"], (await ((IOutbox)store).DueAsync("review", T0.AddDays(1), 10)).Where(e => e.Key != "again").Select(e => e.Key));
        var retried = Assert.Single(await ((IOutbox)store).DueAsync("review", T0.AddHours(2), 10));
        Assert.Equal(("again", 1), (retried.Key, retried.Attempts));
    }

    [Fact]
    public async Task A_handler_that_throws_is_tried_again_after_a_growing_wait_never_dropped()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var clock = new ManualClock(T0);
        var logged = new List<string>();
        var worker = new OutboxWorker(store, "review", (_, _) => throw new InvalidOperationException("service down"), clock, logged.Add);
        await ((IOutbox)store).EnqueueAsync("review", "s1", "{}", T0);

        await worker.ProcessDueAsync();
        var first = Assert.Single(await ((IOutbox)store).DueAsync("review", T0.AddHours(1), 10));
        Assert.Equal(1, first.Attempts);
        Assert.Equal(T0.AddSeconds(1), first.Due);

        clock.Advance(TimeSpan.FromSeconds(1));
        await worker.ProcessDueAsync();
        var second = Assert.Single(await ((IOutbox)store).DueAsync("review", T0.AddHours(1), 10));
        Assert.Equal(2, second.Attempts);
        Assert.Equal(T0.AddSeconds(3), second.Due);
        Assert.Contains(logged, l => l.Contains("service down"));

        // the wait stops growing at an hour
        await ((IOutbox)store).RetryAsync("review", "s1", 40, clock.GetUtcNow());
        await worker.ProcessDueAsync();
        Assert.Equal(clock.GetUtcNow().AddHours(1), Assert.Single(await ((IOutbox)store).DueAsync("review", T0.AddYears(1), 10)).Due);
    }

    [Fact]
    public async Task The_worker_wakes_when_nudged()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new OutboxWorker(store, "review", (entry, _) =>
        {
            done.TrySetResult(entry.Key);
            return Task.FromResult(OutboxOutcome.Done);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = worker.RunAsync(cts.Token);

        await ((IOutbox)store).EnqueueAsync("review", "s1", "{}", DateTimeOffset.UtcNow);
        worker.Nudge();

        Assert.Equal("s1", await done.Task.WaitAsync(cts.Token));
        cts.Cancel();
        await run;
    }

    // A review can be years away; a single wait that long is beyond what a timer accepts. The worker waits at most
    // OutboxWorker.MaxWait at a time and looks again, which is the clock, not a poll for new work.
    [Fact]
    public async Task An_entry_due_years_from_now_is_waited_for_without_failing()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var logged = new List<string>();
        var worker = new OutboxWorker(store, "review", (_, _) => Task.FromResult(OutboxOutcome.Done), log: logged.Add);
        await ((IOutbox)store).EnqueueAsync("review", "s1", "{}", DateTimeOffset.UtcNow.AddYears(6));
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);

        await Task.Delay(300);
        Assert.False(run.IsCompleted, "the worker stopped instead of waiting");
        cts.Cancel();
        await run;
        Assert.Empty(logged);
        Assert.Single(await ((IOutbox)store).DueAsync("review", DateTimeOffset.UtcNow.AddYears(7), 10));
    }
}
