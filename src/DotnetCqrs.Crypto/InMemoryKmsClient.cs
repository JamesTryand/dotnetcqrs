namespace DotnetCqrs.Crypto;

/// <summary>An <see cref="IKmsClient"/> with no facade behind it, for scenario
/// verification, generator tests and local dry runs. It honours the facade's contract
/// (keys must be ensured before encrypt; a destroyed key makes every ciphertext under
/// it <see cref="KmsKeyState.Destroyed"/>; destroy is idempotent) but its "ciphertext"
/// is a reversible, self-describing wrapper — <c>inmem:{subject}:{base64 plaintext}</c> —
/// not encryption. <b>Never register this in a real host.</b> A production host that
/// has no <c>KMS_FACADE_URL</c> must fail closed (no protector at all), not fall back
/// to this.</summary>
public sealed class InMemoryKmsClient : IKmsClient
{
    private readonly HashSet<string> _keys = [];
    private readonly Lock _lock = new();

    public int EncryptCalls { get; private set; }
    public int DecryptBatchCalls { get; private set; }

    public Task EnsureKeyAsync(string subjectId, CancellationToken ct = default)
    {
        lock (_lock) _keys.Add(subjectId);
        return Task.CompletedTask;
    }

    public Task<string> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EncryptCalls++;
            if (!_keys.Contains(subjectId)) throw new KmsKeyNotFoundException(subjectId);
            return Task.FromResult(Seal(subjectId, plaintext));
        }
    }

    public Task<KmsDecryptResult> DecryptAsync(string subjectId, string ciphertext, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_keys.Contains(subjectId)) return Task.FromResult(KmsDecryptResult.Destroyed);
            return Task.FromResult(KmsDecryptResult.Found(Unseal(subjectId, ciphertext)));
        }
    }

    public Task<IReadOnlyList<KmsBatchEncryptItem>> EncryptBatchAsync(string subjectId, IReadOnlyList<byte[]> plaintexts, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EncryptCalls++;
            if (!_keys.Contains(subjectId)) throw new KmsKeyNotFoundException(subjectId);
            IReadOnlyList<KmsBatchEncryptItem> items = [.. plaintexts.Select(p => new KmsBatchEncryptItem(Seal(subjectId, p), null))];
            return Task.FromResult(items);
        }
    }

    public Task<KmsBatchDecryptResult> DecryptBatchAsync(string subjectId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
    {
        lock (_lock)
        {
            DecryptBatchCalls++;
            if (!_keys.Contains(subjectId)) return Task.FromResult(KmsBatchDecryptResult.Destroyed);
            return Task.FromResult(KmsBatchDecryptResult.Found([.. ciphertexts.Select(c => new KmsBatchDecryptItem(Unseal(subjectId, c), null))]));
        }
    }

    public Task DestroyKeyAsync(string subjectId, CancellationToken ct = default)
    {
        lock (_lock) _keys.Remove(subjectId);
        return Task.CompletedTask;
    }

    private static string Seal(string subjectId, byte[] plaintext) => $"inmem:{subjectId}:{Convert.ToBase64String(plaintext)}";

    private static byte[] Unseal(string subjectId, string ciphertext)
    {
        var prefix = $"inmem:{subjectId}:";
        if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
            throw new KmsProtocolException($"ciphertext '{ciphertext}' was not produced for subject '{subjectId}' by this client");
        return Convert.FromBase64String(ciphertext[prefix.Length..]);
    }
}
