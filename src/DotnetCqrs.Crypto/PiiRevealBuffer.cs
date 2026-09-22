namespace DotnetCqrs.Crypto;

/// <summary>The batching half of the lazy/batched design (see
/// <c>platform/key-management-service/findings.md</c>'s "Phase 4 design" section). A
/// <see cref="Pii{T}.RevealAsync"/> call enqueues into this buffer and returns a task
/// that only completes once <see cref="FlushAsync"/> runs — nothing hits the facade
/// until then. Reveals sharing a subject collapse into one <c>decrypt-batch</c> call
/// (chunked at <see cref="KmsClient.MaxBatchItems"/>, the facade's limit); different
/// subjects flush as separate, concurrent calls, mirroring the facade's own
/// one-key-per-batch-call shape.
///
/// <para>With a <see cref="PiiRevealCache"/>, a reveal the cache can answer (a hit, or a
/// subject it knows is erased) completes at once and never joins the batch. Flushed
/// plaintexts fill the cache, and a destroyed key marks the subject erased in it.</para>
///
/// Scoped to one replay pass / one request — not a singleton (the cache is the
/// singleton). A generated projection or read-model route creates one, reveals whatever
/// it needs, flushes once, discards it.</summary>
public sealed class PiiRevealBuffer(IKmsClient client, PiiRevealCache? cache = null)
{
    private readonly Dictionary<string, List<PendingReveal>> _bySubject = new();

    internal Task<PendingRevealOutcome> Enqueue(string subjectId, string ciphertext)
    {
        if (cache is not null)
        {
            if (cache.IsErased(subjectId)) return Task.FromResult(PendingRevealOutcome.RedactedOutcome);
            if (cache.TryGet(subjectId, ciphertext, out var plaintext)) return Task.FromResult(PendingRevealOutcome.Ok(plaintext));
        }

        var tcs = new TaskCompletionSource<PendingRevealOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_bySubject.TryGetValue(subjectId, out var list))
            _bySubject[subjectId] = list = [];
        list.Add(new PendingReveal(ciphertext, tcs));
        return tcs.Task;
    }

    /// <summary>Flushes every subject's outstanding reveals, one <c>decrypt-batch</c>
    /// call per subject (more past the facade's item limit), run concurrently. Safe to
    /// call with nothing pending (no-op). Not safe to call concurrently with itself on
    /// the same buffer.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_bySubject.Count == 0) return;
        var batch = _bySubject.ToList();
        _bySubject.Clear();
        await Task.WhenAll(batch.Select(kv => FlushSubjectAsync(kv.Key, kv.Value, ct))).ConfigureAwait(false);
    }

    // A subject's chunks run in order, so a destroyed key found by the first chunk
    // settles the rest without another call.
    private async Task FlushSubjectAsync(string subjectId, List<PendingReveal> pending, CancellationToken ct)
    {
        for (var start = 0; start < pending.Count; start += KmsClient.MaxBatchItems)
        {
            var chunk = pending.GetRange(start, Math.Min(KmsClient.MaxBatchItems, pending.Count - start));
            if (!await FlushChunkAsync(subjectId, chunk, ct).ConfigureAwait(false))
            {
                foreach (var p in pending.Skip(start + chunk.Count)) p.Tcs.TrySetResult(PendingRevealOutcome.RedactedOutcome);
                return;
            }
        }
    }

    /// <summary>False when the subject's key turned out to be destroyed.</summary>
    private async Task<bool> FlushChunkAsync(string subjectId, List<PendingReveal> pending, CancellationToken ct)
    {
        KmsBatchDecryptResult result;
        try
        {
            result = await client.DecryptBatchAsync(subjectId, [.. pending.Select(p => p.Ciphertext)], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var p in pending) p.Tcs.TrySetException(ex);
            return true;
        }

        if (result.State == KmsKeyState.Destroyed)
        {
            cache?.MarkErased(subjectId);
            foreach (var p in pending) p.Tcs.TrySetResult(PendingRevealOutcome.RedactedOutcome);
            return false;
        }

        var items = result.Items!;
        for (var i = 0; i < pending.Count; i++)
        {
            var item = items[i];
            if (item.Succeeded) cache?.Put(subjectId, pending[i].Ciphertext, item.Plaintext!);
            pending[i].Tcs.TrySetResult(item.Succeeded
                ? PendingRevealOutcome.Ok(item.Plaintext!)
                : PendingRevealOutcome.ErrorOutcome(item.Error!));
        }
        return true;
    }

    private sealed record PendingReveal(string Ciphertext, TaskCompletionSource<PendingRevealOutcome> Tcs);
}

internal readonly record struct PendingRevealOutcome(bool Redacted, byte[]? Plaintext, string? Error)
{
    public static readonly PendingRevealOutcome RedactedOutcome = new(true, null, null);
    public static PendingRevealOutcome Ok(byte[] plaintext) => new(false, plaintext, null);
    public static PendingRevealOutcome ErrorOutcome(string error) => new(false, null, error);
}
