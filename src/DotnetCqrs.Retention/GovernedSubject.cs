using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Retention;

/// <summary>Where a subject's erasure request stands. <see cref="Erased"/> is terminal.</summary>
public enum ErasureStage
{
    /// <summary>No request has been made.</summary>
    None,
    /// <summary>Asked for; the retention policy has not ruled yet.</summary>
    Requested,
    /// <summary>The retention policy (or a person sending HoldErasure) said "not yet", with or without a review date.</summary>
    Held,
    /// <summary>Cleared to erase; the erasure is about to be recorded.</summary>
    Approved,
    /// <summary>Erased. Terminal.</summary>
    Erased,
}

/// <summary>The governed fold of a data subject's stream: the core's (<see cref="DataSubjectState"/>), plus the stage,
/// the legal hold and the current hold.</summary>
/// <param name="HoldReason">Why the request is held (null unless <see cref="Stage"/> is Held).</param>
/// <param name="ReviewAt">When a held request is looked at again (null: not on a schedule).</param>
public sealed record GovernedSubjectState(
    DataSubjectState Core,
    ErasureStage Stage = ErasureStage.None,
    bool LegalHold = false,
    string? HoldReason = null,
    DateTimeOffset? ReviewAt = null)
{
    /// <summary>Whether the subject has been erased.</summary>
    public bool Erased => Core.Erased;
}

/// <summary>
/// Governed erasure over the core <c>dataSubject</c> aggregate (<see cref="DataSubject"/>), which has the request and
/// the erasure. A request is not an erasure: erasure is permanent, and a business is usually obliged to keep some
/// records about a person for a time (GDPR Article 17(3) exempts what the law requires kept). So a request goes through
/// a lifecycle, <c>None -> Requested -> (Held &lt;-&gt; asked again) -> Approved -> Erased</c>; an
/// <see cref="IRetentionPolicy"/>, owned by the business, says whether it may proceed; and a legal hold blocks
/// approval and erasure whatever the stage. <see cref="ErasureGovernor"/> drives each request to a conclusion.
///
/// <para>This decider only adds refusals to the core one: the core's two commands decide exactly as the core does
/// once this one's refusals have passed (<c>EraseSubject</c> is refused under a legal hold).</para>
///
/// <para>Who may send which command is the host's to declare in its gateway authorization: typically a wider group
/// may ask (<c>RequestErasure</c>) than may hold, approve, place or release a legal hold, or erase directly.</para>
/// </summary>
public static class GovernedSubject
{
    // Commands this module adds; RequestErasure and EraseSubject are the core's (DataSubject).
    public const string HoldErasureCommand = "HoldErasure";
    public const string ApproveErasureCommand = "ApproveErasure";
    public const string PlaceLegalHoldCommand = "PlaceLegalHold";
    public const string ReleaseLegalHoldCommand = "ReleaseLegalHold";

    // Events this module adds.
    public const string ErasureHeldEvent = "ErasureHeld";
    public const string ErasureApprovedEvent = "ErasureApproved";
    public const string LegalHoldPlacedEvent = "LegalHoldPlaced";
    public const string LegalHoldReleasedEvent = "LegalHoldReleased";

    /// <summary>Every command the governed aggregate accepts, the core's included.</summary>
    public static IReadOnlyList<string> Commands { get; } =
    [
        DataSubject.RequestErasureCommand, HoldErasureCommand, ApproveErasureCommand,
        PlaceLegalHoldCommand, ReleaseLegalHoldCommand, DataSubject.EraseSubjectCommand,
    ];

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed record Payload(string? Reason, DateTimeOffset? ReviewAt);

    private static Payload Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new Payload(null, null) : JsonSerializer.Deserialize<Payload>(json, Json) ?? new Payload(null, null);

    private static NewEvent Event(string type, object payload) => new(type, JsonSerializer.Serialize(payload, Json));

    /// <summary>The governed pure decider.</summary>
    public static Decider<GovernedSubjectState> Create() => new()
    {
        InitialState = () => new GovernedSubjectState(DataSubject.Create().InitialState()),
        Decide = Decide,
        Evolve = Evolve,
    };

    /// <summary>Registers the governed aggregate in place of the core's. <see cref="DataSubject.RegisterDataSubjects"/>
    /// keeps it, whichever is called first.</summary>
    public static void RegisterGovernedDataSubjects(this DeciderRegistry registry) =>
        registry.Register(DataSubject.Aggregate, Create());

    /// <summary>The commands a subject in <paramref name="state"/> would accept and record an event for: what a screen
    /// may offer, before the caller's role is considered. The same rules <c>Decide</c> applies, from the same function.
    /// Only what depends on the payload is left out: a hold restated with the same reason and review date records
    /// nothing, and a reason must be short.</summary>
    public static IReadOnlyList<string> AcceptedCommands(GovernedSubjectState state) =>
        Commands.Where(command => Judge(state, command).Verdict == Verdict.Records).ToList();

