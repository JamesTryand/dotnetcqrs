using System.Text.Json;
using DotnetCqrs.Crypto;

namespace DotnetCqrs.Tests.Crypto;

public class PiiTests
{
    private static (IKmsClient Client, FakeKmsHandler Handler) MakeClient()
    {
        var handler = new FakeKmsHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://kms.test/") };
        return (new KmsClient(http), handler);
    }

    private static async Task<Pii<T>> RoundTripThroughStorage<T>(IKmsClient client, string subjectId, T value)
    {
        var known = await Pii<T>.EncryptAsync(client, subjectId, value);
        // Simulate what actually crosses the event store: only subjectId + ciphertext
        // survive serialization — a freshly-encrypted value is never assumed still
        // "known" once it's round-tripped through storage.
        return Pii<T>.FromCiphertext(known.SubjectId!, known.Ciphertext!);
    }

    [Fact]
    public async Task Reveal_does_not_call_the_facade_until_the_buffer_is_flushed()
    {
        var (client, handler) = MakeClient();
        var pii = await RoundTripThroughStorage(client, "s1", "top secret");
        var buffer = new PiiRevealBuffer(client);

        var revealTask = pii.RevealAsync(buffer);

        Assert.Equal(0, handler.DecryptBatchCallCount);
        Assert.False(revealTask.IsCompleted);

        await buffer.FlushAsync();
        var revealed = await revealTask;

        Assert.Equal(1, handler.DecryptBatchCallCount);
        Assert.Equal(PiiState.Known, revealed.State);
        Assert.Equal("top secret", revealed.Value);
    }

    [Fact]
    public async Task Multiple_reveals_for_the_same_subject_flush_as_one_batch_call()
    {
        var (client, handler) = MakeClient();
        var a = await RoundTripThroughStorage(client, "s1", "alpha");
        var b = await RoundTripThroughStorage(client, "s1", "beta");
        var c = await RoundTripThroughStorage(client, "s1", "gamma");
        var buffer = new PiiRevealBuffer(client);

        var tasks = new[] { a.RevealAsync(buffer), b.RevealAsync(buffer), c.RevealAsync(buffer) };
        await buffer.FlushAsync();
        var revealed = await Task.WhenAll(tasks);

        Assert.Equal(1, handler.DecryptBatchCallCount);
        Assert.Equal(3, handler.DecryptBatchItemCounts.Single());
        Assert.Equal(["alpha", "beta", "gamma"], revealed.Select(r => r.Value));
    }

    [Fact]
    public async Task Reveals_for_different_subjects_flush_as_separate_batch_calls()
    {
        var (client, handler) = MakeClient();
        var a = await RoundTripThroughStorage(client, "s1", "alpha");
        var b = await RoundTripThroughStorage(client, "s2", "beta");
        var buffer = new PiiRevealBuffer(client);

        var tasks = new[] { a.RevealAsync(buffer), b.RevealAsync(buffer) };
        await buffer.FlushAsync();
        await Task.WhenAll(tasks);

        Assert.Equal(2, handler.DecryptBatchCallCount);
        Assert.All(handler.DecryptBatchItemCounts, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task A_pending_reveal_for_a_destroyed_subject_resolves_to_Redacted_not_an_exception()
    {
        var (client, _) = MakeClient();
        var pii = await RoundTripThroughStorage(client, "s1", "will be erased");
        await client.DestroyKeyAsync("s1");
        var buffer = new PiiRevealBuffer(client);

        var revealTask = pii.RevealAsync(buffer);
        await buffer.FlushAsync();
        var revealed = await revealTask;

        Assert.Equal(PiiState.Redacted, revealed.State);
        Assert.Throws<PiiRedactedException>(() => revealed.Value);
    }

    [Fact]
    public async Task A_per_item_batch_error_surfaces_as_KmsProtocolException_for_just_that_reveal()
    {
        var (client, handler) = MakeClient();
        var ok = await RoundTripThroughStorage(client, "s1", "fine");
        var bad = await RoundTripThroughStorage(client, "s1", "will error");
        handler.ForceItemErrors.Add(bad.Ciphertext!);
        var buffer = new PiiRevealBuffer(client);

        var okTask = ok.RevealAsync(buffer);
        var badTask = bad.RevealAsync(buffer);
        await buffer.FlushAsync();

        var okResult = await okTask;
        Assert.Equal("fine", okResult.Value);
        await Assert.ThrowsAsync<KmsProtocolException>(() => badTask);
    }

    [Fact]
    public async Task Known_and_Redacted_values_reveal_as_no_ops_with_zero_facade_calls()
    {
        var (client, handler) = MakeClient();
        var known = await Pii<string>.EncryptAsync(client, "s1", "already known");
        var redacted = Pii<string>.Redacted("s1");
        var buffer = new PiiRevealBuffer(client);

        var revealedKnown = await known.RevealAsync(buffer);
        var revealedRedacted = await redacted.RevealAsync(buffer);
        await buffer.FlushAsync(); // nothing queued; must be a safe no-op

        Assert.Same(known, revealedKnown);
        Assert.Same(redacted, revealedRedacted);
        Assert.Equal(0, handler.DecryptBatchCallCount);
    }

    [Fact]
    public async Task Round_trips_every_folded_CLR_type_a_PII_field_can_be()
    {
        var (client, _) = MakeClient();
        var buffer = new PiiRevealBuffer(client);

        var str = await RoundTripThroughStorage(client, "s1", "text value");
        var num = await RoundTripThroughStorage(client, "s1", 42.5);
        var flag = await RoundTripThroughStorage(client, "s1", true);
        var when = await RoundTripThroughStorage(client, "s1", new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc));
        var json = await RoundTripThroughStorage(client, "s1", JsonDocument.Parse("""{"k":"v"}""").RootElement);

        var strTask = str.RevealAsync(buffer);
        var numTask = num.RevealAsync(buffer);
        var flagTask = flag.RevealAsync(buffer);
        var whenTask = when.RevealAsync(buffer);
        var jsonTask = json.RevealAsync(buffer);

        await buffer.FlushAsync();

        Assert.Equal("text value", (await strTask).Value);
        Assert.Equal(42.5, (await numTask).Value);
        Assert.True((await flagTask).Value);
        Assert.Equal(new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc), (await whenTask).Value);
        Assert.Equal("v", (await jsonTask).Value.GetProperty("k").GetString());
    }
}
