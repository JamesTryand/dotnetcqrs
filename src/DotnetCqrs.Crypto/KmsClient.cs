using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Crypto;

/// <summary>HTTP implementation of <see cref="IKmsClient"/>. Expects
/// <paramref name="http"/>.BaseAddress already set to the facade's base URL (e.g.
/// <c>https://kms.bureau.tryand.uk:8080/</c>) — construction/TLS/lifetime of the
/// <see cref="HttpClient"/> is the caller's concern (DI's <c>IHttpClientFactory</c> in a
/// real host), same split every other facade caller in this repo uses.</summary>
public sealed class KmsClient(HttpClient http) : IKmsClient
{
    private static string KeyPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/key";
    private static string EncryptPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/encrypt";
    private static string DecryptPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/decrypt";
    private static string EncryptBatchPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/encrypt-batch";
    private static string DecryptBatchPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/decrypt-batch";

    public async Task EnsureKeyAsync(string subjectId, CancellationToken ct = default)
    {
        using var resp = await http.PutAsync(KeyPath(subjectId), content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> EncryptAsync(string subjectId, byte[] plaintext, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync(EncryptPath(subjectId), new EncryptRequest(Convert.ToBase64String(plaintext)), ct)
            .ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new KmsKeyNotFoundException(subjectId);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EncryptResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("encrypt response body was empty");
        return body.Ciphertext;
    }

    public async Task<KmsDecryptResult> DecryptAsync(string subjectId, string ciphertext, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync(DecryptPath(subjectId), new DecryptRequest(ciphertext), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return KmsDecryptResult.Destroyed;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DecryptResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("decrypt response body was empty");
        return KmsDecryptResult.Found(Convert.FromBase64String(body.Plaintext));
    }

    public async Task<IReadOnlyList<KmsBatchEncryptItem>> EncryptBatchAsync(string subjectId, IReadOnlyList<byte[]> plaintexts, CancellationToken ct = default)
    {
        if (plaintexts.Count is 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(plaintexts), "must contain 1-1000 items, matching the facade's own limit");

        var request = new EncryptBatchRequest([.. plaintexts.Select(Convert.ToBase64String)]);
        using var resp = await http.PostAsJsonAsync(EncryptBatchPath(subjectId), request, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new KmsKeyNotFoundException(subjectId);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<EncryptBatchResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("encrypt-batch response body was empty");
        return [.. body.Results.Select(r => new KmsBatchEncryptItem(r.Ciphertext, r.Error))];
    }

    public async Task<KmsBatchDecryptResult> DecryptBatchAsync(string subjectId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
    {
        if (ciphertexts.Count is 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(ciphertexts), "must contain 1-1000 items, matching the facade's own limit");

        var request = new DecryptBatchRequest(ciphertexts);
        using var resp = await http.PostAsJsonAsync(DecryptBatchPath(subjectId), request, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return KmsBatchDecryptResult.Destroyed;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DecryptBatchResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("decrypt-batch response body was empty");
        var items = body.Results.Select(r => new KmsBatchDecryptItem(
            r.Plaintext is null ? null : Convert.FromBase64String(r.Plaintext),
            r.Error));
        return KmsBatchDecryptResult.Found([.. items]);
    }

    public async Task DestroyKeyAsync(string subjectId, CancellationToken ct = default)
    {
        using var resp = await http.DeleteAsync(KeyPath(subjectId), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // Wire shapes match docs/facade-api-contract.md exactly (journal.bureau.tryand.uk/kms) —
    // kept private/internal to this file since nothing outside the HTTP transport needs them.
    private sealed record EncryptRequest([property: JsonPropertyName("plaintext")] string Plaintext);
    private sealed record EncryptResponse([property: JsonPropertyName("ciphertext")] string Ciphertext);
    private sealed record DecryptRequest([property: JsonPropertyName("ciphertext")] string Ciphertext);
    private sealed record DecryptResponse([property: JsonPropertyName("plaintext")] string Plaintext);
    private sealed record EncryptBatchRequest([property: JsonPropertyName("plaintexts")] IReadOnlyList<string> Plaintexts);
    private sealed record EncryptBatchResultItem(
        [property: JsonPropertyName("ciphertext")] string? Ciphertext,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record EncryptBatchResponse([property: JsonPropertyName("results")] IReadOnlyList<EncryptBatchResultItem> Results);
    private sealed record DecryptBatchRequest([property: JsonPropertyName("ciphertexts")] IReadOnlyList<string> Ciphertexts);
    private sealed record DecryptBatchResultItem(
        [property: JsonPropertyName("plaintext")] string? Plaintext,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record DecryptBatchResponse([property: JsonPropertyName("results")] IReadOnlyList<DecryptBatchResultItem> Results);
}
