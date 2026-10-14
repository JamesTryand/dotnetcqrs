using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// Which governing commands a subject would accept, from the decider's own rules (timesheets' paradigm audit U5), so
/// a screen offers what the subject would act on instead of repeating the stage rules in the browser. Worked by hand
/// from the lifecycle (None, Requested, Held &lt;-&gt; Requested, Approved, Erased; a legal hold blocks approval and
/// erasure), and checked against what <c>Decide</c> does in every state.
/// </summary>
public class DataSubjectAcceptedCommandsTests
{
    private const string Request = DataSubject.RequestErasureCommand;
    private const string Hold = DataSubject.HoldErasureCommand;
    private const string Approve = DataSubject.ApproveErasureCommand;
    private const string Place = DataSubject.PlaceLegalHoldCommand;
    private const string Release = DataSubject.ReleaseLegalHoldCommand;
    private const string Erase = DataSubject.EraseSubjectCommand;

    public static TheoryData<ErasureStage, bool, string[]> ByHand => new()
    {
        // Nothing asked yet: ask, place a legal hold, or (the administrator's override) erase.
        { ErasureStage.None, false, [Request, Place, Erase] },
        // Asked: hold, approve, place a legal hold, erase. Asking again records nothing.
        { ErasureStage.Requested, false, [Hold, Approve, Place, Erase] },
        // A legal hold: no approval, no erasure; a hold may still be recorded, and the legal hold released.
        { ErasureStage.Requested, true, [Hold, Release] },
        { ErasureStage.Held, false, [Hold, Approve, Place, Erase] },
        { ErasureStage.Held, true, [Hold, Release] },
        // Approved: only the erasure (normally the governor's) or a legal hold, which stops it.
        { ErasureStage.Approved, false, [Place, Erase] },
        { ErasureStage.Approved, true, [Release] },
        // Erased: nothing, ever.
        { ErasureStage.Erased, false, [] },
    };

    private static DataSubjectState State(ErasureStage stage, bool legalHold) =>
        new(Erased: stage == ErasureStage.Erased, stage, legalHold, Reason: "earlier-reason");

    [Theory]
    [MemberData(nameof(ByHand))]
    public void A_subject_accepts_what_its_stage_and_legal_hold_allow(ErasureStage stage, bool legalHold, string[] expected)
    {
        Assert.Equal(expected.Order(), DataSubject.AcceptedCommands(State(stage, legalHold)).Order());
    }

    [Theory]
    [MemberData(nameof(ByHand))]
    public void Exactly_the_accepted_commands_record_an_event_when_decided(ErasureStage stage, bool legalHold, string[] _)
    {
        var state = State(stage, legalHold);
        var accepted = DataSubject.AcceptedCommands(state);

        foreach (var command in DataSubject.Commands)
        {
            var records = Records(state, command);
            Assert.True(records == accepted.Contains(command),
                $"{command} at {stage}{(legalHold ? " with a legal hold" : "")}: Decide {(records ? "records" : "records nothing")}, but AcceptedCommands {(accepted.Contains(command) ? "offers" : "doesn't offer")} it");
        }
    }

    /// <summary>Not offered, and not refused either: a second approval (the governor re-running, a redelivery) is
    /// harmless.</summary>
    [Fact]
    public void Approving_an_approved_request_again_records_nothing_and_is_not_refused()
    {
        var events = DataSubject.Create().Decide(State(ErasureStage.Approved, legalHold: false),
            new Command(Approve, "{}") { Actor = "admin-1", Now = "2026-10-14T09:00:00.000Z" });

        Assert.Empty(events);
    }

    private static bool Records(DataSubjectState state, string command)
    {
        try
        {
            return DataSubject.Create().Decide(state,
                new Command(command, """{"reason":"a-new-reason"}""") { Actor = "admin-1", Now = "2026-10-14T09:00:00.000Z" }).Count > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
