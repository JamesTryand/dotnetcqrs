using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// Milestone D1: the data subject's own lifecycle. Erasure is a new FACT on the
/// subject's own stream plus a destroyed key — the domain log is never rewritten — and
/// the erased state is terminal, so a returning person is a new subject with a new id.
/// </summary>
public class DataSubjectTests
{
    private static async Task<(SqliteEventStore Store, DeciderRegistry Registry)> NewAsync()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterDataSubjects();
        return (store, registry);
    }

    [Fact]
    public async Task Erasing_a_subject_appends_one_SubjectErased_to_the_subjects_own_stream()
    {
        var (store, registry) = await NewAsync();

        await registry.HandleAsync(DataSubject.Aggregate, "cust-1", new Command(DataSubject.EraseSubjectCommand, "{}"));

        var stream = await store.LoadStreamAsync(DataSubject.Aggregate, "cust-1");
        var ev = Assert.Single(stream);
        Assert.Equal(DataSubject.SubjectErasedEvent, ev.Type);
        // The stream id IS the subject id, so no payload names the subject and this
        // aggregate never carries PII of its own.
        Assert.Equal("{}", ev.Data);
    }

    [Fact]
    public async Task Erasing_an_already_erased_subject_is_a_no_op_rather_than_a_failure()
    {
        var (store, registry) = await NewAsync();
        var command = new Command(DataSubject.EraseSubjectCommand, "{}");

        await registry.HandleAsync(DataSubject.Aggregate, "cust-1", command);
        var second = await registry.HandleAsync(DataSubject.Aggregate, "cust-1", command);

        Assert.Empty(second); // a retried erasure request must not fail
        Assert.Single(await store.LoadStreamAsync(DataSubject.Aggregate, "cust-1"));
    }

    [Fact]
    public async Task An_unknown_command_on_the_subject_stream_is_rejected()
    {
        var (_, registry) = await NewAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.HandleAsync(DataSubject.Aggregate, "cust-1", new Command("Reactivate", "{}")));
    }

    [Fact]
    public async Task SubjectStatus_reports_erased_only_for_the_erased_subject()
    {
        var (store, registry) = await NewAsync();
        await registry.HandleAsync(DataSubject.Aggregate, "cust-1", new Command(DataSubject.EraseSubjectCommand, "{}"));
        var status = new SubjectStatus(store);

        Assert.True(await status.IsErasedAsync("cust-1"));
        Assert.False(await status.IsErasedAsync("cust-2"));
    }

    [Fact]
    public async Task The_key_destroyer_destroys_the_erased_subjects_key_and_ignores_everything_else()
    {
        var kms = new InMemoryKmsClient();
        await kms.EnsureKeyAsync("cust-1");
        await kms.EnsureKeyAsync("cust-2");
        var destroyer = new SubjectKeyDestroyer(kms);

        await destroyer.ApplyAsync(ErasureEvent("cust-1"), CancellationToken.None);
        // An unrelated domain event must not touch any key, even one whose type collides.
        await destroyer.ApplyAsync(ErasureEvent("cust-2") with { Aggregate = "order" }, CancellationToken.None);

        var cipher2 = await kms.EncryptAsync("cust-2", [1, 2, 3]);
        Assert.Equal(KmsKeyState.Destroyed, (await kms.DecryptAsync("cust-1", "inmem:cust-1:AQID")).State);
        Assert.Equal(KmsKeyState.Found, (await kms.DecryptAsync("cust-2", cipher2)).State);
    }

    [Fact]
    public async Task Destroying_a_key_twice_is_harmless_because_delivery_is_at_least_once()
    {
        var kms = new InMemoryKmsClient();
        await kms.EnsureKeyAsync("cust-1");
        var destroyer = new SubjectKeyDestroyer(kms);

        await destroyer.ApplyAsync(ErasureEvent("cust-1"), CancellationToken.None);
        await destroyer.ApplyAsync(ErasureEvent("cust-1"), CancellationToken.None);

        Assert.Equal(KmsKeyState.Destroyed, (await kms.DecryptAsync("cust-1", "inmem:cust-1:AQID")).State);
    }

    [Fact]
    public void The_destroyer_is_a_consumer_with_a_stable_checkpoint_name()
    {
        Assert.Equal("pii:key-destroyer", ((IConsumer)new SubjectKeyDestroyer(new InMemoryKmsClient())).Name);
    }

    private static Event ErasureEvent(string subjectId) => new(
        1, $"erase-{subjectId}", DataSubject.Aggregate, subjectId, 1,
        DataSubject.SubjectErasedEvent, "{}", "{}", "1970-01-01T00:00:00.000Z");
}
