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
}
