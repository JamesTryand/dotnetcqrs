using System.Text.Json;
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
public sealed record DataSubjectState(
    bool Erased,
    ErasureStage Stage = ErasureStage.None,
    bool LegalHold = false,
    string? RequestedBy = null,
    string? Reason = null,
    DateTimeOffset? ReviewAt = null);

/// <summary>Where a subject's erasure request stands. <see cref="Erased"/> is terminal.</summary>
public enum ErasureStage
{
    /// <summary>No request has been made.</summary>
    None,
    /// <summary>Asked for; the retention policy has not ruled yet.</summary>
    Requested,
    /// <summary>The retention policy (or an administrator) said "not yet", with a review date.</summary>
    Held,
    /// <summary>Cleared to erase; the key is about to be destroyed.</summary>
    Approved,
    /// <summary>The key has been destroyed. Terminal.</summary>
    Erased,
}

/// <summary>Registration and constants for the built-in data-subject aggregate.
///
/// <para><b>Erasure is governed, not automatic</b> (venture-overview decision 0013): a request
/// is not an erasure. The lifecycle is
/// <c>None -> Requested -> (Held &lt;-&gt; Requested) -> Approved -> Erased</c>, and an explicit
/// <b>legal hold</b> blocks approval and erasure whatever the stage. Whether a request may be
/// approved is a retention-policy question (<see cref="IRetentionPolicy"/>), answered outside this
/// pure decider by <see cref="ErasureGovernor"/>.</para>
///
/// <para><b>What the events carry.</b> Reasons are stored in the clear on the subject's own
/// stream and survive the erasure (the stream is never rewritten), so a reason must be a short
/// code or non-identifying text, never personal data. Reasons over
/// <see cref="MaxReasonLength"/> characters are refused.</para>
///
/// <para><c>EraseSubject</c> remains as the administrator's direct override (and as the
/// governor's final step). It is refused while a legal hold stands, and hosts must authorise it
/// as tightly as the other commands: a command with no declared policy is allowed by the generic
/// gateway.</para></summary>
public static class DataSubject
{
    public const string Aggregate = "dataSubject";

    // Commands.
    public const string RequestErasureCommand = "RequestErasure";
    public const string HoldErasureCommand = "HoldErasure";
    public const string ApproveErasureCommand = "ApproveErasure";
    public const string PlaceLegalHoldCommand = "PlaceLegalHold";
    public const string ReleaseLegalHoldCommand = "ReleaseLegalHold";
    public const string EraseSubjectCommand = "EraseSubject";

    // Events.
    public const string ErasureRequestedEvent = "ErasureRequested";
    public const string ErasureHeldEvent = "ErasureHeld";
    public const string ErasureApprovedEvent = "ErasureApproved";
    public const string LegalHoldPlacedEvent = "LegalHoldPlaced";
    public const string LegalHoldReleasedEvent = "LegalHoldReleased";
    public const string SubjectErasedEvent = "SubjectErased";

    /// <summary>Longest reason accepted.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>The roles that should be allowed to send each command through a host's gateway, for a host whose
    /// roles are named <c>manager</c> and <c>administrator</c>: anyone who runs the roster may <i>ask</i>; only an
    /// administrator may hold, approve, place or release a legal hold, or erase directly. Null for a command this
    /// aggregate does not have. <b>A host must declare a policy for every one of these:</b> the generic gateway
    /// allows a command that has none, and erasure cannot be undone.</summary>
    public static IReadOnlyList<string>? DefaultRequiredRoles(string command) => command switch
    {
        RequestErasureCommand => ["manager", "administrator"],
        HoldErasureCommand or ApproveErasureCommand or PlaceLegalHoldCommand or ReleaseLegalHoldCommand or EraseSubjectCommand => ["administrator"],
        _ => null,
    };

    /// <summary>Every command the aggregate accepts, for hosts that declare policies in a loop.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        [RequestErasureCommand, HoldErasureCommand, ApproveErasureCommand, PlaceLegalHoldCommand, ReleaseLegalHoldCommand, EraseSubjectCommand];

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed record CommandPayload(string? Reason, DateTimeOffset? ReviewAt);

    private sealed record EventPayload(string? RequestedBy, string? Reason, DateTimeOffset? ReviewAt);

    private static CommandPayload ReadCommand(Command cmd) =>
        string.IsNullOrWhiteSpace(cmd.Payload)
            ? new CommandPayload(null, null)
            : JsonSerializer.Deserialize<CommandPayload>(cmd.Payload, Json) ?? new CommandPayload(null, null);

