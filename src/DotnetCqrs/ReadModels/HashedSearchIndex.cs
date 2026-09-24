using System.Collections.Concurrent;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// A consumer that fills keyed-hash search indexes (schema 3.1.0 <c>match</c>, modes
/// <c>exact</c>/<c>prefix</c>, on a <c>field.pii</c> field) for ONE version of the
/// application's index key. Generated as <c>{Collection}HashedIndex</c>; it lives in the
/// <see cref="SqliteSearchIndexStore"/> beside the <c>contains</c> indexes, under the same rules
/// (checkpoint in the same file, rows deleted on erasure, excluded from backups).
///
/// <para>Every row carries the key version it was hashed with. Rows of two versions can sit in
/// one table during a rotation without ever matching each other, because the stored
/// <c>vault:vN:...</c> string names its version.</para>
/// </summary>
public interface IHashedSearchIndex : IConsumer
{
    /// <summary>The name shared by every version, e.g. <c>customers:hashed</c>. The consumer's
    /// own <see cref="IConsumer.Name"/> (its checkpoint) is <c>{IndexName}:v{KeyVersion}</c>.</summary>
    string IndexName { get; }

    /// <summary>The index key version this instance hashes with.</summary>
    int KeyVersion { get; }

    /// <summary>Creates the tables (idempotent).</summary>
    Task InitAsync(CancellationToken ct = default);

    /// <summary>Deletes every row whose key version is not in <paramref name="keep"/> (an empty
    /// list deletes every row).</summary>
    Task DeleteVersionsExceptAsync(IReadOnlyCollection<int> keep, CancellationToken ct = default);
}

/// <summary>
/// The application's index key name, and the key version each hashed index currently
/// searches with. One per process, shared by the query routes (which pin their <c>hmac</c>
/// call to <see cref="ActiveVersion"/>) and <see cref="HashedSearchIndexRegistration"/>
/// (which moves it on a switch-over).
/// </summary>
public sealed class HashedIndexKey(string name)
{
    private readonly ConcurrentDictionary<string, int> _active = new(StringComparer.Ordinal);

    /// <summary>The facade's index key name (<c>PUT /v1/index-keys/{name}</c>).</summary>
    public string Name { get; } = name;

    /// <summary>The key version to hash a search term with for <paramref name="indexName"/>.</summary>
    public int ActiveVersion(string indexName) =>
        _active.TryGetValue(indexName, out var version)
            ? version
            : throw new InvalidOperationException($"hashed index '{indexName}' is not registered");

    internal void SetActive(string indexName, int version) => _active[indexName] = version;
}

/// <summary>
/// Registers a hashed search index with a <see cref="ConsumerEngine"/>, handling index key
/// rotation (platform/eventmodeling-codegen, "D6 decisions" item 7).
///
/// <para>The key version is checked once, at startup. The store records which version each
/// index searches with. When the key's latest version is higher, a second consumer rebuilds the
/// index at the new version from the start of the log, while the old one stays live and
/// searches stay pinned to the old version. When the rebuild reaches the live consumer's
/// position it switches over: it records the new version, points searches at it, retires the
/// old consumer, and deletes the old version's rows. A host that is never restarted keeps
/// working on its pinned version; nothing polls.</para>
///
/// <para>Ordering: the live consumer is registered before the rebuild, and the engine runs a
/// pass in registration order. So by the time the rebuild applies an event, the live consumer
/// has already drained that pass, and "reached the live position" means caught up.</para>
/// </summary>
public static class HashedSearchIndexRegistration
{
    /// <param name="latestVersion">The key's latest version, from the facade at startup.</param>
    /// <param name="create">Makes the index consumer for a key version.</param>
    public static async Task RegisterHashedSearchIndexAsync(
        this ConsumerEngine engine, SqliteSearchIndexStore store, HashedIndexKey key, int latestVersion,
        Func<int, IHashedSearchIndex> create, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var latest = create(latestVersion);
        await latest.InitAsync(ct);
        var indexName = latest.IndexName;
        var active = await store.IndexVersionAsync(indexName, ct);

        if (active is null || active > latestVersion)
        {
            // Never built, or built with a version the key no longer has (the key was
            // recreated, or Vault restored from an older backup): its hashes can't be
            // reproduced, so rebuild everything at the latest version.
            if (active is not null)
                log($"hashed index {indexName}: recorded key version {active} is above the key's latest ({latestVersion}); rebuilding from the start");
            await latest.DeleteVersionsExceptAsync([], ct);
            await store.DeleteCheckpointAsync(latest.Name, ct);
            await store.SetIndexVersionAsync(indexName, latestVersion, ct);
            key.SetActive(indexName, latestVersion);
            engine.Register(latest, store);
            return;
        }

        if (active == latestVersion)
        {
            // Also clears rows an interrupted switch-over left behind.
            await latest.DeleteVersionsExceptAsync([latestVersion], ct);
            key.SetActive(indexName, latestVersion);
            engine.Register(latest, store);
            return;
        }

        // Rotated: keep the live version searchable while the new one is built.
        var live = create(active.Value);
        await live.DeleteVersionsExceptAsync([active.Value, latestVersion], ct);
        var switchOver = new SwitchOver(engine, store, key, live, latest, log)
        {
            LivePosition = await store.CheckpointAsync(live.Name, ct),
        };
        if (await store.CheckpointAsync(latest.Name, ct) >= switchOver.LivePosition)
        {
            // The rebuild had already caught up before a restart.
            await switchOver.SwitchAsync(ct);
            engine.Register(latest, store);
            return;
        }
        key.SetActive(indexName, active.Value);
        log($"hashed index {indexName}: key rotated to version {latestVersion}; rebuilding, searches stay on version {active} until it catches up");
        engine.Register(new LiveConsumer(live, switchOver), store);
        engine.Register(new RebuildConsumer(latest, switchOver), store);
    }

    private sealed class SwitchOver(
        ConsumerEngine engine, SqliteSearchIndexStore store, HashedIndexKey key,
        IHashedSearchIndex live, IHashedSearchIndex rebuild, Action<string> log)
    {
        public long LivePosition { get; set; }
        public bool Done { get; private set; }

        public async Task SwitchAsync(CancellationToken ct)
        {
            Done = true;
            // Durable first: if the process stops part-way, the next start sees the new
            // version as active and clears the old rows itself.
            await store.SetIndexVersionAsync(rebuild.IndexName, rebuild.KeyVersion, ct);
            key.SetActive(rebuild.IndexName, rebuild.KeyVersion);
            engine.Unregister(live.Name);
            await store.DeleteCheckpointAsync(live.Name, ct);
            await rebuild.DeleteVersionsExceptAsync([rebuild.KeyVersion], ct);
            log($"hashed index {rebuild.IndexName}: switched to key version {rebuild.KeyVersion}");
        }
    }

    private sealed class LiveConsumer(IHashedSearchIndex inner, SwitchOver switchOver) : IConsumer
    {
        public string Name => inner.Name;

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            // Retired: a pass that snapshotted the consumer list before the switch must not
            // write old-version rows back.
            if (switchOver.Done) return;
            await inner.ApplyAsync(ev, ct);
            switchOver.LivePosition = ev.Position;
        }
    }

    private sealed class RebuildConsumer(IHashedSearchIndex inner, SwitchOver switchOver) : IConsumer
    {
        public string Name => inner.Name;

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            await inner.ApplyAsync(ev, ct);
            if (!switchOver.Done && ev.Position >= switchOver.LivePosition)
                await switchOver.SwitchAsync(ct);
        }
    }
}
