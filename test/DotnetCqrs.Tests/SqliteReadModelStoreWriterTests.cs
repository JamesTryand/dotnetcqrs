using Microsoft.Data.Sqlite;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

/// <summary>
/// A projection writes to <see cref="IReadModelStore.Connection"/> while query routes, authorization checks and
/// batch steps read through <see cref="IReadModelStore.ReadAsync{T}"/>. When both used the one shared
/// <see cref="SqliteConnection"/>, a read landing in the middle of a write threw from inside SQLite (found: the
/// legacy migration failing with ArgumentOutOfRangeException from SqliteCommand.Dispose). ReadAsync used to make reads
/// take turns with EACH OTHER only. File-backed stores now read on their own connections.
///
/// <para>Typed as <see cref="IReadModelStore"/> on purpose, as in <see cref="SqliteReadModelStoreConcurrencyTests"/>.</para>
/// </summary>
public class SqliteReadModelStoreWriterTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"readmodel-writer-{Guid.NewGuid():N}.db");
    private readonly List<string> _extraFiles = [];
    private IReadModelStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await SqliteReadModelStore.OpenAsync(_path);
        await using var create = _store.Connection.CreateCommand();
        create.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT)";
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var f in new[] { _path }.Concat(_extraFiles).SelectMany(p => new[] { p, p + "-wal", p + "-shm" }))
            try { File.Delete(f); } catch (IOException) { }
    }

    private static Task<long> CountAsync(IReadModelStore store) =>
        store.ReadAsync(async (connection, ct) =>
        {
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT COUNT(*) FROM items";
            await using var reader = await select.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return reader.GetInt64(0);
        });

    [Fact]
    public async Task Reads_overlapping_a_projections_writes_never_throw_and_only_see_committed_rows()
    {
        const int rows = 4000;
        var writing = true;
        var readerFailures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var lastSeen = new long[6];

        var readers = Enumerable.Range(0, lastSeen.Length).Select(r => Task.Run(async () =>
        {
            while (Volatile.Read(ref writing))
            {
                try
                {
                    var n = await CountAsync(_store);
                    if (n < lastSeen[r]) throw new InvalidOperationException($"count went backwards: {n} after {lastSeen[r]}");
                    lastSeen[r] = n;
                }
                catch (Exception ex)
                {
                    readerFailures.Enqueue(ex);
                    return;
                }
            }
        })).ToList();

        // The projection: one INSERT after another straight on Connection, as every projection does.
        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= rows; i++)
            {
                await using var insert = _store.Connection.CreateCommand();
                insert.CommandText = "INSERT INTO items (id, name) VALUES (@id, @name)";
                var id = insert.CreateParameter(); id.ParameterName = "@id"; id.Value = i; insert.Parameters.Add(id);
                var name = insert.CreateParameter(); name.ParameterName = "@name"; name.Value = $"item-{i}"; insert.Parameters.Add(name);
                await insert.ExecuteNonQueryAsync();
            }
        });

        await writer;
        Volatile.Write(ref writing, false);
        await Task.WhenAll(readers);

        Assert.True(readerFailures.IsEmpty, string.Join(Environment.NewLine, readerFailures.Select(e => e.GetType().Name + ": " + e.Message)));
        Assert.Equal(rows, await CountAsync(_store));
    }

    [Fact]
    public async Task A_database_attached_to_the_store_is_visible_to_reads_including_ones_already_pooled()
    {
        // A read before the attach leaves a pooled read connection that has never seen it.
        Assert.Equal(0, await CountAsync(_store));

        var otherPath = Path.Combine(Path.GetTempPath(), $"readmodel-attached-{Guid.NewGuid():N}.db");
        _extraFiles.Add(otherPath);
        await using (var other = new SqliteConnection($"Data Source={otherPath};Pooling=False"))
        {
            await other.OpenAsync();
            await using var create = other.CreateCommand();
            create.CommandText = "CREATE TABLE hits (term TEXT); INSERT INTO hits VALUES ('a'), ('b'), ('c');";
            await create.ExecuteNonQueryAsync();
        }

        await SqliteSearchIndexStore.AttachAsync(_store.Connection, otherPath);

        var hits = await _store.ReadAsync(async (connection, ct) =>
        {
            await using var select = connection.CreateCommand();
            select.CommandText = $"SELECT COUNT(*) FROM {SqliteSearchIndexStore.SchemaName}.hits";
            return (long)(await select.ExecuteScalarAsync(ct))!;
        });
        Assert.Equal(3, hits);
    }

    [Fact]
    public async Task A_read_cannot_write()
    {
        var ex = await Record.ExceptionAsync(() => _store.ReadAsync(async (connection, ct) =>
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO items (id, name) VALUES (1, 'sneaky')";
            return await insert.ExecuteNonQueryAsync(ct);
        }));

        Assert.NotNull(ex);
        Assert.Equal(0, await CountAsync(_store));
    }

    [Fact]
    public async Task A_failing_read_does_not_poison_later_reads()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ReadAsync<long>((_, _) => throw new InvalidOperationException("boom")));
        for (var i = 0; i < 20; i++) Assert.Equal(0, await CountAsync(_store));
    }

    [Fact]
    public async Task Disposing_the_store_releases_every_read_connection_so_the_file_can_be_deleted()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => CountAsync(_store)));

        await _store.DisposeAsync();

        File.Delete(_path); // throws IOException on Windows if any connection still holds the file
        foreach (var f in new[] { _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        _store = await SqliteReadModelStore.OpenAsync(_path); // for DisposeAsync
    }
}
