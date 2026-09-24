namespace DotnetCqrs.Crypto;

/// <summary>Which side of a Transit key's lifecycle a decrypt-shaped call landed on. Kept
/// as a typed outcome rather than an exception because a destroyed key is the expected,
/// correct result for an erased subject's data — see the facade's own contract doc
/// ("callers must treat this as 'data is gone,' not as an error to retry").</summary>
public enum KmsKeyState
{
    Found,
    Destroyed,
}

public sealed record KmsDecryptResult(KmsKeyState State, byte[]? Plaintext)
{
    public static readonly KmsDecryptResult Destroyed = new(KmsKeyState.Destroyed, null);
    public static KmsDecryptResult Found(byte[] plaintext) => new(KmsKeyState.Found, plaintext);
}

/// <summary>One item of a batch decrypt's per-item outcome. Exactly one of
/// <see cref="Plaintext"/>/<see cref="Error"/> is set, mirroring the facade's own
/// per-item response shape.</summary>
public sealed record KmsBatchDecryptItem(byte[]? Plaintext, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record KmsBatchDecryptResult(KmsKeyState State, IReadOnlyList<KmsBatchDecryptItem>? Items)
{
    public static readonly KmsBatchDecryptResult Destroyed = new(KmsKeyState.Destroyed, null);
    public static KmsBatchDecryptResult Found(IReadOnlyList<KmsBatchDecryptItem> items) => new(KmsKeyState.Found, items);
}

/// <summary>One item of a batch encrypt's per-item outcome. Unlike decrypt, a whole-call
/// "no key" outcome is not modeled as a typed state here — see
/// <see cref="IKmsClient.EncryptBatchAsync"/>'s own doc comment for why.</summary>
public sealed record KmsBatchEncryptItem(string? Ciphertext, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>One item of a batch HMAC's per-item outcome. Exactly one of
/// <see cref="Hmac"/>/<see cref="Error"/> is set, mirroring the facade's own per-item
/// response shape.</summary>
public sealed record KmsBatchHmacItem(string? Hmac, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Thin HTTP client over <c>platform/key-management-service</c>'s facade —
/// see that repo's <c>docs/facade-api-contract.md</c> for the exact contract this
/// implements against. Deliberately carries no caller→facade auth of its own: the
/// facade's own contract doc states that layer is still an open design question on
/// that issue, not this one's job to invent — see <c>findings.md</c>.</summary>
public interface IKmsClient
{
    /// <summary>Idempotent. Ensures a Transit key exists for <paramref name="subjectId"/>
    /// (and that it's deletion-allowed) — safe, and expected, to call on every event
    /// carrying a new subject, not just the first.</summary>
    Task EnsureKeyAsync(string subjectId, CancellationToken ct = default);

    /// <summary>Throws <see cref="KmsKeyNotFoundException"/> if no key exists yet for
    /// <paramref name="subjectId"/> — callers must call <see cref="EnsureKeyAsync"/>
    /// first; the facade deliberately does not auto-create on encrypt.</summary>
    Task<string> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default);

    /// <summary>Never throws for a destroyed key — see <see cref="KmsKeyState.Destroyed"/>.</summary>
    Task<KmsDecryptResult> DecryptAsync(string subjectId, string ciphertext, CancellationToken ct = default);

    /// <summary>1-1000 items, all encrypted under one subject's key in a single Vault
    /// round trip. Throws <see cref="KmsKeyNotFoundException"/> on the same "no key yet"
    /// condition <see cref="EncryptAsync"/> does — unlike decrypt, there is no legitimate
    /// "expected" reason for an encrypt call to hit a missing key, so this stays an
    /// exception rather than a typed outcome every caller has to check for.</summary>
    Task<IReadOnlyList<KmsBatchEncryptItem>> EncryptBatchAsync(string subjectId, IReadOnlyList<byte[]> plaintexts, CancellationToken ct = default);

    /// <summary>1-1000 items, all decrypted under one subject's key in a single Vault
    /// round trip. A destroyed key fails the whole call (every ciphertext under it is
    /// invalidated together) — see <see cref="KmsKeyState.Destroyed"/>.</summary>
    Task<KmsBatchDecryptResult> DecryptBatchAsync(string subjectId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default);

    /// <summary>The erasure operation. Idempotent — destroying an already-destroyed or
    /// never-existing key still succeeds, matching the facade's own contract.</summary>
    Task DestroyKeyAsync(string subjectId, CancellationToken ct = default);

    // Blind-index keys: the facade's own transit-index mount, one HMAC key per application,
    // never on the erasure path (there is no delete). Hashes of normalized PII feed the
    // searchable pii match modes (exact/prefix). The caller normalizes; the facade only
    // hashes bytes.

    /// <summary>Idempotent. Ensures the named HMAC index key exists.</summary>
    Task EnsureIndexKeyAsync(string name, CancellationToken ct = default);

    /// <summary>The named index key's latest version. An index built with an older version
    /// must be rebuilt (rotation is ops-only). Throws <see cref="KmsIndexKeyNotFoundException"/>
    /// if the key doesn't exist.</summary>
    Task<int> GetIndexKeyVersionAsync(string name, CancellationToken ct = default);

    /// <summary>HMAC of <paramref name="input"/> under the named key, as the whole
    /// <c>vault:vN:...</c> string (store and compare it whole). <paramref name="keyVersion"/>
    /// null means the latest; callers of a built index always pin the version it was built
    /// with. Throws <see cref="KmsIndexKeyNotFoundException"/> if the key doesn't exist.</summary>
    Task<string> HmacAsync(string name, byte[] input, int? keyVersion = null, CancellationToken ct = default);

    /// <summary>1-1000 inputs hashed in one round trip, all at <paramref name="keyVersion"/>.
    /// A per-item error doesn't fail the call.</summary>
    Task<IReadOnlyList<KmsBatchHmacItem>> HmacBatchAsync(string name, IReadOnlyList<byte[]> inputs, int? keyVersion = null, CancellationToken ct = default);
}
