using System.Net;
using System.Security.Cryptography;

namespace DotnetCqrs.Crypto;

/// <summary>An <see cref="IKmsClient"/> with no facade behind it, for scenario
/// verification, generator tests and local dry runs. It honours the facade's contract
/// (keys must be ensured before encrypt; a destroyed key makes every ciphertext under
/// it <see cref="KmsKeyState.Destroyed"/>; destroy is idempotent) but its "ciphertext"
/// is a reversible, self-describing wrapper — <c>inmem:{subject}:{base64 plaintext}</c> —
/// not encryption. <b>Never register this in a real host.</b> A production host that
/// has no <c>KMS_FACADE_URL</c> must fail closed (no protector at all), not fall back
/// to this.
///
/// <para>Index keys are real HMAC-SHA256 keys, random per name and version and held only in
/// this instance, with output in the facade's <c>vault:vN:&lt;base64&gt;</c> shape. So hashes are
/// deterministic within one instance, which is all a scenario run needs.
/// <see cref="RotateIndexKey"/> stands in for ops' <c>vault.sh rotate-index-key</c>.</para></summary>
public sealed class InMemoryKmsClient : IKmsClient
{
    private readonly HashSet<string> _keys = [];
    private readonly Dictionary<string, List<byte[]>> _indexKeys = [];
    private readonly Lock _lock = new();

    public int EncryptCalls { get; private set; }
    public int DecryptBatchCalls { get; private set; }
    public int HmacCalls { get; private set; }

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

    public Task EnsureIndexKeyAsync(string name, CancellationToken ct = default)
    {
        lock (_lock)
            if (!_indexKeys.ContainsKey(name)) _indexKeys[name] = [RandomNumberGenerator.GetBytes(32)];
        return Task.CompletedTask;
    }

    public Task<int> GetIndexKeyVersionAsync(string name, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(IndexKey(name).Count);
    }

    public Task<string> HmacAsync(string name, byte[] input, int? keyVersion = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            HmacCalls++;
            var (version, key) = IndexKeyVersion(name, keyVersion);
            return Task.FromResult(Hmac(version, key, input));
        }
    }

    public Task<IReadOnlyList<KmsBatchHmacItem>> HmacBatchAsync(string name, IReadOnlyList<byte[]> inputs, int? keyVersion = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            HmacCalls++;
            var (version, key) = IndexKeyVersion(name, keyVersion);
            IReadOnlyList<KmsBatchHmacItem> items = [.. inputs.Select(i => new KmsBatchHmacItem(Hmac(version, key, i), null))];
            return Task.FromResult(items);
        }
    }

    /// <summary>Adds a new version of the named index key (ops' <c>vault.sh
    /// rotate-index-key</c>); earlier versions stay usable, as in Transit.</summary>
    public void RotateIndexKey(string name)
    {
        lock (_lock) IndexKey(name).Add(RandomNumberGenerator.GetBytes(32));
    }

    private List<byte[]> IndexKey(string name) =>
        _indexKeys.TryGetValue(name, out var versions) ? versions : throw new KmsIndexKeyNotFoundException(name);

    // Null or 0 = latest; anything else outside 1..latest is the facade's 400.
    private (int Version, byte[] Key) IndexKeyVersion(string name, int? keyVersion)
    {
        var versions = IndexKey(name);
        var version = keyVersion is null or 0 ? versions.Count : keyVersion.Value;
        if (version < 1 || version > versions.Count)
            throw new HttpRequestException($"index key '{name}' has no version {version}", null, HttpStatusCode.BadRequest);
        return (version, versions[version - 1]);
    }

    private static string Hmac(int version, byte[] key, byte[] input) =>
        $"vault:v{version}:{Convert.ToBase64String(HMACSHA256.HashData(key, input))}";

    private static string Seal(string subjectId, byte[] plaintext) => $"inmem:{subjectId}:{Convert.ToBase64String(plaintext)}";

    private static byte[] Unseal(string subjectId, string ciphertext)
    {
        var prefix = $"inmem:{subjectId}:";
        if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
            throw new KmsProtocolException($"ciphertext '{ciphertext}' was not produced for subject '{subjectId}' by this client");
        return Convert.FromBase64String(ciphertext[prefix.Length..]);
    }
}
