using System.Text;
using DotnetCqrs.Crypto;

namespace DotnetCqrs.Tests.Crypto;

public class KmsClientTests
{
    private static (KmsClient Client, FakeKmsHandler Handler) MakeClient()
    {
        var handler = new FakeKmsHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://kms.test/") };
        return (new KmsClient(http), handler);
    }

    [Fact]
    public async Task Encrypt_then_decrypt_round_trips_the_plaintext()
    {
        var (client, _) = MakeClient();
        await client.EnsureKeyAsync("subject-1");

        var ciphertext = await client.EncryptAsync("subject-1", Encoding.UTF8.GetBytes("hello pii"));
        var result = await client.DecryptAsync("subject-1", ciphertext);

        Assert.Equal(KmsKeyState.Found, result.State);
        Assert.Equal("hello pii", Encoding.UTF8.GetString(result.Plaintext!));
    }

    [Fact]
    public async Task Encrypt_without_ensuring_the_key_first_throws_KmsKeyNotFoundException()
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<KmsKeyNotFoundException>(
            () => client.EncryptAsync("never-ensured", Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public async Task Decrypt_after_the_key_is_destroyed_returns_Destroyed_not_an_exception()
    {
        var (client, _) = MakeClient();
        await client.EnsureKeyAsync("subject-1");
        var ciphertext = await client.EncryptAsync("subject-1", Encoding.UTF8.GetBytes("secret"));

        await client.DestroyKeyAsync("subject-1");
        var result = await client.DecryptAsync("subject-1", ciphertext);

        Assert.Equal(KmsKeyState.Destroyed, result.State);
        Assert.Null(result.Plaintext);
    }

    [Fact]
    public async Task EnsureKey_after_destroy_throws_SubjectErasedException()
    {
        var (client, _) = MakeClient();
        await client.EnsureKeyAsync("subject-1");
        await client.DestroyKeyAsync("subject-1");

        var ex = await Assert.ThrowsAsync<SubjectErasedException>(() => client.EnsureKeyAsync("subject-1"));
        Assert.Equal("subject-1", ex.SubjectId);
    }

    [Fact]
    public async Task DestroyKey_is_idempotent_for_a_never_existing_subject()
    {
        var (client, _) = MakeClient();
        await client.DestroyKeyAsync("never-existed"); // must not throw
    }

    [Fact]
    public async Task Batch_encrypt_then_batch_decrypt_round_trips_every_item_in_order()
    {
        var (client, _) = MakeClient();
        await client.EnsureKeyAsync("subject-1");
        var plaintexts = new[] { "a", "bb", "ccc" }.Select(Encoding.UTF8.GetBytes).ToList();

        var encrypted = await client.EncryptBatchAsync("subject-1", plaintexts);
        Assert.All(encrypted, r => Assert.True(r.Succeeded));

        var ciphertexts = encrypted.Select(r => r.Ciphertext!).ToList();
        var decrypted = await client.DecryptBatchAsync("subject-1", ciphertexts);

        Assert.Equal(KmsKeyState.Found, decrypted.State);
        var actual = decrypted.Items!.Select(i => Encoding.UTF8.GetString(i.Plaintext!));
        Assert.Equal(["a", "bb", "ccc"], actual);
    }

    [Fact]
    public async Task Batch_decrypt_after_destroy_reports_Destroyed_for_the_whole_call()
    {
        var (client, _) = MakeClient();
        await client.EnsureKeyAsync("subject-1");
        var encrypted = await client.EncryptBatchAsync("subject-1", [Encoding.UTF8.GetBytes("x")]);
        await client.DestroyKeyAsync("subject-1");

        var result = await client.DecryptBatchAsync("subject-1", [encrypted[0].Ciphertext!]);

        Assert.Equal(KmsKeyState.Destroyed, result.State);
        Assert.Null(result.Items);
    }

    [Fact]
    public async Task Batch_decrypt_a_per_item_error_does_not_fail_the_rest_of_the_batch()
    {
        var (client, handler) = MakeClient();
        await client.EnsureKeyAsync("subject-1");
        var encrypted = await client.EncryptBatchAsync("subject-1",
            [Encoding.UTF8.GetBytes("good-1"), Encoding.UTF8.GetBytes("bad"), Encoding.UTF8.GetBytes("good-2")]);
        handler.ForceItemErrors.Add(encrypted[1].Ciphertext!);

        var result = await client.DecryptBatchAsync("subject-1", [.. encrypted.Select(r => r.Ciphertext!)]);

        Assert.Equal(KmsKeyState.Found, result.State);
        Assert.True(result.Items![0].Succeeded);
        Assert.False(result.Items[1].Succeeded);
        Assert.Equal("forced test error", result.Items[1].Error);
        Assert.True(result.Items[2].Succeeded);
    }

    [Fact]
    public async Task Index_key_hmac_is_deterministic_per_version_and_pins_the_requested_version()
    {
        var (client, handler) = MakeClient();
        await client.EnsureIndexKeyAsync("app");
        await client.EnsureIndexKeyAsync("app"); // idempotent
        Assert.Equal(1, await client.GetIndexKeyVersionAsync("app"));

        var input = Encoding.UTF8.GetBytes("alice@example.com");
        var first = await client.HmacAsync("app", input, keyVersion: 1);
        Assert.StartsWith("vault:v1:", first);
        Assert.Equal(first, await client.HmacAsync("app", input, keyVersion: 1));

        handler.RotateIndexKey("app");
        Assert.Equal(2, await client.GetIndexKeyVersionAsync("app"));
        // Pinned: still the version-1 hash after the rotation.
        Assert.Equal(first, await client.HmacAsync("app", input, keyVersion: 1));
        var latest = await client.HmacAsync("app", input);
        Assert.StartsWith("vault:v2:", latest);
        Assert.NotEqual(first, latest);
        // Omitted on the wire when null (= latest), sent when pinned.
        Assert.Equal([1, 1, 1, null], handler.HmacKeyVersions);
    }

    [Fact]
    public async Task Hmac_batch_matches_single_hmac_item_for_item()
    {
        var (client, _) = MakeClient();
        await client.EnsureIndexKeyAsync("app");
        byte[][] inputs = [Encoding.UTF8.GetBytes("ali"), Encoding.UTF8.GetBytes("alic"), Encoding.UTF8.GetBytes("alice")];

        var batch = await client.HmacBatchAsync("app", inputs, keyVersion: 1);

        Assert.Equal(3, batch.Count);
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(await client.HmacAsync("app", inputs[i], keyVersion: 1), batch[i].Hmac);
    }

    [Fact]
    public async Task Index_key_calls_on_a_missing_key_throw_KmsIndexKeyNotFoundException()
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<KmsIndexKeyNotFoundException>(() => client.GetIndexKeyVersionAsync("missing"));
        await Assert.ThrowsAsync<KmsIndexKeyNotFoundException>(() => client.HmacAsync("missing", [1]));
        await Assert.ThrowsAsync<KmsIndexKeyNotFoundException>(() => client.HmacBatchAsync("missing", [[1]]));
    }

    [Fact]
    public async Task Hmac_at_a_version_the_key_does_not_have_is_an_http_error()
    {
        var (client, _) = MakeClient();
        await client.EnsureIndexKeyAsync("app");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.HmacAsync("app", [1], keyVersion: 5));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task InMemoryKmsClient_index_keys_behave_like_the_facade()
    {
        var kms = new InMemoryKmsClient();
        await Assert.ThrowsAsync<KmsIndexKeyNotFoundException>(() => kms.HmacAsync("app", [1]));
        await kms.EnsureIndexKeyAsync("app");
        var v1 = await kms.HmacAsync("app", [1, 2, 3], keyVersion: 1);
        Assert.StartsWith("vault:v1:", v1);

        kms.RotateIndexKey("app");

        Assert.Equal(2, await kms.GetIndexKeyVersionAsync("app"));
        Assert.Equal(v1, (await kms.HmacBatchAsync("app", [[1, 2, 3]], keyVersion: 1)).Single().Hmac);
        Assert.StartsWith("vault:v2:", await kms.HmacAsync("app", [1, 2, 3]));
        await Assert.ThrowsAsync<HttpRequestException>(() => kms.HmacAsync("app", [1], keyVersion: 3));
    }
}