    private static string? CheckedReason(string? reason, bool required)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
            return required ? throw new InvalidOperationException("a reason is required") : null;
        if (reason.Length > MaxReasonLength)
            throw new InvalidOperationException($"reason is limited to {MaxReasonLength} characters; use a short code, not personal data");
        return reason;
    }

    private static NewEvent Event(string type, object payload) =>
        new(type, JsonSerializer.Serialize(payload, Json));

    private static string At(Command cmd) =>
        string.IsNullOrEmpty(cmd.Now) ? DateTimeOffset.UtcNow.ToString("O") : cmd.Now;

    /// <summary>The pure decider. The stream id IS the subject id, so nothing in the
    /// payload has to name it — and no PII ever reaches this aggregate.</summary>
    public static Decider<DataSubjectState> Create() => new()
    {
        InitialState = () => new DataSubjectState(false),
        Decide = Decide,
        Evolve = Evolve,
    };

    private static IReadOnlyList<NewEvent> Decide(DataSubjectState state, Command cmd)
    {
        switch (cmd.Name)
        {
            // Idempotent: erasing an already-erased subject appends nothing rather than
            // throwing, so a retried erasure request is harmless.
            case EraseSubjectCommand:
                if (state.Erased) return [];
                if (state.LegalHold) throw new InvalidOperationException("a legal hold is in place; the subject cannot be erased");
                return [new NewEvent(SubjectErasedEvent, "{}")];

            case RequestErasureCommand:
            {
                // A request is idempotent while one is open, and meaningless after erasure.
                if (state.Erased || state.Stage != ErasureStage.None) return [];
                var reason = CheckedReason(ReadCommand(cmd).Reason, required: false);
                return [Event(ErasureRequestedEvent, new { requestedBy = cmd.Actor, reason, requestedAt = At(cmd) })];
            }

            case HoldErasureCommand:
            {
                if (state.Erased) throw new InvalidOperationException("the subject has already been erased");
                if (state.Stage is not (ErasureStage.Requested or ErasureStage.Held))
                    throw new InvalidOperationException("there is no open erasure request to hold");
                var payload = ReadCommand(cmd);
                var reason = CheckedReason(payload.Reason, required: true);
                // The same hold restated (a redelivered event, a policy re-run): nothing new to record.
                if (state.Stage == ErasureStage.Held && state.Reason == reason && state.ReviewAt == payload.ReviewAt) return [];
                return [Event(ErasureHeldEvent, new { reason, reviewAt = payload.ReviewAt, heldBy = cmd.Actor, heldAt = At(cmd) })];
            }

            case ApproveErasureCommand:
                if (state.Erased) throw new InvalidOperationException("the subject has already been erased");
                if (state.Stage == ErasureStage.Approved) return [];
                if (state.Stage is not (ErasureStage.Requested or ErasureStage.Held))
                    throw new InvalidOperationException("there is no open erasure request to approve");
                if (state.LegalHold) throw new InvalidOperationException("a legal hold is in place; the request cannot be approved");
                return [Event(ErasureApprovedEvent, new { approvedBy = cmd.Actor, approvedAt = At(cmd) })];

            case PlaceLegalHoldCommand:
            {
                if (state.Erased) throw new InvalidOperationException("the subject has already been erased");
                if (state.LegalHold) return [];
                var reason = CheckedReason(ReadCommand(cmd).Reason, required: true);
                return [Event(LegalHoldPlacedEvent, new { reason, placedBy = cmd.Actor, placedAt = At(cmd) })];
            }

            case ReleaseLegalHoldCommand:
                if (!state.LegalHold) return [];
                return [Event(LegalHoldReleasedEvent, new { releasedBy = cmd.Actor, releasedAt = At(cmd) })];

            default:
                throw new InvalidOperationException($"unknown command: {cmd.Name}");
        }
    }

    private static DataSubjectState Evolve(DataSubjectState state, Event ev)
    {
        EventPayload Read() => JsonSerializer.Deserialize<EventPayload>(
            string.IsNullOrWhiteSpace(ev.Data) ? "{}" : ev.Data, Json) ?? new EventPayload(null, null, null);

        return ev.Type switch
        {
            ErasureRequestedEvent => state with { Stage = ErasureStage.Requested, RequestedBy = Read().RequestedBy, Reason = Read().Reason, ReviewAt = null },
            ErasureHeldEvent => state with { Stage = ErasureStage.Held, Reason = Read().Reason, ReviewAt = Read().ReviewAt },
            ErasureApprovedEvent => state with { Stage = ErasureStage.Approved },
            LegalHoldPlacedEvent => state with { LegalHold = true },
            LegalHoldReleasedEvent => state with { LegalHold = false },
            SubjectErasedEvent => state with { Erased = true, Stage = ErasureStage.Erased },
            _ => state,
        };
    }

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
