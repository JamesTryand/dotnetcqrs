using System.Text.Json;

namespace DotnetCqrs.Crypto;

/// <summary>Reveals one stored pii text field for a search index (the generated
/// <c>{Collection}SearchIndex</c> and <c>{Collection}HashedIndex</c>).</summary>
public static class PiiSearchReveal
{
    /// <summary>The field's plaintext and subject, or null when it is absent, null, or its
    /// subject is erased (key destroyed): a value that can't be read is not indexed. A stored
    /// plaintext (not the envelope) is a bug upstream and throws.</summary>
    public static async Task<(string Plaintext, string Subject)?> RevealAsync(
        JsonElement value, string field, IKmsClient kms, PiiRevealCache? cache, CancellationToken ct)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var pii = value.Deserialize<Pii<string>>();
        if (pii is null) return null;
        if (pii.State == PiiState.Fresh)
            throw new InvalidOperationException($"{field} is pii but the stored event holds plaintext, not the envelope");
        var buffer = new PiiRevealBuffer(kms, cache);
        var reveal = pii.RevealAsync(buffer, ct);
        await buffer.FlushAsync(ct);
        var revealed = await reveal;
        return revealed.State == PiiState.Known ? (revealed.Value, revealed.SubjectId!) : null;
    }
}
