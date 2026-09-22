using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// Milestone D2: the per-process reveal cache (Option A). A warm read makes no call to
/// the key service. Erasure empties the cache and keeps it empty, including against a
/// flush that was already in flight when the erasure landed.
/// </summary>
public class PiiRevealCacheTests
{
    private static (IKmsClient Client, FakeKmsHandler Handler) MakeClient()
    {
        var handler = new FakeKmsHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://kms.test/") };
        return (new KmsClient(http), handler);
    }

    private static async Task<Pii<string>> StoredAsync(IKmsClient client, string subjectId, string value)
    {
        var known = await Pii<string>.EncryptAsync(client, subjectId, value);
        return Pii<string>.FromCiphertext(known.SubjectId!, known.Ciphertext!);
    }

    private static async Task<Pii<string>> RevealAsync(Pii<string> pii, IKmsClient client, PiiRevealCache cache)
    {
        var buffer = new PiiRevealBuffer(client, cache);
        var task = pii.RevealAsync(buffer);
        await buffer.FlushAsync();
        return await task;
    }

    [Fact]
    public async Task A_cache_hit_completes_before_any_flush_and_makes_no_call()
    {
        var (client, handler) = MakeClient();
        var cache = new PiiRevealCache();
        var pii = await StoredAsync(client, "s1", "alice@example.com");

        await RevealAsync(pii, client, cache);
        Assert.Equal(1, handler.DecryptBatchCallCount);

        var warm = pii.RevealAsync(new PiiRevealBuffer(client, cache)); // never flushed
        Assert.True(warm.IsCompleted);
        Assert.Equal("alice@example.com", (await warm).Value);
        Assert.Equal(1, handler.DecryptBatchCallCount);
    }

    [Fact]
    public async Task Only_the_exact_ciphertext_hits_a_second_value_for_the_same_subject_still_calls()
    {
        var (client, handler) = MakeClient();
        var cache = new PiiRevealCache();
        var first = await StoredAsync(client, "s1", "one");
        var second = await StoredAsync(client, "s1", "two");

        await RevealAsync(first, client, cache);
        Assert.Equal("two", (await RevealAsync(second, client, cache)).Value);

        Assert.Equal(2, handler.DecryptBatchCallCount);
    }

    [Fact]
    public async Task A_destroyed_key_marks_the_subject_erased_so_later_reveals_skip_the_facade()
    {
        var (client, handler) = MakeClient();
        var cache = new PiiRevealCache();
        var pii = await StoredAsync(client, "s1", "secret");
        await client.DestroyKeyAsync("s1");

        Assert.Equal(PiiState.Redacted, (await RevealAsync(pii, client, cache)).State);
        Assert.True(cache.IsErased("s1"));

        var again = pii.RevealAsync(new PiiRevealBuffer(client, cache));
        Assert.True(again.IsCompleted);
        Assert.Equal(PiiState.Redacted, (await again).State);
        Assert.Equal(1, handler.DecryptBatchCallCount);
    }

    [Fact]
    public async Task MarkErased_drops_the_subjects_entries_and_leaves_other_subjects_alone()
    {
        var (client, handler) = MakeClient();
        var cache = new PiiRevealCache();
        var s1 = await StoredAsync(client, "s1", "a");
        var s2 = await StoredAsync(client, "s2", "b");
        await RevealAsync(s1, client, cache);
        await RevealAsync(s2, client, cache);

        cache.MarkErased("s1");

        Assert.Equal(1, cache.Count);
        // The key is still live at the facade (the destroyer hasn't run), yet the cache
        // alone is enough to answer "redacted": that is the evictor's whole job.
        Assert.Equal(PiiState.Redacted, (await RevealAsync(s1, client, cache)).State);
        Assert.Equal("b", (await RevealAsync(s2, client, cache)).Value);
        Assert.Equal(2, handler.DecryptBatchCallCount);
    }

