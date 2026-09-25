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
    /// <summary>The facade's per-call item limit for <c>encrypt-batch</c>/<c>decrypt-batch</c>.</summary>
    public const int MaxBatchItems = 1000;

    private static string KeyPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/key";
    private static string EncryptPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/encrypt";
    private static string DecryptPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/decrypt";
    private static string EncryptBatchPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/encrypt-batch";
    private static string DecryptBatchPath(string subjectId) => $"v1/subjects/{Uri.EscapeDataString(subjectId)}/decrypt-batch";
    private static string IndexKeyPath(string name) => $"v1/index-keys/{Uri.EscapeDataString(name)}";

    public async Task EnsureKeyAsync(string subjectId, CancellationToken ct = default)
    {
        using var resp = await http.PutAsync(KeyPath(subjectId), content: null, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Conflict) throw new SubjectErasedException(subjectId);
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
        if (plaintexts.Count is 0 or > MaxBatchItems)
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
        if (ciphertexts.Count is 0 or > MaxBatchItems)
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

    public async Task EnsureIndexKeyAsync(string name, CancellationToken ct = default)
    {
        using var resp = await http.PutAsync(IndexKeyPath(name), content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<KmsErasurePage> ListErasuresAsync(long after, int limit = MaxBatchItems, CancellationToken ct = default)
    {
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after), "must be non-negative");
        if (limit is < 1 or > MaxBatchItems)
            throw new ArgumentOutOfRangeException(nameof(limit), "must be 1-1000, matching the facade's own limit");

        using var resp = await http.GetAsync($"v1/erasures?after={after}&limit={limit}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ErasuresResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("erasures response body was empty");
        return new KmsErasurePage(body.SubjectIds ?? throw new KmsProtocolException("erasures response has no subjectIds"), body.Next);
    }

    public async Task<int> GetIndexKeyVersionAsync(string name, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(IndexKeyPath(name), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new KmsIndexKeyNotFoundException(name);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<IndexKeyResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("index-key response body was empty");
        return body.LatestVersion;
    }

    public async Task<string> HmacAsync(string name, byte[] input, int? keyVersion = null, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync($"{IndexKeyPath(name)}/hmac", new HmacRequest(Convert.ToBase64String(input), keyVersion), ct)
            .ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new KmsIndexKeyNotFoundException(name);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<HmacResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("hmac response body was empty");
        return body.Hmac;
    }

    public async Task<IReadOnlyList<KmsBatchHmacItem>> HmacBatchAsync(string name, IReadOnlyList<byte[]> inputs, int? keyVersion = null, CancellationToken ct = default)
    {
        if (inputs.Count is 0 or > MaxBatchItems)
            throw new ArgumentOutOfRangeException(nameof(inputs), "must contain 1-1000 items, matching the facade's own limit");

        var request = new HmacBatchRequest([.. inputs.Select(Convert.ToBase64String)], keyVersion);
        using var resp = await http.PostAsJsonAsync($"{IndexKeyPath(name)}/hmac-batch", request, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new KmsIndexKeyNotFoundException(name);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<HmacBatchResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new KmsProtocolException("hmac-batch response body was empty");
        return [.. body.Results.Select(r => new KmsBatchHmacItem(r.Hmac, r.Error))];
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
    private sealed record IndexKeyResponse([property: JsonPropertyName("latestVersion")] int LatestVersion);
    private sealed record ErasuresResponse(
        [property: JsonPropertyName("subjectIds")] IReadOnlyList<string>? SubjectIds,
        [property: JsonPropertyName("next")] long Next);
    // keyVersion omitted (not null) means the latest, per the contract.
    private sealed record HmacRequest(
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("keyVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? KeyVersion);
    private sealed record HmacResponse([property: JsonPropertyName("hmac")] string Hmac);
    private sealed record HmacBatchRequest(
        [property: JsonPropertyName("inputs")] IReadOnlyList<string> Inputs,
        [property: JsonPropertyName("keyVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? KeyVersion);
    private sealed record HmacBatchResultItem(
        [property: JsonPropertyName("hmac")] string? Hmac,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record HmacBatchResponse([property: JsonPropertyName("results")] IReadOnlyList<HmacBatchResultItem> Results);
}
