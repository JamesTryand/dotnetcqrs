using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Crypto;

/// <summary>The core fold of a data subject's stream. A module's events leave it unchanged.
///
/// <para><b><see cref="Erased"/> is terminal.</b> A person who is erased and later returns is a NEW subject with a new
/// id and a new key, never a reactivation of this one. Reuse would gain nothing (destroying a key destroys its
/// material, so a new key of the same name cannot read the old ciphertext) and would undo the erasure by reconnecting
/// a live identity to the history erasure severed. Subject ids are therefore opaque, never derived from personal data,
/// and never reused.</para></summary>
/// <param name="Requested">An erasure has been asked for. It stays true: only the erasure closes a request.</param>
public sealed record DataSubjectState(
    bool Erased,
    bool Requested = false,
    string? RequestedBy = null,
    string? Reason = null);

/// <summary>The data subject's own aggregate, the one this library ships rather than generates. Erasure is not a
/// property of any domain aggregate that happens to mention a person: an order's stream holds the customer's email,
/// but the customer outlives the order and is erased independently of it. So the subject gets its own stream (the
/// stream id IS the subject id), and every reader learns of an erasure the way it learns anything else: from an event.
///
/// <para>The core has two commands (cqrs-system-contracts subject-erasure). <c>RequestErasure</c> records that
/// someone asked, and erases nothing. <c>EraseSubject</c> records the erasure, which <see cref="SubjectKeyDestroyer"/>
/// turns into a destroyed key. What decides a request (retention duties, legal holds, review dates) belongs to a
/// module, such as DotnetCqrs.Retention, which registers its own decider over this one and only ever adds refusals
/// to it. With no module, nothing acts on a request until the host does.</para>
///
/// <para>A reason is stored in the clear on the subject's own stream and survives the erasure (the stream is never
/// rewritten), so it must be a short code or non-identifying text, never personal data. Reasons over
/// <see cref="MaxReasonLength"/> characters are refused. Hosts must authorise these commands: the gateway refuses
/// every one of them until an authorization policy is wired.</para></summary>
public static class DataSubject
{
    public const string Aggregate = "dataSubject";

    // Commands.
    public const string RequestErasureCommand = "RequestErasure";
    public const string EraseSubjectCommand = "EraseSubject";

    // Events.
    public const string ErasureRequestedEvent = "ErasureRequested";
    public const string SubjectErasedEvent = "SubjectErased";

    /// <summary>Longest reason accepted.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>Every command the core aggregate accepts.</summary>
    public static IReadOnlyList<string> Commands { get; } = [RequestErasureCommand, EraseSubjectCommand];

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed record Payload(string? Reason, string? RequestedBy);

    private static Payload Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new Payload(null, null) : JsonSerializer.Deserialize<Payload>(json, Json) ?? new Payload(null, null);

    /// <summary>Trims a reason, and refuses one that is missing (when <paramref name="required"/>) or too long to be a
    /// code. An empty reason is no reason.</summary>
    public static string? CheckReason(string? reason, bool required)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            return required ? throw new InvalidOperationException("a reason is required") : null;
        if (reason.Length > MaxReasonLength)
            throw new InvalidOperationException($"reason is limited to {MaxReasonLength} characters; use a short code, not personal data");
        return reason;
    }

    /// <summary>The command's time. It arrives on the command (DeciderRegistry stamps it); a decider never reads the
    /// clock, so a command with no time is refused rather than given the current one.</summary>
    public static string TimeOf(Command cmd) =>
        string.IsNullOrEmpty(cmd.Now) ? throw new InvalidOperationException("the command carries no time (Command.Now)") : cmd.Now;

    /// <summary>The core's pure decider. No personal data ever reaches this aggregate.</summary>
    public static Decider<DataSubjectState> Create() => new()
    {
        InitialState = () => new DataSubjectState(false),
        Decide = Decide,
        Evolve = Evolve,
    };

    /// <summary>Registers the core aggregate, unless a <c>dataSubject</c> decider is registered already: a module that
    /// governs erasure registers its own, which adds refusals to this one, and the order a host wires them in must not
    /// drop them. A host that stores any <c>field.pii</c> value needs this, or nothing can ever be erased.</summary>
    public static void RegisterDataSubjects(this DeciderRegistry registry)
    {
        if (!registry.IsRegistered(Aggregate)) registry.Register(Aggregate, Create());
    }

    /// <summary>The core commands a subject in <paramref name="state"/> would accept and record an event for: what a
    /// screen may offer, before the caller's role is considered.</summary>
    public static IReadOnlyList<string> AcceptedCommands(DataSubjectState state) =>
        state.Erased ? [] : state.Requested ? [EraseSubjectCommand] : [RequestErasureCommand, EraseSubjectCommand];

    /// <summary>Decides a core command. A module deciding the same aggregate calls this for these two once its own
    /// refusals have passed.</summary>
    public static IReadOnlyList<NewEvent> Decide(DataSubjectState state, Command cmd)
    {
        switch (cmd.Name)
        {
            case EraseSubjectCommand:
                // Idempotent: a retried erasure appends nothing rather than throwing.
                return state.Erased ? [] : [new NewEvent(SubjectErasedEvent, "{}")];

            case RequestErasureCommand:
            {
                // Idempotent while one is open, meaningless after erasure.
                if (state.Erased || state.Requested) return [];
                var at = TimeOf(cmd);
                var reason = CheckReason(Read(cmd.Payload).Reason, required: false);
                return [new NewEvent(ErasureRequestedEvent, JsonSerializer.Serialize(new { requestedBy = cmd.Actor, reason, requestedAt = at }, Json))];
            }

            default:
                throw new InvalidOperationException($"unknown command: {cmd.Name}");
        }
    }

    /// <summary>Folds a core event; any other event leaves <paramref name="state"/> unchanged.</summary>
    public static DataSubjectState Evolve(DataSubjectState state, Event ev) => ev.Type switch
    {
        ErasureRequestedEvent => state with { Requested = true, RequestedBy = Read(ev.Data).RequestedBy, Reason = Read(ev.Data).Reason },
        SubjectErasedEvent => state with { Erased = true },
        _ => state,
    };
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
/// erasure that lands immediately afterwards. The facade closes that: it refuses to
/// re-create a key for a subject it has already destroyed one for (<c>409</c>), which
/// <see cref="KmsClient.EnsureKeyAsync"/> surfaces as the same
/// <see cref="SubjectErasedException"/> this check throws.</para></summary>
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
