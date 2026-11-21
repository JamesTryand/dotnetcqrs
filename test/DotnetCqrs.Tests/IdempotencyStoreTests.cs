using DotnetCqrs.Host;
using DotnetCqrs.Idempotency;

namespace DotnetCqrs.Tests;

/// <summary>A clock a test moves by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>What every <see cref="IIdempotencyStore"/> must do (the gateway contract's G7): the first write under a
/// key wins, a record comes back exactly as written for at least 7 days and never after its retention, and an expired
/// record gives way to a new one.</summary>
public static class IdempotencyStoreBehaviour
{
    private static IdempotencyRecord Record(string hash, string body) =>
        new(hash, 200, "application/json", System.Text.Encoding.UTF8.GetBytes(body));

    public static async Task FirstWriteWinsAndIsKeptForItsRetentionAsync(IIdempotencyStore store, ManualTimeProvider clock, TimeSpan retention)
    {
        Assert.Null(await store.FindAsync("k1"));
        Assert.True(await store.TryAddAsync("k1", Record("h1", """{"events":[]}""")));
        Assert.False(await store.TryAddAsync("k1", Record("h2", """{"events":[1]}""")));

        var found = await store.FindAsync("k1");
        Assert.NotNull(found);
        Assert.Equal("h1", found.RequestHash);
        Assert.Equal(200, found.StatusCode);
        Assert.Equal("application/json", found.ContentType);
        Assert.Equal("""{"events":[]}""", System.Text.Encoding.UTF8.GetString(found.Body));

        clock.Now += retention - TimeSpan.FromSeconds(1);
        Assert.NotNull(await store.FindAsync("k1"));

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(await store.FindAsync("k1"));
        Assert.True(await store.TryAddAsync("k1", Record("h3", "{}")));
        Assert.Equal("h3", (await store.FindAsync("k1"))!.RequestHash);
    }
}

public sealed class IdempotencyStoreTests
{
    [Fact]
    public async Task The_in_memory_store_keeps_the_first_write_for_its_retention()
    {
        var clock = new ManualTimeProvider();
        await IdempotencyStoreBehaviour.FirstWriteWinsAndIsKeptForItsRetentionAsync(
            new InMemoryIdempotencyStore(time: clock), clock, IdempotencyRetention.Default);
    }

    [Fact]
    public async Task The_sqlite_store_keeps_the_first_write_for_its_retention()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-idem-{Guid.NewGuid():N}", "idempotency.db");
        var clock = new ManualTimeProvider();
        await using var store = await SqliteIdempotencyStore.OpenAsync(path, TimeSpan.FromDays(10), clock);
        await IdempotencyStoreBehaviour.FirstWriteWinsAndIsKeptForItsRetentionAsync(store, clock, TimeSpan.FromDays(10));
    }

    [Fact]
    public async Task A_sqlite_record_survives_reopening_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-idem-{Guid.NewGuid():N}", "idempotency.db");
        var clock = new ManualTimeProvider();
        await using (var store = await SqliteIdempotencyStore.OpenAsync(path, time: clock))
            Assert.True(await store.TryAddAsync("k", new IdempotencyRecord("h", 200, "application/json", [1, 2, 3])));

        clock.Now += TimeSpan.FromDays(6);
        await using var reopened = await SqliteIdempotencyStore.OpenAsync(path, time: clock);
        Assert.Equal([1, 2, 3], (await reopened.FindAsync("k"))!.Body);
    }

    [Fact]
    public async Task No_store_accepts_a_retention_under_7_days()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryIdempotencyStore(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SqliteIdempotencyStore.OpenAsync(Path.Combine(Path.GetTempPath(), $"dotnetcqrs-idem-{Guid.NewGuid():N}.db"), TimeSpan.FromDays(1)));
    }
}
