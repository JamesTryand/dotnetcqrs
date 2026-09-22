using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Crypto;

/// <summary>Drops an erased subject's plaintext from this process's
/// <see cref="PiiRevealCache"/> when <c>SubjectErased</c> lands in the log.
///
/// <para>Its position belongs to the process, not the database, because the cache it
/// empties is per-process. A durable, shared checkpoint would let one instance advance
/// another's position past an erasure its cache never saw. Register it with
/// <see cref="PiiCacheEvictorRegistration.RegisterPiiCacheEvictorAsync"/>, which gives it an
/// in-memory checkpoint.</para></summary>
public sealed class PiiCacheEvictor(PiiRevealCache cache) : IConsumer
{
    public string Name => "pii:cache-evictor";

    public Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Type == DataSubject.SubjectErasedEvent && ev.Aggregate == DataSubject.Aggregate)
            cache.MarkErased(ev.AggregateId);
        return Task.CompletedTask;
    }
}

/// <summary>Registration for <see cref="PiiCacheEvictor"/>.</summary>
public static class PiiCacheEvictorRegistration
{
    /// <summary>Registers the evictor with a process-local checkpoint seeded from
    /// <see cref="SubjectKeyDestroyer"/>'s durable position in
    /// <paramref name="durableCheckpoints"/> (the engine's store).
    ///
    /// <para>Why there: every erasure before that position already has its key destroyed,
    /// so a reveal for that subject gets a 404 from the facade and is marked erased rather
    /// than cached. Every erasure after it, the evictor reads. Starting at the log head
    /// instead would miss an erasure the destroyer hasn't handled yet: its key is still
    /// live, so its plaintext could be cached with nothing left to evict it. If the
    /// destroyer has never run against this store, the seed is 0 and the evictor scans
    /// the whole log once. That is slower, but still safe.</para></summary>
    public static async Task<PiiCacheEvictor> RegisterPiiCacheEvictorAsync(
        this ConsumerEngine engine, PiiRevealCache cache, ICheckpointStore durableCheckpoints, CancellationToken ct = default)
    {
        var start = await durableCheckpoints.CheckpointAsync(SubjectKeyDestroyer.ConsumerName, ct).ConfigureAwait(false);
        var evictor = new PiiCacheEvictor(cache);
        engine.Register(evictor, new InMemoryCheckpointStore(start));
        return evictor;
    }
}
