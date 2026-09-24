using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

/// <summary>
/// Milestone D6: a keyed-hash index records the key version it was built with; after an
/// index key rotation, <c>RegisterHashedSearchIndexAsync</c> rebuilds at the new version while
/// searches stay pinned to the old one, then switches over (findings, "D6 decisions" item 7).
/// </summary>
public class HashedSearchIndexRotationTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-hashed-{Guid.NewGuid():N}.db");
    private SqliteEventStore _events = null!;
    private SqliteSearchIndexStore _search = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync()
    {
        _events = await SqliteEventStore.OpenAsync(":memory:");
        _search = await SqliteSearchIndexStore.OpenAsync(_path);
    }

    public async Task DisposeAsync()
    {
        await _events.DisposeAsync();
        await _search.DisposeAsync();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Stands in for a generated <c>{Collection}HashedIndex</c>: one row per event,
    /// tagged with the key version, whose "hash" names the version as the facade's does.</summary>
    private sealed class FakeHashedIndex(SqliteSearchIndexStore store, int keyVersion) : IHashedSearchIndex
    {
        public string IndexName => "customers:hashed";
        public int KeyVersion => keyVersion;
        public string Name => $"{IndexName}:v{keyVersion}";

        public async Task InitAsync(CancellationToken ct = default) =>
            await ExecuteAsync("CREATE TABLE IF NOT EXISTS t (row_key TEXT NOT NULL, key_version INTEGER NOT NULL, hash TEXT NOT NULL)", ct);

        public Task DeleteVersionsExceptAsync(IReadOnlyCollection<int> keep, CancellationToken ct = default) =>
            ExecuteAsync(keep.Count == 0 ? "DELETE FROM t" : $"DELETE FROM t WHERE key_version NOT IN ({string.Join(", ", keep)})", ct);

        public Task ApplyAsync(Event ev, CancellationToken ct) =>
            ExecuteAsync($"INSERT INTO t VALUES ('{ev.AggregateId}', {keyVersion}, 'vault:v{keyVersion}:{ev.Position}')", ct);

        private async Task ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<(ConsumerEngine Engine, HashedIndexKey Key)> StartAsync(int latestVersion)
    {
        var engine = new ConsumerEngine(_events, _events);
        var key = new HashedIndexKey("app");
        await engine.RegisterHashedSearchIndexAsync(_search, key, latestVersion, v => new FakeHashedIndex(_search, v), _log.Add);
        return (engine, key);
    }

    private async Task AppendAsync(string id) =>
        await _events.AppendAsync("customer", id, 0, [new NewEvent("CustomerRegistered", "{}")]);

    private async Task<List<string>> HashesAsync()
    {
        await using var command = _search.Connection.CreateCommand();
        command.CommandText = "SELECT hash FROM t ORDER BY hash";
        await using var reader = await command.ExecuteReaderAsync();
        var hashes = new List<string>();
        while (await reader.ReadAsync()) hashes.Add(reader.GetString(0));
        return hashes;
    }

    [Fact]
    public async Task A_first_build_records_the_latest_version_and_searches_with_it()
    {
        await AppendAsync("c1");
        var (engine, key) = await StartAsync(latestVersion: 1);
        await engine.RunOnceAsync();

        Assert.Equal(1, key.ActiveVersion("customers:hashed"));
        Assert.Equal(1, await _search.IndexVersionAsync("customers:hashed"));
        Assert.Equal(["vault:v1:1"], await HashesAsync());
    }

    [Fact]
    public async Task After_a_rotation_searches_stay_pinned_until_the_rebuild_catches_up_then_switch()
    {
        await AppendAsync("c1");
        await AppendAsync("c2");
        var (first, _) = await StartAsync(latestVersion: 1);
        await first.RunOnceAsync();

        // Ops rotated the key; the host restarts.
        var (engine, key) = await StartAsync(latestVersion: 2);
        Assert.Equal(1, key.ActiveVersion("customers:hashed"));
        Assert.Equal(["customers:hashed:v1", "customers:hashed:v2"], engine.Names);

        await engine.RunOnceAsync();

        Assert.Equal(2, key.ActiveVersion("customers:hashed"));
        Assert.Equal(2, await _search.IndexVersionAsync("customers:hashed"));
        Assert.Equal(["customers:hashed:v2"], engine.Names);
        Assert.Equal(0, await _search.CheckpointAsync("customers:hashed:v1"));
        Assert.Equal(["vault:v2:1", "vault:v2:2"], await HashesAsync());
        Assert.Contains(_log, l => l.Contains("switched to key version 2"));

        // Only the new version keeps indexing.
        await AppendAsync("c3");
        await engine.RunOnceAsync();
        Assert.Equal(["vault:v2:1", "vault:v2:2", "vault:v2:3"], await HashesAsync());
    }

    [Fact]
    public async Task A_rebuild_that_caught_up_before_a_restart_switches_at_startup()
    {
        await AppendAsync("c1");
        var (first, _) = await StartAsync(latestVersion: 1);
        await first.RunOnceAsync();
        // As if the v2 rebuild had reached the head, then the process stopped before switching.
        await _search.SaveCheckpointAsync("customers:hashed:v2", 1);

        var (engine, key) = await StartAsync(latestVersion: 2);

        Assert.Equal(2, key.ActiveVersion("customers:hashed"));
        Assert.Equal(["customers:hashed:v2"], engine.Names);
        Assert.Empty(await HashesAsync()); // v1 rows gone; v2's (checkpointed) rows would stay
    }

    [Fact]
    public async Task A_recorded_version_above_the_keys_latest_rebuilds_everything_at_the_latest()
    {
        await AppendAsync("c1");
        var (first, _) = await StartAsync(latestVersion: 3);
        await first.RunOnceAsync();

        // The key was recreated (or Vault restored): its latest is now 2.
        var (engine, key) = await StartAsync(latestVersion: 2);
        Assert.Empty(await HashesAsync());
        Assert.Equal(2, key.ActiveVersion("customers:hashed"));
        Assert.Contains(_log, l => l.Contains("rebuilding from the start"));

        await engine.RunOnceAsync();
        Assert.Equal(["vault:v2:1"], await HashesAsync());
    }
}
