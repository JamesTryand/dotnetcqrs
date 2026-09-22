using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Crypto;

/// <summary>The data subject's own lifecycle — the one aggregate this library ships
/// rather than generates. Erasure is not a property of any domain aggregate that happens
/// to mention a person: an order's stream holds the customer's email, but the customer
/// outlives the order and is erased independently of it. So the subject gets its own
/// stream, and every reader learns of an erasure the way it learns anything else: from an
/// event.
///
/// <para>Deliberately NOT modelled in <c>eventmodelschema</c> (user's call, 2026-09-22):
/// erasure is a runtime concern, not something a document has to describe. If documents
/// ever need to say something about it, that decision gets revisited.</para>
///
/// <para><b><see cref="DataSubjectState.Erased"/> is terminal.</b> A person who is erased
/// and later returns is a NEW subject with a new id and a new key, never a reactivation
/// of this one. Reuse would gain nothing (destroying a key destroys its material, so a
/// new key of the same name cannot read the old ciphertext) and would undo the erasure by
/// reconnecting a live identity to the history erasure severed. Subject ids are therefore
/// opaque, never derived from personal data, and never reused.</para>
/// </summary>
public sealed record DataSubjectState(bool Erased);

/// <summary>Registration and constants for the built-in data-subject aggregate.</summary>
public static class DataSubject
{
    public const string Aggregate = "dataSubject";
    public const string EraseSubjectCommand = "EraseSubject";
    public const string SubjectErasedEvent = "SubjectErased";

    /// <summary>The pure decider. The stream id IS the subject id, so nothing in the
    /// payload has to name it — and no PII ever reaches this aggregate.</summary>
    public static Decider<DataSubjectState> Create() => new()
    {
        InitialState = () => new DataSubjectState(false),
        Decide = (state, cmd) => cmd.Name switch
        {
            // Idempotent: erasing an already-erased subject appends nothing rather than
            // throwing, so a retried erasure request is harmless.
            EraseSubjectCommand => state.Erased ? [] : [new NewEvent(SubjectErasedEvent, "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (state, ev) => ev.Type switch
        {
            SubjectErasedEvent => state with { Erased = true },
            _ => state,
        },
    };

    /// <summary>Registers the built-in aggregate. A host that stores any
    /// <c>field.pii</c> value needs this, or nothing can ever be erased.</summary>
    public static void RegisterDataSubjects(this DeciderRegistry registry) =>
        registry.Register(Aggregate, Create());
}

/// <summary>Answers "has this subject been erased?" from the subject's own stream.
/// Reading the local event store, not the key service: it is the same database the
/// command is about to append to, so it costs no network hop, and it stays correct while
/// the key service is unreachable.</summary>
public interface ISubjectStatus
{
    Task<bool> IsErasedAsync(string subjectId, CancellationToken ct = default);
}

/// <summary>The store-backed <see cref="ISubjectStatus"/>. Generated protectors take one
/// (optional) and refuse to encrypt fresh PII for an erased subject, so an erased
/// person's data cannot quietly reappear under their old id.
///
/// <para><b>What this does not close:</b> a write that passes this check can still race an
/// erasure that lands immediately afterwards, and <c>EnsureKey</c> would then re-create
/// the destroyed key. Only the facade can close that fully, by refusing to create a key
/// for a subject it has already destroyed one for — an open ask against
/// <c>platform/key-management-service</c>.</para></summary>
public sealed class SubjectStatus(IEventStore store) : ISubjectStatus
{
    public async Task<bool> IsErasedAsync(string subjectId, CancellationToken ct = default)
    {
        var stream = await store.LoadStreamAsync(DataSubject.Aggregate, subjectId, ct).ConfigureAwait(false);
        return stream.Any(e => e.Type == DataSubject.SubjectErasedEvent);
    }
}

/// <summary>Thrown when a command would store fresh PII for a subject whose lifecycle has
/// ended. Distinct from <see cref="PiiRedactedException"/>, which means "this stored value
/// can no longer be read": this one means "this person is gone; a returning person is a
/// new subject with a new id".</summary>
public sealed class SubjectErasedException(string subjectId)
    : Exception($"data subject '{subjectId}' has been erased; a returning subject needs a new id, not this one.")
{
    public string SubjectId { get; } = subjectId;
}

/// <summary>Destroys a subject's key when their erasure lands in the log. A plain
/// consumer rather than an <c>IReactor</c>: it performs an external effect instead of
/// dispatching a command. <c>DestroyKeyAsync</c> is idempotent and the engine retries a
/// failed event, so at-least-once delivery is exactly what this needs.</summary>
public sealed class SubjectKeyDestroyer(IKmsClient kms) : DotnetCqrs.Consumers.IConsumer
{
    /// <summary>The durable checkpoint name. <see cref="PiiCacheEvictor"/> reads it, so
    /// keep the two in step.</summary>
    public const string ConsumerName = "pii:key-destroyer";

    public string Name => ConsumerName;

    public Task ApplyAsync(Event ev, CancellationToken ct) =>
        ev.Type == DataSubject.SubjectErasedEvent && ev.Aggregate == DataSubject.Aggregate
            ? kms.DestroyKeyAsync(ev.AggregateId, ct)
            : Task.CompletedTask;
}
