using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Retention;

namespace DotnetCqrs.Tests.Retention;

/// <summary>
/// The governed lifecycle over the core dataSubject aggregate: a request is not an erasure. Which commands a subject
/// accepts in each stage is worked by hand (None, Requested, Held &lt;-&gt; asked again, Approved, Erased; a legal hold
/// blocks approval and erasure) and checked against what <c>Decide</c> does in every state, so a screen built on
/// <see cref="GovernedSubject.AcceptedCommands"/> never offers what the subject would refuse or ignore.
/// </summary>
public class GovernedSubjectTests
{
    private const string Request = DataSubject.RequestErasureCommand;
    private const string Hold = GovernedSubject.HoldErasureCommand;
    private const string Approve = GovernedSubject.ApproveErasureCommand;
    private const string Place = GovernedSubject.PlaceLegalHoldCommand;
    private const string Release = GovernedSubject.ReleaseLegalHoldCommand;
    private const string Erase = DataSubject.EraseSubjectCommand;

    private static async Task<(SqliteEventStore Store, DeciderRegistry Registry)> BareAsync()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterGovernedDataSubjects();
        return (store, registry);
    }

    private static Task<IReadOnlyList<Event>> Do(DeciderRegistry r, string id, string cmd, string payload = "{}", string actor = "u") =>
        r.HandleWithMetaAsync(DataSubject.Aggregate, id, new Command(cmd, payload),
            new Dictionary<string, object> { ["actor"] = actor, ["now"] = "2026-10-16T09:00:00.000Z" });

    /// <summary>The module adds to the core's rules, never relaxes them.</summary>
    [Fact]
    public async Task The_core_request_and_erasure_behave_as_the_core_does()
    {
        var (store, registry) = await BareAsync();

        var first = Assert.Single(await Do(registry, "s1", Request, """{"reason":"asked"}""", actor: "u:clerk-1"));
        Assert.Equal(DataSubject.ErasureRequestedEvent, first.Type);
        Assert.Contains("\"requestedBy\":\"u:clerk-1\"", first.Data);
        Assert.Empty(await Do(registry, "s1", Request));
        var tooLong = new string('x', DataSubject.MaxReasonLength + 1);
        Assert.Contains("not personal data", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Do(registry, "s2", Request, $$"""{"reason":"{{tooLong}}"}"""))).Message);

        Assert.Single(await Do(registry, "s3", Erase));
        Assert.Equal("{}", (await store.LoadStreamAsync(DataSubject.Aggregate, "s3")).Single().Data);
        Assert.Empty(await Do(registry, "s3", Erase));
        Assert.Empty(await Do(registry, "s3", Request));
    }

    [Theory]
    [InlineData(Hold, """{"reason":"x"}""")]
    [InlineData(Approve, "{}")]
    public async Task Hold_and_approve_need_an_open_request(string command, string payload)
    {
        var (_, registry) = await BareAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", command, payload));
    }

    [Fact]
    public async Task A_hold_needs_a_reason_and_can_be_restated_without_duplicating_the_event()
    {
        var (store, registry) = await BareAsync();
        await Do(registry, "s1", Request);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Hold, "{}"));
        var hold = """{"reason":"statutory-records","reviewAt":"2030-01-01T00:00:00Z"}""";
        Assert.Single(await Do(registry, "s1", Hold, hold));
        Assert.Empty(await Do(registry, "s1", Hold, hold)); // same hold, nothing new

        var moved = """{"reason":"statutory-records","reviewAt":"2031-01-01T00:00:00Z"}""";
        Assert.Single(await Do(registry, "s1", Hold, moved)); // a new review date is news
        Assert.Equal(3, (await store.LoadStreamAsync(DataSubject.Aggregate, "s1")).Count);
    }

    [Fact]
    public async Task A_legal_hold_blocks_approval_and_erasure_until_released()
    {
        var (_, registry) = await BareAsync();
        await Do(registry, "s1", Request);
        await Do(registry, "s1", Place, """{"reason":"litigation"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Approve));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Erase));
        Assert.Empty(await Do(registry, "s1", Place, """{"reason":"again"}""")); // idempotent

        await Do(registry, "s1", Release);
        Assert.Single(await Do(registry, "s1", Approve));
        Assert.Empty(await Do(registry, "s1", Release)); // nothing to release
    }

    [Fact]
    public async Task Nothing_but_a_no_op_is_possible_after_erasure()
    {
        var (_, registry) = await BareAsync();
        await Do(registry, "s1", Erase);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Place, """{"reason":"x"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Approve));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", Hold, """{"reason":"x"}"""));
    }

    [Theory]
    [InlineData(Request, "{}")]
    [InlineData(Place, """{"reason":"litigation"}""")]
    [InlineData(Hold, """{"reason":"statutory-records"}""")]
    public void A_command_with_no_time_is_refused_not_given_the_clocks(string command, string payload)
    {
        var state = State(command == Request ? ErasureStage.None : ErasureStage.Requested, legalHold: false);

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            GovernedSubject.Create().Decide(state, new Command(command, payload) { Actor = "admin-1" }));

        Assert.Contains("time", refusal.Message);
    }

    public static TheoryData<ErasureStage, bool, string[]> ByHand => new()
    {
        // Nothing asked yet: ask, place a legal hold, or erase directly.
        { ErasureStage.None, false, [Request, Place, Erase] },
        // Asked: hold, approve, place a legal hold, erase. Asking again records nothing.
        { ErasureStage.Requested, false, [Hold, Approve, Place, Erase] },
        // A legal hold: no approval, no erasure; a hold may still be recorded, and the legal hold released.
        { ErasureStage.Requested, true, [Hold, Release] },
        { ErasureStage.Held, false, [Hold, Approve, Place, Erase] },
        { ErasureStage.Held, true, [Hold, Release] },
        // Approved: only the erasure or a legal hold, which stops it.
        { ErasureStage.Approved, false, [Place, Erase] },
        { ErasureStage.Approved, true, [Release] },
        // Erased: nothing, ever.
        { ErasureStage.Erased, false, [] },
    };

    private static GovernedSubjectState State(ErasureStage stage, bool legalHold) =>
        new(new DataSubjectState(Erased: stage == ErasureStage.Erased, Requested: stage != ErasureStage.None),
            stage, legalHold, HoldReason: "earlier-reason");

    [Theory]
    [MemberData(nameof(ByHand))]
    public void A_subject_accepts_what_its_stage_and_legal_hold_allow(ErasureStage stage, bool legalHold, string[] expected)
    {
        Assert.Equal(expected.Order(), GovernedSubject.AcceptedCommands(State(stage, legalHold)).Order());
    }

    [Theory]
    [MemberData(nameof(ByHand))]
    public void Exactly_the_accepted_commands_record_an_event_when_decided(ErasureStage stage, bool legalHold, string[] _)
    {
        var state = State(stage, legalHold);
        var accepted = GovernedSubject.AcceptedCommands(state);

        foreach (var command in GovernedSubject.Commands)
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
        var events = GovernedSubject.Create().Decide(State(ErasureStage.Approved, legalHold: false),
            new Command(Approve, "{}") { Actor = "admin-1", Now = "2026-10-14T09:00:00.000Z" });

        Assert.Empty(events);
    }

    [Fact]
    public void The_fold_tracks_the_stage_the_hold_and_the_review()
    {
        var decider = GovernedSubject.Create();
        var state = new[]
        {
            Ev(DataSubject.ErasureRequestedEvent, """{"requestedBy":"u:clerk-1","reason":"asked"}"""),
            Ev(GovernedSubject.ErasureHeldEvent, """{"reason":"statutory-records","reviewAt":"2032-03-01T00:00:00Z"}"""),
            Ev(GovernedSubject.LegalHoldPlacedEvent, """{"reason":"audit"}"""),
        }.Aggregate(decider.InitialState(), decider.Evolve);

        Assert.Equal(ErasureStage.Held, state.Stage);
        Assert.True(state.LegalHold);
        Assert.Equal("statutory-records", state.HoldReason);
        Assert.Equal(new DateTimeOffset(2032, 3, 1, 0, 0, 0, TimeSpan.Zero), state.ReviewAt);
        Assert.Equal(("u:clerk-1", "asked", true), (state.Core.RequestedBy, state.Core.Reason, state.Core.Requested));

        state = new[]
        {
            Ev(GovernedSubject.LegalHoldReleasedEvent, "{}"),
            Ev(GovernedSubject.ErasureApprovedEvent, "{}"),
            Ev(DataSubject.SubjectErasedEvent, "{}"),
        }.Aggregate(state, decider.Evolve);

        Assert.Equal(ErasureStage.Erased, state.Stage);
        Assert.True(state.Erased);
        Assert.False(state.LegalHold);
    }

    private static Event Ev(string type, string data) =>
        new(1, Guid.NewGuid().ToString("N"), DataSubject.Aggregate, "s1", 1, type, data, "{}", "2026-10-16T09:00:00.000Z");

    private static bool Records(GovernedSubjectState state, string command)
    {
        try
        {
            return GovernedSubject.Create().Decide(state,
                new Command(command, """{"reason":"a-new-reason"}""") { Actor = "admin-1", Now = "2026-10-14T09:00:00.000Z" }).Count > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
