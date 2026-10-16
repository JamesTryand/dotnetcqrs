using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// The core data-subject aggregate (cqrs-system-contracts subject-erasure, sections 3 and 4): a request, and the
/// erasure. A request records that someone asked; it erases nothing. What decides a request (retention duties, legal
/// holds) is a module's, such as DotnetCqrs.Retention, which registers its own decider over this one and only adds
/// refusals to it.
/// </summary>
public class DataSubjectCoreTests
{
    private const string Now = "2026-10-16T09:00:00.000Z";

    private static async Task<(SqliteEventStore Store, DeciderRegistry Registry)> NewAsync()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterDataSubjects();
        return (store, registry);
    }

    private static Task<IReadOnlyList<Event>> Do(DeciderRegistry r, string id, string cmd, string payload = "{}", string actor = "u") =>
        r.HandleWithMetaAsync(DataSubject.Aggregate, id, new Command(cmd, payload),
            new Dictionary<string, object> { ["actor"] = actor, ["now"] = Now });

    [Fact]
    public async Task A_request_records_who_asked_why_and_when_and_is_idempotent_while_open()
    {
        var (store, registry) = await NewAsync();

        var first = Assert.Single(await Do(registry, "s1", DataSubject.RequestErasureCommand, """{"reason":"asked-by-subject"}""", actor: "u:clerk-1"));
        var again = await Do(registry, "s1", DataSubject.RequestErasureCommand);

        Assert.Equal(DataSubject.ErasureRequestedEvent, first.Type);
        Assert.Contains("\"requestedBy\":\"u:clerk-1\"", first.Data);
        Assert.Contains("\"reason\":\"asked-by-subject\"", first.Data);
        Assert.Contains($"\"requestedAt\":\"{Now}\"", first.Data);
        Assert.Empty(again);
        Assert.Single(await store.LoadStreamAsync(DataSubject.Aggregate, "s1"));
    }

    [Fact]
    public async Task A_reason_is_trimmed_and_refused_when_too_long_to_be_a_code()
    {
        var (_, registry) = await NewAsync();
        var tooLong = new string('x', DataSubject.MaxReasonLength + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Do(registry, "s1", DataSubject.RequestErasureCommand, $$"""{"reason":"{{tooLong}}"}"""));
        Assert.Contains("not personal data", ex.Message);
        Assert.Equal(new string('x', DataSubject.MaxReasonLength), DataSubject.CheckReason("  " + new string('x', DataSubject.MaxReasonLength) + " ", required: true));
        Assert.Throws<InvalidOperationException>(() => DataSubject.CheckReason(" ", required: true));
        Assert.Null(DataSubject.CheckReason(" ", required: false));
    }

    [Fact]
    public async Task Nothing_is_recorded_after_erasure()
    {
        var (_, registry) = await NewAsync();
        await Do(registry, "s1", DataSubject.EraseSubjectCommand);

        Assert.Empty(await Do(registry, "s1", DataSubject.EraseSubjectCommand));
        Assert.Empty(await Do(registry, "s1", DataSubject.RequestErasureCommand));
    }

    /// <summary>The core has the request and the erasure, nothing else: a workflow over them is a module's.</summary>
    [Fact]
    public async Task The_core_aggregate_knows_only_the_request_and_the_erasure()
    {
        Assert.Equal([DataSubject.RequestErasureCommand, DataSubject.EraseSubjectCommand], DataSubject.Commands);
        var (_, registry) = await NewAsync();
        await Do(registry, "s1", DataSubject.RequestErasureCommand);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", "HoldErasure", """{"reason":"x"}"""));
        Assert.Single(await Do(registry, "s1", DataSubject.EraseSubjectCommand)); // an open request does not stop it
    }

    public static TheoryData<DataSubjectState, string[]> ByHand => new()
    {
        { new DataSubjectState(Erased: false), [DataSubject.RequestErasureCommand, DataSubject.EraseSubjectCommand] },
        { new DataSubjectState(Erased: false, Requested: true), [DataSubject.EraseSubjectCommand] },
        { new DataSubjectState(Erased: true, Requested: true), [] },
        { new DataSubjectState(Erased: true), [] },
    };

    [Theory]
    [MemberData(nameof(ByHand))]
    public void A_subject_accepts_what_its_state_allows_and_exactly_that_records(DataSubjectState state, string[] expected)
    {
        Assert.Equal(expected, DataSubject.AcceptedCommands(state));
        foreach (var command in DataSubject.Commands)
        {
            var records = DataSubject.Decide(state, new Command(command, "{}") { Actor = "a", Now = Now }).Count > 0;
            Assert.True(records == expected.Contains(command), $"{command} at {state}: records={records}");
        }
    }

    [Fact]
    public void The_fold_tracks_the_request_and_the_erasure_and_ignores_a_modules_events()
    {
        var state = DataSubject.Evolve(new DataSubjectState(false), Ev(DataSubject.ErasureRequestedEvent, """{"requestedBy":"u:clerk-1","reason":"asked"}"""));
        Assert.Equal(new DataSubjectState(Erased: false, Requested: true, RequestedBy: "u:clerk-1", Reason: "asked"), state);

        Assert.Equal(state, DataSubject.Evolve(state, Ev("ErasureHeld", """{"reason":"other"}""")));
        Assert.True(DataSubject.Evolve(state, Ev(DataSubject.SubjectErasedEvent, "{}")).Erased);
    }

    /// <summary>The clock arrives on the command (the registry stamps it); a request with none is refused, never given
    /// the current time. The erasure records no time, so needs none.</summary>
    [Fact]
    public void A_request_with_no_time_is_refused_and_an_erasure_needs_none()
    {
        var decider = DataSubject.Create();

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            decider.Decide(decider.InitialState(), new Command(DataSubject.RequestErasureCommand, "{}") { Actor = "a" }));
        Assert.Contains("time", refusal.Message);
        Assert.Single(decider.Decide(decider.InitialState(), new Command(DataSubject.EraseSubjectCommand, "{}")));
    }

    /// <summary>A module that governs erasure registers its own dataSubject decider, a superset of the core one.
    /// Registering the core keeps it, so the order a host wires them in can't quietly drop the module's
    /// refusals.</summary>
    [Fact]
    public async Task Registering_the_core_keeps_a_dataSubject_decider_already_registered()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        Assert.False(registry.IsRegistered(DataSubject.Aggregate));
        registry.Register(DataSubject.Aggregate, new Decider<int>
        {
            InitialState = () => 0,
            Decide = (_, cmd) => cmd.Name == "ModuleCommand" ? [new NewEvent("ModuleEvent", "{}")] : throw new InvalidOperationException("no"),
            Evolve = (s, _) => s,
        });
        Assert.True(registry.IsRegistered(DataSubject.Aggregate));

        registry.RegisterDataSubjects();

        Assert.Single(await registry.HandleAsync(DataSubject.Aggregate, "s1", new Command("ModuleCommand", "{}")));
    }

    private static Event Ev(string type, string data) =>
        new(1, Guid.NewGuid().ToString("N"), DataSubject.Aggregate, "s1", 1, type, data, "{}", Now);
}
