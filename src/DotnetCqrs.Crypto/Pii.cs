using System.Text.Json;

namespace DotnetCqrs.Crypto;

public enum PiiState
{
    /// <summary>Carries ciphertext + subject id; nothing decrypted yet. The whole point
    /// of this state existing is that constructing one costs zero facade traffic.</summary>
    Pending,

    /// <summary>The owning subject's key has been destroyed (crypto-shredded) — this is
    /// the correct terminal state for an erased subject's data, not a fault.</summary>
    Redacted,

    /// <summary>The value has been revealed (or was constructed from a known value) and
    /// is available via <see cref="Pii{T}.Value"/>.</summary>
    Known,
}

/// <summary>A `field.pii`-marked value: lazy (constructing/copying one never touches the
/// facade) and, when revealed via a <see cref="PiiRevealBuffer"/>, batched with every
/// other pending reveal for the same subject. See
/// <c>platform/key-management-service/findings.md</c>'s "Phase 4 design" section for the
/// full rationale.
///
/// <c>T</c> is always one of dotnetcqrs's five folded field CLR types
/// (<c>string</c>/<c>double</c>/<c>bool</c>/<c>DateTime</c>/<c>JsonElement</c> — see
/// <c>GenerationSupport.CSharpType</c>); the encrypt/decrypt codec is plain
/// <see cref="JsonSerializer"/> round-tripping of <c>T</c> rather than a per-type codec,
/// since JSON already faithfully round-trips all five.
///
/// Deliberately has no public constructor and no way to read <see cref="Ciphertext"/>'s
/// bytes as plaintext without a real facade round trip (via <see cref="RevealAsync"/>) —
/// there is no code path in this type that can produce a plaintext value the facade
/// itself didn't hand back, matching the facade's own non-exportable-key guarantee one
/// layer up.</summary>
public sealed class Pii<T>
{
    public PiiState State { get; }
    public string? SubjectId { get; }
    public string? Ciphertext { get; }
    private readonly T? _value;

    private Pii(PiiState state, string? subjectId, string? ciphertext, T? value)
    {
        State = state;
        SubjectId = subjectId;
        Ciphertext = ciphertext;
        _value = value;
    }

    /// <summary>Wraps a ciphertext read back from storage (e.g. deserializing a stored
    /// event). Costs nothing until <see cref="RevealAsync"/> is called.</summary>
    public static Pii<T> FromCiphertext(string subjectId, string ciphertext) =>
        new(PiiState.Pending, subjectId, ciphertext, default);

    /// <summary>A value whose subject's key is already known to be destroyed (e.g. a
    /// caller checked the erasure ledger before even attempting to store/read the
    /// field). <paramref name="subjectId"/> is optional context for diagnostics only.</summary>
    public static Pii<T> Redacted(string? subjectId = null) =>
        new(PiiState.Redacted, subjectId, null, default);

    private static Pii<T> Known(string subjectId, string ciphertext, T value) =>
        new(PiiState.Known, subjectId, ciphertext, value);

    /// <summary>Throws unless <see cref="State"/> is <see cref="PiiState.Known"/> — call
    /// <see cref="RevealAsync"/> (and flush the buffer) first.</summary>
    public T Value => State == PiiState.Known
        ? _value!
        : throw new InvalidOperationException(
            $"Pii<{typeof(T).Name}> is {State}, not Known — call RevealAsync via a PiiRevealBuffer (and flush it) first.");

    /// <summary>Encrypts <paramref name="value"/> under <paramref name="subjectId"/>'s
    /// key (ensuring the key exists first) and returns a <see cref="Pii{T}"/> already in
    /// the <see cref="PiiState.Known"/> state, carrying the resulting ciphertext for
    /// storage. This is a direct, unbatched facade round trip — Milestone C decides where
    /// in the write path this actually gets called from (see <c>findings.md</c>'s open
    /// design question); this method itself makes no assumption about that.</summary>
    public static async Task<Pii<T>> EncryptAsync(IKmsClient client, string subjectId, T value, CancellationToken ct = default)
    {
        await client.EnsureKeyAsync(subjectId, ct).ConfigureAwait(false);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        var ciphertext = await client.EncryptAsync(subjectId, plaintext, ct).ConfigureAwait(false);
        return Known(subjectId, ciphertext, value);
    }

    /// <summary>A <see cref="PiiState.Known"/> or <see cref="PiiState.Redacted"/> value
    /// returns itself immediately (no-op, no facade traffic). A
    /// <see cref="PiiState.Pending"/> value enqueues into <paramref name="buffer"/> and
    /// returns a task that completes only once <see cref="PiiRevealBuffer.FlushAsync"/>
    /// runs — nothing happens until the caller flushes.</summary>
    public Task<Pii<T>> RevealAsync(PiiRevealBuffer buffer, CancellationToken ct = default) =>
        State == PiiState.Pending ? RevealPendingAsync(buffer, ct) : Task.FromResult(this);

    private async Task<Pii<T>> RevealPendingAsync(PiiRevealBuffer buffer, CancellationToken ct)
    {
        var outcome = await buffer.Enqueue(SubjectId!, Ciphertext!).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (outcome.Redacted) return Redacted(SubjectId);
        if (outcome.Error is not null)
            throw new KmsProtocolException($"decrypt-batch item error for subject '{SubjectId}': {outcome.Error}");
        var value = JsonSerializer.Deserialize<T>(outcome.Plaintext!)
            ?? throw new KmsProtocolException($"decrypted plaintext for subject '{SubjectId}' deserialized to null");
        return Known(SubjectId!, Ciphertext!, value);
    }
}
