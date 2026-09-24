using System.Net;
using System.Security.Cryptography;
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

    public int EncryptCallCount { get; private set; }
    public int DecryptBatchCallCount { get; private set; }
    public List<int> DecryptBatchItemCounts { get; } = [];

    // Index (HMAC) keys: name -> latest version. Key material is derived from name and
    // version, so hashes are stable across instances (a restarted host sees the same ones).
    private readonly Dictionary<string, int> _indexKeys = [];
    public int HmacCallCount { get; private set; }
    public List<int?> HmacKeyVersions { get; } = [];

    /// <summary>Ops' <c>vault.sh rotate-index-key</c>.</summary>
    public void RotateIndexKey(string name) => _indexKeys[name]++;

    public int? IndexKeyVersion(string name) => _indexKeys.TryGetValue(name, out var v) ? v : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (segments[1] == "index-keys")
        {
            // ["v1", "index-keys", "{name}", "{op}"?]
            var name = Uri.UnescapeDataString(segments[2]);
            var indexBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (request.Method.Method, segments.Length > 3 ? segments[3] : "") switch
            {
                ("PUT", "") => EnsureIndexKey(name),
                ("GET", "") => _indexKeys.TryGetValue(name, out var latest)
                    ? JsonResponse(HttpStatusCode.OK, $$"""{"latestVersion":{{latest}}}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound),
                ("POST", "hmac") => Hmac(name, indexBody!, batch: false),
                ("POST", "hmac-batch") => Hmac(name, indexBody!, batch: true),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
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
        EncryptCallCount++;
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

    private HttpResponseMessage EnsureIndexKey(string name)
    {
        _indexKeys.TryAdd(name, 1);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Hmac(string name, string body, bool batch)
    {
        HmacCallCount++;
        if (!_indexKeys.TryGetValue(name, out var latest)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var root = JsonDocument.Parse(body).RootElement;
        int? requested = root.TryGetProperty("keyVersion", out var kv) ? kv.GetInt32() : null;
        HmacKeyVersions.Add(requested);
        var version = requested is null or 0 ? latest : requested.Value;
        if (version < 1 || version > latest) return new HttpResponseMessage(HttpStatusCode.BadRequest);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}:{version}"));
        string Hash(string base64) => $"vault:v{version}:{Convert.ToBase64String(HMACSHA256.HashData(key, Convert.FromBase64String(base64)))}";
        if (!batch)
            return JsonResponse(HttpStatusCode.OK, $$"""{"hmac":"{{Hash(root.GetProperty("input").GetString()!)}}"}""");
        var results = root.GetProperty("inputs").EnumerateArray().Select(e => $$"""{"hmac":"{{Hash(e.GetString()!)}}"}""");
        return JsonResponse(HttpStatusCode.OK, $$"""{"results":[{{string.Join(",", results)}}]}""");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
