using System.Collections.Concurrent;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

/// <summary>
/// Milestone D5: the separate search store keeps its consumers' checkpoints inside itself,
/// so losing the file (deleted, excluded from a backup, not restored) means a rebuild from
/// the start of the log, never an index that silently stops short.
/// </summary>
public class SearchIndexStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-search-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private sealed class RecordingConsumer : IConsumer
    {
        public string Name => "customers:search";
        public ConcurrentQueue<long> Applied { get; } = new();

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            Applied.Enqueue(ev.Position);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Losing_the_search_file_loses_its_checkpoint_too_so_the_index_rebuilds_from_the_start()
    {
        await using var events = await SqliteEventStore.OpenAsync(":memory:");
        await events.AppendAsync("customer", "c1", 0, [new NewEvent("CustomerRegistered", "{}"), new NewEvent("CustomerRenamed", "{}")]);

        var first = new RecordingConsumer();
        await using (var search = await SqliteSearchIndexStore.OpenAsync(_path))
        {
            var engine = new ConsumerEngine(events, events);
            engine.Register(first, search);
            await engine.RunOnceAsync();
            Assert.Equal(2, await search.CheckpointAsync(first.Name));
        }
        Assert.Equal(0, await events.CheckpointAsync(first.Name)); // never in the event store's checkpoints

        Dispose(); // the file is gone: not restored from a backup, say

        var rebuilt = new RecordingConsumer();
        await using (var search = await SqliteSearchIndexStore.OpenAsync(_path))
        {
            var engine = new ConsumerEngine(events, events);
            engine.Register(rebuilt, search);
            await engine.RunOnceAsync();
        }
        Assert.Equal([1L, 2L], rebuilt.Applied);
    }

    [Fact]
    public async Task The_store_turns_on_secure_delete_so_erased_plaintext_does_not_linger_in_free_pages()
    {
        await using var search = await SqliteSearchIndexStore.OpenAsync(_path);
        await using var command = search.Connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
