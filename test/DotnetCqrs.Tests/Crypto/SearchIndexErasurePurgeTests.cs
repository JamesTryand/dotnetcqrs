using System.Net;
using System.Text;
using DotnetCqrs.Crypto;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>The startup purge that makes a search index safe to serve after a restore
/// (<see cref="SearchIndexErasurePurge"/>), against the SQLite store. The Postgres store is
/// driven through the same purge by the generated-host tests.</summary>
public class SearchIndexErasurePurgeTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-purge-{Guid.NewGuid():N}.db");
    private SqliteSearchIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await SqliteSearchIndexStore.OpenAsync(_path);
        await ExecAsync("CREATE TABLE people__match_email_email (row_key TEXT PRIMARY KEY, term TEXT NOT NULL, subject TEXT NOT NULL)");
        await ExecAsync("CREATE TABLE people__hash_email_email_exact (row_key TEXT NOT NULL, key_version INTEGER NOT NULL, hash TEXT NOT NULL, subject TEXT NOT NULL)");
        await ExecAsync("INSERT INTO people__match_email_email VALUES ('r1', 'alice@example.com', 'alice'), ('r2', 'bob@example.com', 'bob')");
        await ExecAsync("INSERT INTO people__hash_email_email_exact VALUES ('r1', 1, 'vault:v1:a', 'alice'), ('r2', 1, 'vault:v1:b', 'bob')");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        File.Delete(_path);
    }

    private async Task ExecAsync(string sql)
    {
        await using var command = _store.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> SubjectsAsync(string table)
    {
        await using var command = _store.Connection.CreateCommand();
        command.CommandText = $"SELECT subject FROM {table} ORDER BY subject";
        var subjects = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) subjects.Add(reader.GetString(0));
        return subjects;
    }

    [Fact]
    public async Task Purge_deletes_erased_subjects_from_every_index_table_and_remembers_where_it_stopped()
    {
        var kms = new InMemoryKmsClient();
        await kms.DestroyKeyAsync("alice");
        await kms.DestroyKeyAsync("alice"); // a retried destroy is recorded twice

        Assert.Equal(2, await _store.PurgeErasedSubjectsAsync(kms));
        Assert.Equal(["bob"], await SubjectsAsync("people__match_email_email"));
        Assert.Equal(["bob"], await SubjectsAsync("people__hash_email_email_exact"));
        Assert.Equal(2, await _store.CheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint));

        // Nothing new erased: nothing to do, and the ledger is read from the cursor on.
        Assert.Equal(0, await _store.PurgeErasedSubjectsAsync(kms));
    }

    [Fact]
    public async Task A_store_restored_with_an_older_cursor_rereads_the_ledger_and_purges_again()
    {
        var kms = new InMemoryKmsClient();
        await kms.DestroyKeyAsync("alice");
        await _store.PurgeErasedSubjectsAsync(kms);

        // The restore: alice's row comes back, with the cursor from before her erasure.
        await ExecAsync("INSERT INTO people__match_email_email VALUES ('r1', 'alice@example.com', 'alice')");
        await _store.SaveCheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, 0);

        Assert.Equal(1, await _store.PurgeErasedSubjectsAsync(kms));
        Assert.Equal(["bob"], await SubjectsAsync("people__match_email_email"));
    }

    [Fact]
    public async Task A_ledger_shorter_than_the_cursor_is_read_from_the_start()
    {
        var kms = new InMemoryKmsClient();
        await kms.DestroyKeyAsync("alice");
        // The cursor is past the end: the key service's ledger was restored from an older copy.
        await _store.SaveCheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, 50);

        Assert.Equal(2, await _store.PurgeErasedSubjectsAsync(kms));
        Assert.Equal(1, await _store.CheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint));
    }

    [Fact]
    public async Task A_facade_that_ignores_the_cursor_fails_the_purge_instead_of_looping()
    {
        // Always answers the first page, whatever "after" says.
        var kms = new KmsClient(new HttpClient(new FixedResponse("""{"subjectIds":["alice"],"next":1}"""))
        {
            BaseAddress = new Uri("https://kms.test/"),
        });

        await Assert.ThrowsAsync<KmsProtocolException>(() => _store.PurgeErasedSubjectsAsync(kms));
    }

    private sealed class FixedResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