    private enum Verdict { Records, Ignored, Refused }

    private static (Verdict Verdict, string? Refusal) Judge(GovernedSubjectState state, string command)
    {
        const string alreadyErased = "the subject has already been erased";
        var open = state.Stage is ErasureStage.Requested or ErasureStage.Held;
        return command switch
        {
            DataSubject.EraseSubjectCommand when state.Erased => (Verdict.Ignored, null), // a retried erasure is harmless
            DataSubject.EraseSubjectCommand when state.LegalHold => (Verdict.Refused, "a legal hold is in place; the subject cannot be erased"),
            DataSubject.EraseSubjectCommand => (Verdict.Records, null),

            // idempotent while one is open, meaningless after erasure
            DataSubject.RequestErasureCommand when state.Erased || state.Core.Requested => (Verdict.Ignored, null),
            DataSubject.RequestErasureCommand => (Verdict.Records, null),

            HoldErasureCommand when state.Erased => (Verdict.Refused, alreadyErased),
            HoldErasureCommand when !open => (Verdict.Refused, "there is no open erasure request to hold"),
            HoldErasureCommand => (Verdict.Records, null),

            ApproveErasureCommand when state.Erased => (Verdict.Refused, alreadyErased),
            ApproveErasureCommand when state.Stage == ErasureStage.Approved => (Verdict.Ignored, null),
            ApproveErasureCommand when !open => (Verdict.Refused, "there is no open erasure request to approve"),
            ApproveErasureCommand when state.LegalHold => (Verdict.Refused, "a legal hold is in place; the request cannot be approved"),
            ApproveErasureCommand => (Verdict.Records, null),

            PlaceLegalHoldCommand when state.Erased => (Verdict.Refused, alreadyErased),
            PlaceLegalHoldCommand when state.LegalHold => (Verdict.Ignored, null),
            PlaceLegalHoldCommand => (Verdict.Records, null),

            ReleaseLegalHoldCommand when !state.LegalHold => (Verdict.Ignored, null),
            ReleaseLegalHoldCommand => (Verdict.Records, null),

            _ => (Verdict.Refused, $"unknown command: {command}"),
        };
    }

    private static IReadOnlyList<NewEvent> Decide(GovernedSubjectState state, Command cmd)
    {
        var (verdict, refusal) = Judge(state, cmd.Name);
        if (verdict == Verdict.Refused) throw new InvalidOperationException(refusal);
        if (verdict == Verdict.Ignored) return [];
        if (cmd.Name is DataSubject.RequestErasureCommand or DataSubject.EraseSubjectCommand)
            return DataSubject.Decide(state.Core, cmd);

        var at = DataSubject.TimeOf(cmd);
        switch (cmd.Name)
        {
            case HoldErasureCommand:
            {
                var payload = Read(cmd.Payload);
                var reason = DataSubject.CheckReason(payload.Reason, required: true);
                // The same hold restated (a redelivered event, a policy re-run): nothing new to record.
                if (state.Stage == ErasureStage.Held && state.HoldReason == reason && state.ReviewAt == payload.ReviewAt) return [];
                return [Event(ErasureHeldEvent, new { reason, reviewAt = payload.ReviewAt, heldBy = cmd.Actor, heldAt = at })];
            }
            case ApproveErasureCommand:
                return [Event(ErasureApprovedEvent, new { approvedBy = cmd.Actor, approvedAt = at })];
            case PlaceLegalHoldCommand:
            {
                var reason = DataSubject.CheckReason(Read(cmd.Payload).Reason, required: true);
                return [Event(LegalHoldPlacedEvent, new { reason, placedBy = cmd.Actor, placedAt = at })];
            }
            default: // ReleaseLegalHold; Judge has refused anything else
                return [Event(LegalHoldReleasedEvent, new { releasedBy = cmd.Actor, releasedAt = at })];
        }
    }

    private static GovernedSubjectState Evolve(GovernedSubjectState state, Event ev)
    {
        state = state with { Core = DataSubject.Evolve(state.Core, ev) };
        return ev.Type switch
        {
            DataSubject.ErasureRequestedEvent => state with { Stage = ErasureStage.Requested },
            ErasureHeldEvent => state with { Stage = ErasureStage.Held, HoldReason = Read(ev.Data).Reason, ReviewAt = Read(ev.Data).ReviewAt },
            ErasureApprovedEvent => state with { Stage = ErasureStage.Approved },
            LegalHoldPlacedEvent => state with { LegalHold = true },
            LegalHoldReleasedEvent => state with { LegalHold = false },
            DataSubject.SubjectErasedEvent => state with { Stage = ErasureStage.Erased },
            _ => state,
        };
    }
}
