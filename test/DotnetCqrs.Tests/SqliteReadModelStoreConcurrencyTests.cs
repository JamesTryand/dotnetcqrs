using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

/// <summary>
/// An HTTP query route reads through <see cref="IReadModelStore.ReadAsync{T}"/>, once per request. The SQLite
/// store used to run every read on its one shared connection with no coordination, and one SqliteConnection is
/// not safe for overlapping commands: under overlapping requests a query route intermittently returned 500
/// (about one run in twenty in the generated-route test). Reads now take turns.
///
/// <para>Typed as <see cref="IReadModelStore"/> on purpose: the default interface method is only visible
/// through the interface, and that is how every caller reaches it.</para>
/// </summary>
public class SqliteReadModelStoreConcurrencyTests
{
    private static async Task<IReadModelStore> NewStoreAsync(string path)
    {
        IReadModelStore store = await SqliteReadModelStore.OpenAsync(path);
        await using var create = store.Connection.CreateCommand();
        create.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT); " +
            string.Join(' ', Enumerable.Range(1, 50).Select(i => $"INSERT INTO items VALUES ({i}, 'item-{i}');"));
        await create.ExecuteNonQueryAsync();
        return store;
    }

    private static Task<long> CountAsync(IReadModelStore store) =>
        store.ReadAsync(async (connection, ct) =>
        {
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT COUNT(*) FROM items WHERE name LIKE 'item-%'";
            await using var reader = await select.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return reader.GetInt64(0);
        });

    [Theory]
    [InlineData(":memory:")]
    [InlineData("file")]
    public async Task Many_overlapping_reads_all_succeed(string kind)
    {
        var path = kind == ":memory:" ? kind : Path.Combine(Path.GetTempPath(), $"readmodel-concurrency-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = (IAsyncDisposable)await NewStoreAsync(path);
            var counts = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => CountAsync((IReadModelStore)store))));

            Assert.All(counts, c => Assert.Equal(50, c));
        }
        finally
        {
            if (kind != ":memory:")
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                    try { File.Delete(file); } catch (IOException) { }
        }
    }
}