    [Fact]
    public async Task A_flush_that_finishes_after_the_erasure_does_not_refill_the_cache()
    {
        var (client, _) = MakeClient();
        var cache = new PiiRevealCache();
        var pii = await StoredAsync(client, "s1", "secret");

        var buffer = new PiiRevealBuffer(client, cache);
        var inFlight = pii.RevealAsync(buffer); // requested before the erasure
        cache.MarkErased("s1");                  // erasure lands mid-flight
        await buffer.FlushAsync();

        // The caller that asked before the erasure gets its answer (bounded lag, P3),
        // but the plaintext is not kept.
        Assert.Equal("secret", (await inFlight).Value);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("s1", pii.Ciphertext!, out _));
    }

    [Fact]
    public void The_cache_is_bounded_and_evicts_the_least_recently_used_entry()
    {
        var cache = new PiiRevealCache(capacity: 2);
        cache.Put("s1", "c1", [1]);
        cache.Put("s1", "c2", [2]);
        Assert.True(cache.TryGet("s1", "c1", out _)); // c1 is now the most recent

        cache.Put("s2", "c3", [3]);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("s1", "c1", out _));
        Assert.False(cache.TryGet("s1", "c2", out _));
        Assert.True(cache.TryGet("s2", "c3", out _));
    }

    [Fact]
    public async Task More_than_the_facades_limit_for_one_subject_is_split_into_chunks()
    {
        var (client, handler) = MakeClient();
        var values = new List<Pii<string>>();
        for (var i = 0; i < KmsClient.MaxBatchItems + 1; i++)
            values.Add(await StoredAsync(client, "s1", $"v{i}"));

        var buffer = new PiiRevealBuffer(client);
        var tasks = values.Select(v => v.RevealAsync(buffer)).ToList();
        await buffer.FlushAsync();
        var revealed = await Task.WhenAll(tasks);

        Assert.Equal([KmsClient.MaxBatchItems, 1], handler.DecryptBatchItemCounts);
        Assert.Equal("v1000", revealed[^1].Value);
    }

    [Fact]
    public async Task A_destroyed_key_in_the_first_chunk_settles_the_rest_without_another_call()
    {
        var (client, handler) = MakeClient();
        var values = new List<Pii<string>>();
        for (var i = 0; i < KmsClient.MaxBatchItems + 1; i++)
            values.Add(await StoredAsync(client, "s1", $"v{i}"));
        await client.DestroyKeyAsync("s1");

        var buffer = new PiiRevealBuffer(client);
        var tasks = values.Select(v => v.RevealAsync(buffer)).ToList();
        await buffer.FlushAsync();
        var revealed = await Task.WhenAll(tasks);

        Assert.Equal(1, handler.DecryptBatchCallCount);
        Assert.All(revealed, r => Assert.Equal(PiiState.Redacted, r.State));
    }

    [Fact]
    public async Task The_evictor_reacts_only_to_SubjectErased_on_the_subject_stream()
    {
        var cache = new PiiRevealCache();
        var evictor = new PiiCacheEvictor(cache);
        Event Ev(string aggregate, string id, string type) => new(1, "e1", aggregate, id, 1, type, "{}", "{}", "");

        await evictor.ApplyAsync(Ev("order", "s1", DataSubject.SubjectErasedEvent), default);
        Assert.False(cache.IsErased("s1"));

        await evictor.ApplyAsync(Ev(DataSubject.Aggregate, "s1", DataSubject.SubjectErasedEvent), default);
        Assert.True(cache.IsErased("s1"));
    }

    [Fact]
    public async Task The_evictor_starts_at_the_key_destroyers_checkpoint_and_keeps_its_position_in_memory()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterDataSubjects();
        var erase = new Command(DataSubject.EraseSubjectCommand, "{}");
        var first = await registry.HandleAsync(DataSubject.Aggregate, "s-old", erase);
        await registry.HandleAsync(DataSubject.Aggregate, "s-new", erase);

        // The destroyer has handled s-old's erasure (its key is gone), not yet s-new's.
        await store.SaveCheckpointAsync(SubjectKeyDestroyer.ConsumerName, first.Single().Position);

        var cache = new PiiRevealCache();
        var engine = new ConsumerEngine(store, store);
        var evictor = await engine.RegisterPiiCacheEvictorAsync(cache, store);
        await engine.RunOnceAsync();

        // Before the seed, the facade's 404 does the work; the evictor didn't re-read it.
        Assert.False(cache.IsErased("s-old"));
        // After the seed, the key may still be live, so the evictor must have seen it.
        Assert.True(cache.IsErased("s-new"));
        // Its position never reached the shared store.
        Assert.Equal(0, await store.CheckpointAsync(evictor.Name));
    }
}
