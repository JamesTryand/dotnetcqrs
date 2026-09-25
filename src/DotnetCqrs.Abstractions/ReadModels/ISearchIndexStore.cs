using System.Data.Common;
using DotnetCqrs.Consumers;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// The store for search indexes over personal data (schema 3.1.0 <c>match</c> on a
/// <c>field.pii</c> field): the one kind of read model that keeps readable values at rest (a
/// <c>contains</c> index), or keyed hashes of them (<c>exact</c>/<c>prefix</c>). Kept apart from
/// the read models so it can be left out of backups whole and rebuilt from the log, which is
/// what stops a restored backup from bringing back an erased person's plaintext.
/// <c>SqliteSearchIndexStore</c> is a separate <c>search.db</c> file; <c>PostgresSearchIndexStore</c>
/// is a schema of unlogged tables. Either way query routes reach it as schema
/// <see cref="SchemaName"/> (<c>key IN (SELECT row_key FROM search.&lt;table&gt; ...)</c>).
///
/// <para>It is the <see cref="ICheckpointStore"/> for the consumers that fill it, so an index and
/// its position are lost together and a lost index is rebuilt rather than left silently
/// partial.</para>
/// </summary>
public interface ISearchIndexStore : ICheckpointStore, IAsyncDisposable
{
    /// <summary>The schema name query routes use for the store's tables.</summary>
    const string SchemaName = "search";

    /// <summary>The checkpoint name under which <see cref="ICheckpointStore"/> keeps the startup
    /// purge's position in the key service's erasure ledger (<c>PurgeErasedSubjectsAsync</c>).</summary>
    const string ErasureLedgerCheckpoint = "kms:erasure-ledger";

    /// <summary>The connection the index consumers write through. Unqualified table names
    /// resolve inside the store.</summary>
    DbConnection Connection { get; }

    /// <summary>The statement an index consumer creates its tables with, followed by
    /// <c>IF NOT EXISTS name (...)</c>: <c>CREATE TABLE</c>, or <c>CREATE UNLOGGED TABLE</c> on
    /// Postgres, whose unlogged tables stay out of physical backups and replicas and come back
    /// empty after any crash recovery.</summary>
    string CreateTable { get; }

    /// <summary>Forgets a consumer's position, so it next starts from the beginning of the log.</summary>
    Task DeleteCheckpointAsync(string name, CancellationToken ct = default);

    /// <summary>The key version the hashed index <paramref name="name"/> searches with, or null
    /// if it has never been built in this store.</summary>
    Task<int?> IndexVersionAsync(string name, CancellationToken ct = default);

    Task SetIndexVersionAsync(string name, int version, CancellationToken ct = default);

    /// <summary>Called after an erasure deleted rows from <paramref name="tables"/>: makes the
    /// deleted values unrecoverable from the store's own files. SQLite already overwrites
    /// deleted rows (<c>secure_delete</c>), so it does nothing; Postgres keeps deleted row
    /// versions on disk until space is reused, so it rewrites each table
    /// (<c>VACUUM FULL</c>). Brief exclusive lock per table; erasures are rare.</summary>
    Task ScrubAsync(IReadOnlyCollection<string> tables, CancellationToken ct = default);

    /// <summary>Deletes every row belonging to <paramref name="subjects"/> from every table in
    /// the store with a <c>subject</c> column, then scrubs the tables it deleted from. Used by
    /// the startup purge, which doesn't know the generated index tables; returns the number of
    /// rows deleted.</summary>
    Task<int> DeleteSubjectsAsync(IReadOnlyCollection<string> subjects, CancellationToken ct = default);
}
