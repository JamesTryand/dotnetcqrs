using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Crypto;

/// <summary>
/// Makes a search index safe to serve after its data was restored from a backup.
///
/// <para>A search index keeps normalized plaintext (or keyed hashes) of personal data, and
/// forgets a person when it sees their <c>SubjectErased</c> event. If the store is restored
/// from a backup taken before an erasure, the person's rows come back. When the event log was
/// restored to the same point, it no longer holds that <c>SubjectErased</c> either, so
/// replaying the log can't remove them. The key service's erasure ledger is backed up and
/// reconciled separately (and is what re-destroys the keys after a Vault restore), so it is the
/// one record that still knows. Run this at startup, before the query routes open and before
/// the consumer engine starts (platform/eventmodeling-codegen, "Postgres parity" item 5).</para>
///
/// <para>The position in the ledger is kept as a checkpoint in the store itself
/// (<see cref="ISearchIndexStore.ErasureLedgerCheckpoint"/>). If the store is rebuilt or
/// restored, the position goes back with it and the purge re-reads more of the ledger:
/// the safe direction.</para>
/// </summary>
public static class SearchIndexErasurePurge
{
    /// <summary>Deletes every erased subject's rows from <paramref name="store"/> (and scrubs the
    /// tables they were in), reading the ledger from where the last purge stopped. Throws if the
    /// key service can't be reached: an index that may hold erased people's data must not be
    /// served. Returns the number of rows deleted.</summary>
    public static async Task<int> PurgeErasedSubjectsAsync(this ISearchIndexStore store, IKmsClient kms, CancellationToken ct = default)
    {
        var cursor = await store.CheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, ct).ConfigureAwait(false);
        var deleted = 0;
        var restarted = false;
        while (true)
        {
            var page = await kms.ListErasuresAsync(cursor, KmsClient.MaxBatchItems, ct).ConfigureAwait(false);
            // A non-empty page must move the cursor forward, except for the facade's one
            // restart-from-0 (a ledger shorter than the cursor). Anything else would loop
            // forever, holding the host at startup.
            if (page.SubjectIds.Count > 0 && page.Next <= cursor)
            {
                if (restarted || page.Next == cursor)
                    throw new KmsProtocolException($"the erasure ledger cursor did not advance (asked after {cursor}, got next {page.Next})");
                restarted = true;
            }
            if (page.SubjectIds.Count == 0)
            {
                // Also records a restart: a ledger shorter than the cursor answers from 0.
                if (page.Next != cursor)
                    await store.SaveCheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, page.Next, ct).ConfigureAwait(false);
                return deleted;
            }
            deleted += await store.DeleteSubjectsAsync([.. page.SubjectIds.Distinct(StringComparer.Ordinal)], ct).ConfigureAwait(false);
            // Saved only after the delete, so an interrupted purge repeats a page rather than skipping it.
            await store.SaveCheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, page.Next, ct).ConfigureAwait(false);
            cursor = page.Next;
        }
    }
}
