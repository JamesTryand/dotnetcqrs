namespace DotnetCqrs.Crypto;

/// <summary>The batching half of the lazy/batched design (see
/// <c>platform/key-management-service/findings.md</c>'s "Phase 4 design" section). A
/// <see cref="Pii{T}.RevealAsync"/> call enqueues into this buffer and returns a task
/// that only completes once <see cref="FlushAsync"/> runs — nothing hits the facade
/// until then. Reveals sharing a subject collapse into one <c>decrypt-batch</c> call;
/// different subjects flush as separate, concurrent calls, mirroring the facade's own
/// one-key-per-batch-call shape.
///
/// Scoped to one replay pass / one request — not a singleton. A generated projection or
/// read-model route creates one, reveals whatever it needs, flushes once, discards it.</summary>
public sealed class PiiRevealBuffer(IKmsClient client)
{
    private readonly Dictionary<string, List<PendingReveal>> _bySubject = new();

    internal Task<PendingRevealOutcome> Enqueue(string subjectId, string ciphertext)
    {
        var tcs = new TaskCompletionSource<PendingRevealOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_bySubject.TryGetValue(subjectId, out var list))
            _bySubject[subjectId] = list = [];
        list.Add(new PendingReveal(ciphertext, tcs));
        return tcs.Task;
    }

    /// <summary>Flushes every subject's outstanding reveals, one <c>decrypt-batch</c>
    /// call per subject, run concurrently. Safe to call with nothing pending (no-op).
    /// Not safe to call concurrently with itself on the same buffer.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_bySubject.Count == 0) return;
        var batch = _bySubject.ToList();
        _bySubject.Clear();
        await Task.WhenAll(batch.Select(kv => FlushPendingAsync(kv.Key, kv.Value, ct))).ConfigureAwait(false);
    }

    private async Task FlushPendingAsync(string subjectId, List<PendingReveal> pending, CancellationToken ct)
    {
        KmsBatchDecryptResult result;
        try
        {
            result = await client.DecryptBatchAsync(subjectId, [.. pending.Select(p => p.Ciphertext)], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var p in pending) p.Tcs.TrySetException(ex);
            return;
        }

        if (result.State == KmsKeyState.Destroyed)
        {
            foreach (var p in pending) p.Tcs.SetResult(PendingRevealOutcome.RedactedOutcome);
            return;
        }

        var items = result.Items!;
        for (var i = 0; i < pending.Count; i++)
        {
            var item = items[i];
            pending[i].Tcs.SetResult(item.Succeeded
                ? PendingRevealOutcome.Ok(item.Plaintext!)
                : PendingRevealOutcome.ErrorOutcome(item.Error!));
        }
    }

    private sealed record PendingReveal(string Ciphertext, TaskCompletionSource<PendingRevealOutcome> Tcs);
}

internal readonly record struct PendingRevealOutcome(bool Redacted, byte[]? Plaintext, string? Error)
{
    public static readonly PendingRevealOutcome RedactedOutcome = new(true, null, null);
    public static PendingRevealOutcome Ok(byte[] plaintext) => new(false, plaintext, null);
    public static PendingRevealOutcome ErrorOutcome(string error) => new(false, null, error);
}
