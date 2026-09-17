using System.Net;
using System.Text;
using System.Text.Json;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>In-memory stand-in for <c>platform/key-management-service</c>'s facade —
/// good enough to unit-test <c>KmsClient</c>/<c>Pii&lt;T&gt;</c>/<c>PiiRevealBuffer</c>
/// without a live Vault or facade, same spirit as the <c>kms</c> repo's own
/// fake-Vault-server tests for <c>internal/vaultauth</c>. Ciphertext shape is a fake but
/// self-checking format (<c>fake:{subjectId}:{base64 plaintext}</c>) — not real Vault
/// Transit output, just enough to prove the client/wrapper wiring is correct.</summary>
internal sealed class FakeKmsHandler : HttpMessageHandler
{
    private readonly HashSet<string> _keys = [];

    /// <summary>Ciphertext strings that should come back as a per-item batch error
    /// instead of a successful decrypt/encrypt result — set by a test before the call.</summary>
    public HashSet<string> ForceItemErrors { get; } = [];

    public int DecryptBatchCallCount { get; private set; }
    public List<int> DecryptBatchItemCounts { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        // ["v1", "subjects", "{id}", "{op}"]
        var subjectId = Uri.UnescapeDataString(segments[2]);
        var op = segments[3];
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return (request.Method.Method, op) switch
        {
            ("PUT", "key") => Ensure(subjectId),
            ("DELETE", "key") => Destroy(subjectId),
            ("POST", "encrypt") => Encrypt(subjectId, body!),
            ("POST", "decrypt") => Decrypt(subjectId, body!),
            ("POST", "encrypt-batch") => EncryptBatch(subjectId, body!),
            ("POST", "decrypt-batch") => DecryptBatch(subjectId, body!),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Ensure(string subjectId)
    {
        _keys.Add(subjectId);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Destroy(string subjectId)
    {
        _keys.Remove(subjectId);
        return new HttpResponseMessage(HttpStatusCode.NoContent); // idempotent, per contract
    }

    private string Seal(string subjectId, string base64Plaintext) => $"fake:{subjectId}:{base64Plaintext}";

    private HttpResponseMessage Encrypt(string subjectId, string body)
    {
        if (!_keys.Contains(subjectId)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var plaintext = JsonDocument.Parse(body).RootElement.GetProperty("plaintext").GetString()!;
        return JsonResponse(HttpStatusCode.OK, $$"""{"ciphertext":"{{Seal(subjectId, plaintext)}}"}""");
    }

    private HttpResponseMessage Decrypt(string subjectId, string body)
    {
        if (!_keys.Contains(subjectId)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var ciphertext = JsonDocument.Parse(body).RootElement.GetProperty("ciphertext").GetString()!;
        var plaintext = Unseal(subjectId, ciphertext);
        return JsonResponse(HttpStatusCode.OK, $$"""{"plaintext":"{{plaintext}}"}""");
    }

    private string Unseal(string subjectId, string ciphertext)
    {
        var prefix = $"fake:{subjectId}:";
        if (!ciphertext.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"ciphertext '{ciphertext}' does not belong to subject '{subjectId}'");
        return ciphertext[prefix.Length..];
    }

    private HttpResponseMessage EncryptBatch(string subjectId, string body)
    {
        if (!_keys.Contains(subjectId)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var plaintexts = JsonDocument.Parse(body).RootElement.GetProperty("plaintexts")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var results = plaintexts.Select(pt =>
        {
            var ciphertext = Seal(subjectId, pt);
            return ForceItemErrors.Contains(ciphertext)
                ? $$"""{"error":"forced test error"}"""
                : $$"""{"ciphertext":"{{ciphertext}}"}""";
        });
        return JsonResponse(HttpStatusCode.OK, $$"""{"results":[{{string.Join(",", results)}}]}""");
    }

    private HttpResponseMessage DecryptBatch(string subjectId, string body)
    {
        DecryptBatchCallCount++;
        if (!_keys.Contains(subjectId)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var ciphertexts = JsonDocument.Parse(body).RootElement.GetProperty("ciphertexts")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        DecryptBatchItemCounts.Add(ciphertexts.Count);
        var results = ciphertexts.Select(ct =>
            ForceItemErrors.Contains(ct)
                ? $$"""{"error":"forced test error"}"""
                : $$"""{"plaintext":"{{Unseal(subjectId, ct)}}"}""");
        return JsonResponse(HttpStatusCode.OK, $$"""{"results":[{{string.Join(",", results)}}]}""");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
