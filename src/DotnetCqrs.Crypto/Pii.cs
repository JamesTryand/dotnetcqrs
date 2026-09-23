using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetCqrs.Deciders;

namespace DotnetCqrs.Crypto;

public enum PiiState
{
    /// <summary>Plaintext that has just entered the system (a command field) and has not
    /// been encrypted yet. Readable by <c>Decide</c>; refuses to serialise, so it can never
    /// reach the event store by accident.</summary>
    Fresh,

    /// <summary>Carries ciphertext + subject id; nothing decrypted yet. Constructing one
    /// costs zero facade traffic. Reading <see cref="Pii{T}.Value"/> throws
    /// <see cref="RevealRequiredException"/>, which <c>DeciderRegistry</c> turns into one
    /// batched reveal followed by a re-run of <c>Decide</c>.</summary>
    Pending,

    /// <summary>The owning subject's key has been destroyed (crypto-shredded) — the
    /// correct terminal state for an erased subject's data, not a fault.</summary>
    Redacted,

    /// <summary>Revealed (or freshly encrypted): the value is available and the
    /// ciphertext is known, so it serialises without another round trip.</summary>
    Known,
}

/// <summary>A `field.pii`-marked value. Lazy (constructing or copying one never touches
/// the facade), batched when revealed through a <see cref="PiiRevealBuffer"/>, and
/// fail-closed on the wire: the JSON converter writes only the ciphertext envelope
/// <c>{"$pii":{"s":subject,"c":ciphertext}}</c> and throws on a <see cref="PiiState.Fresh"/>
/// value, while reading accepts either that envelope (⇒ <see cref="PiiState.Pending"/>)
/// or a bare scalar (⇒ <see cref="PiiState.Fresh"/>, how a command's plaintext arrives).
///
/// <c>T</c> is always one of dotnetcqrs's five folded field CLR types
/// (<c>string</c>/<c>double</c>/<c>bool</c>/<c>DateTime</c>/<c>JsonElement</c>); the
/// encrypt/decrypt codec is plain <see cref="JsonSerializer"/> round-tripping of <c>T</c>.
///
/// There is no code path in this type that produces a plaintext value the facade itself
/// didn't hand back or a command didn't carry in, matching the facade's own
/// non-exportable-key guarantee one layer up.</summary>
[JsonConverter(typeof(PiiJsonConverterFactory))]
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

    /// <summary>Plaintext that has just arrived and must be encrypted before it can be
    /// stored. <c>Decide</c> may read it freely.</summary>
    public static Pii<T> Fresh(T value) => new(PiiState.Fresh, null, null, value);

    /// <summary>Wraps a ciphertext read back from storage. Costs nothing until revealed.</summary>
    public static Pii<T> FromCiphertext(string subjectId, string ciphertext) =>
        new(PiiState.Pending, subjectId, ciphertext, default);

    /// <summary>A value whose subject's key is known to be destroyed.</summary>
    public static Pii<T> Redacted(string? subjectId = null) =>
        new(PiiState.Redacted, subjectId, null, default);

    private static Pii<T> Known(string subjectId, string ciphertext, T value) =>
        new(PiiState.Known, subjectId, ciphertext, value);

    /// <summary>The plaintext, for a <see cref="PiiState.Fresh"/> or
    /// <see cref="PiiState.Known"/> value. A <see cref="PiiState.Pending"/> value throws
    /// <see cref="RevealRequiredException"/> (the registry reveals and re-decides); a
    /// <see cref="PiiState.Redacted"/> value throws <see cref="PiiRedactedException"/> —
    /// check <see cref="State"/> first when erasure is a legitimate branch.</summary>
    public T Value => State switch
    {
        PiiState.Fresh or PiiState.Known => _value!,
        PiiState.Pending => throw new RevealRequiredException(
            $"Pii<{typeof(T).Name}> for subject '{SubjectId}' has not been revealed yet."),
        _ => throw new PiiRedactedException(SubjectId),
    };

    /// <summary>Encrypts a <see cref="PiiState.Fresh"/> value under
    /// <paramref name="subjectId"/>'s key (ensuring the key exists), returning a
    /// <see cref="PiiState.Known"/> value that carries the ciphertext for storage.
    /// Any other state returns itself — already encrypted, or nothing to encrypt.</summary>
    public async Task<Pii<T>> EncryptAsync(IKmsClient client, string subjectId, CancellationToken ct = default)
    {
        if (State != PiiState.Fresh) return this;
        await client.EnsureKeyAsync(subjectId, ct).ConfigureAwait(false);
        var ciphertext = await client.EncryptAsync(subjectId, Encode(_value!), ct).ConfigureAwait(false);
        return Known(subjectId, ciphertext, _value!);
    }

    /// <summary>Convenience for a value not yet wrapped: <c>Fresh(value).EncryptAsync(...)</c>.</summary>
    public static Task<Pii<T>> EncryptAsync(IKmsClient client, string subjectId, T value, CancellationToken ct = default) =>
        Fresh(value).EncryptAsync(client, subjectId, ct);

    /// <summary>A <see cref="PiiState.Pending"/> value enqueues into
    /// <paramref name="buffer"/> and returns a task that completes only once
    /// <see cref="PiiRevealBuffer.FlushAsync"/> runs, or at once when the buffer's
    /// <see cref="PiiRevealCache"/> can answer it. Any other state returns itself
    /// immediately with no facade traffic.</summary>
    public Task<Pii<T>> RevealAsync(PiiRevealBuffer buffer, CancellationToken ct = default) =>
        State == PiiState.Pending ? RevealPendingAsync(buffer, ct) : Task.FromResult(this);

    private async Task<Pii<T>> RevealPendingAsync(PiiRevealBuffer buffer, CancellationToken ct)
    {
        var outcome = await buffer.Enqueue(SubjectId!, Ciphertext!).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (outcome.Redacted) return Redacted(SubjectId);
        if (outcome.Error is not null)
            throw new KmsProtocolException($"decrypt-batch item error for subject '{SubjectId}': {outcome.Error}");
        return Known(SubjectId!, Ciphertext!, Decode(outcome.Plaintext!));
    }

    internal static byte[] Encode(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    internal static T Decode(byte[] plaintext) => JsonSerializer.Deserialize<T>(plaintext)
        ?? throw new KmsProtocolException($"decrypted plaintext deserialized to a null {typeof(T).Name}");
}

/// <summary>A decision read a value whose subject has been erased. Distinct from
/// <see cref="RevealRequiredException"/>: no reveal can help, and the registry does not
/// retry. A decider that expects erasure as a normal branch checks
/// <see cref="Pii{T}.State"/> instead of reading <see cref="Pii{T}.Value"/>.</summary>
public sealed class PiiRedactedException(string? subjectId)
    : Exception($"PII for subject '{subjectId}' has been erased (key destroyed).")
{
    public string? SubjectId { get; } = subjectId;
}
