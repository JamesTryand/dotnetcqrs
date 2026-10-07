using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// Governed erasure (venture-overview decision 0013): a request is not an erasure. These cover the
/// subject's state machine, the retention policies, and the governor that drives a request to a
/// conclusion against a real consumer engine and the in-memory key client.
/// </summary>
public class ErasureWorkflowTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedActivity(DateTimeOffset? last) : ISubjectActivity
    {
        public Task<DateTimeOffset?> LastActivityAsync(string subjectId, CancellationToken ct = default) => Task.FromResult(last);
    }

    private sealed class Rig
    {
        public required SqliteEventStore Events { get; init; }
        public required SqliteReadModelStore ReadModels { get; init; }
        public required DeciderRegistry Registry { get; init; }
        public required ConsumerEngine Engine { get; init; }
        public required InMemoryKmsClient Kms { get; init; }
        public required ErasureGovernor Governor { get; init; }
        public required ManualTime Time { get; init; }
        public List<string> Log { get; } = [];

        public Task<IReadOnlyList<Event>> Send(string subject, string command, string payload = "{}", string actor = "user:admin") =>
            Registry.HandleWithMetaAsync(DataSubject.Aggregate, subject, new Command(command, payload),
                new Dictionary<string, object> { ["actor"] = actor, ["now"] = Time.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") });

        /// <summary>Runs the engine until the governor's own follow-up commands have all been consumed.</summary>
        public async Task Settle()
        {
            for (var i = 0; i < 8; i++) await Engine.RunOnceAsync();
        }

        public async Task<IReadOnlyList<string>> Types(string subject) =>
            (await Events.LoadStreamAsync(DataSubject.Aggregate, subject)).Select(e => e.Type).ToList();

        public async Task<DataSubjectState> State(string subject)
        {
            var decider = DataSubject.Create();
            var stream = await Events.LoadStreamAsync(DataSubject.Aggregate, subject);
            return stream.Aggregate(decider.InitialState(), (s, e) => decider.Evolve(s, e));
        }
    }

    private static async Task<Rig> NewRigAsync(IRetentionPolicy policy)
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var events = await SqliteEventStore.OpenAsync(":memory:");
        var readModels = await SqliteReadModelStore.OpenAsync(":memory:");
        var kms = new InMemoryKmsClient();
        var registry = new DeciderRegistry(events);
        registry.RegisterDataSubjects();
        var engine = new ConsumerEngine(events, events, timeProvider: time);
        engine.Register(new SubjectKeyDestroyer(kms));
        var log = new List<string>();
        var governor = await engine.RegisterErasureGovernanceAsync(registry, events, readModels, policy, time, log.Add);
        var rig = new Rig { Events = events, ReadModels = readModels, Registry = registry, Engine = engine, Kms = kms, Governor = governor, Time = time };
        rig.Log.AddRange(log);
        return rig;
    }

    // --- the state machine, without any policy involved ---

    private static async Task<(SqliteEventStore Store, DeciderRegistry Registry)> BareAsync()
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterDataSubjects();
        return (store, registry);
    }

    private static Task<IReadOnlyList<Event>> Do(DeciderRegistry r, string id, string cmd, string payload = "{}", string actor = "u") =>
        r.HandleWithMetaAsync(DataSubject.Aggregate, id, new Command(cmd, payload), new Dictionary<string, object> { ["actor"] = actor });

    [Fact]
    public async Task A_request_records_who_asked_and_why_and_is_idempotent_while_open()
    {
        var (store, registry) = await BareAsync();

        var first = await Do(registry, "s1", DataSubject.RequestErasureCommand, """{"reason":"data-subject-request"}""", actor: "u:manager-1");
        var again = await Do(registry, "s1", DataSubject.RequestErasureCommand);

        var ev = Assert.Single(first);
        Assert.Equal(DataSubject.ErasureRequestedEvent, ev.Type);
        Assert.Contains("\"requestedBy\":\"u:manager-1\"", ev.Data);
        Assert.Contains("\"reason\":\"data-subject-request\"", ev.Data);
        Assert.Empty(again);
        Assert.Single(await store.LoadStreamAsync(DataSubject.Aggregate, "s1"));
    }

    [Fact]
    public async Task A_reason_is_refused_when_too_long_to_be_a_code()
    {
        var (_, registry) = await BareAsync();
        var tooLong = new string('x', DataSubject.MaxReasonLength + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Do(registry, "s1", DataSubject.RequestErasureCommand, $$"""{"reason":"{{tooLong}}"}"""));
        Assert.Contains("not personal data", ex.Message);
    }

    [Theory]
    [InlineData(DataSubject.HoldErasureCommand, """{"reason":"x"}""")]
    [InlineData(DataSubject.ApproveErasureCommand, "{}")]
    public async Task Hold_and_approve_need_an_open_request(string command, string payload)
    {
        var (_, registry) = await BareAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", command, payload));
    }

    [Fact]
    public async Task A_hold_needs_a_reason_and_can_be_restated_without_duplicating_the_event()
    {
        var (store, registry) = await BareAsync();
        await Do(registry, "s1", DataSubject.RequestErasureCommand);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", DataSubject.HoldErasureCommand, "{}"));
        var hold = """{"reason":"payroll","reviewAt":"2030-01-01T00:00:00Z"}""";
        Assert.Single(await Do(registry, "s1", DataSubject.HoldErasureCommand, hold));
        Assert.Empty(await Do(registry, "s1", DataSubject.HoldErasureCommand, hold)); // same hold, nothing new

        var moved = """{"reason":"payroll","reviewAt":"2031-01-01T00:00:00Z"}""";
        Assert.Single(await Do(registry, "s1", DataSubject.HoldErasureCommand, moved)); // a new review date is news
        Assert.Equal(3, (await store.LoadStreamAsync(DataSubject.Aggregate, "s1")).Count);
    }

    [Fact]
    public async Task A_legal_hold_blocks_approval_and_erasure_until_released()
    {
        var (_, registry) = await BareAsync();
        await Do(registry, "s1", DataSubject.RequestErasureCommand);
        await Do(registry, "s1", DataSubject.PlaceLegalHoldCommand, """{"reason":"litigation"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", DataSubject.ApproveErasureCommand));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", DataSubject.EraseSubjectCommand));
        Assert.Empty(await Do(registry, "s1", DataSubject.PlaceLegalHoldCommand, """{"reason":"again"}""")); // idempotent

        await Do(registry, "s1", DataSubject.ReleaseLegalHoldCommand);
        Assert.Single(await Do(registry, "s1", DataSubject.ApproveErasureCommand));
        Assert.Empty(await Do(registry, "s1", DataSubject.ReleaseLegalHoldCommand)); // nothing to release
    }

    [Fact]
    public async Task Nothing_but_a_no_op_is_possible_after_erasure()
    {
        var (_, registry) = await BareAsync();
        await Do(registry, "s1", DataSubject.EraseSubjectCommand);

        Assert.Empty(await Do(registry, "s1", DataSubject.EraseSubjectCommand));
        Assert.Empty(await Do(registry, "s1", DataSubject.RequestErasureCommand));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", DataSubject.PlaceLegalHoldCommand, """{"reason":"x"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(registry, "s1", DataSubject.ApproveErasureCommand));
    }

    [Fact]
    public async Task The_direct_erase_override_still_works_for_a_subject_with_no_request()
    {
        var (store, registry) = await BareAsync();

        Assert.Single(await Do(registry, "s1", DataSubject.EraseSubjectCommand));
        Assert.Equal("{}", (await store.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single().Data);
    }

    [Fact]
    public void Every_command_has_a_default_role_policy_and_only_the_request_is_open_to_a_manager()
    {
        foreach (var command in DataSubject.Commands)
            Assert.NotNull(DataSubject.DefaultRequiredRoles(command));

        Assert.Equal(["manager", "administrator"], DataSubject.DefaultRequiredRoles(DataSubject.RequestErasureCommand));
        foreach (var command in DataSubject.Commands.Where(c => c != DataSubject.RequestErasureCommand))
            Assert.Equal(["administrator"], DataSubject.DefaultRequiredRoles(command));
        Assert.Null(DataSubject.DefaultRequiredRoles("NotACommand"));
    }

    // --- the governor ---

    [Fact]
    public async Task With_no_retention_duty_a_request_is_approved_and_the_key_is_destroyed()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Kms.EnsureKeyAsync("s1");
        var ciphertext = await rig.Kms.EncryptAsync("s1", [1, 2, 3]);

        await rig.Send("s1", DataSubject.RequestErasureCommand, actor: "u:manager-1");
        await rig.Settle();

        Assert.Equal(
            [DataSubject.ErasureRequestedEvent, DataSubject.ErasureApprovedEvent, DataSubject.SubjectErasedEvent],
            await rig.Types("s1"));
        Assert.Equal(KmsKeyState.Destroyed, (await rig.Kms.DecryptAsync("s1", ciphertext)).State);
    }

    [Fact]
    public async Task A_period_policy_holds_the_request_then_the_review_closes_it_when_the_period_ends()
    {
        var lastActivity = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var rig = await NewRigAsync(new PeriodRetentionPolicy(6, new FixedActivity(lastActivity), reason: "payroll-tax"));
        await rig.Kms.EnsureKeyAsync("s1");

        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal(ErasureStage.Held, held.Stage);
        Assert.Equal("payroll-tax", held.Reason);
        Assert.Equal(lastActivity.AddYears(6), held.ReviewAt);

        // Not due yet: a review changes nothing, and the key is intact.
        Assert.Equal(0, await rig.Governor.ReviewDueAsync(CancellationToken.None));
        await rig.Settle();
        Assert.Equal(ErasureStage.Held, (await rig.State("s1")).Stage);
        Assert.True((await rig.Kms.DecryptAsync("s1", await rig.Kms.EncryptAsync("s1", [9]))).State != KmsKeyState.Destroyed);

        // The period ends: the next review approves it, and the lifecycle closes itself.
        rig.Time.Now = lastActivity.AddYears(6).AddDays(1);
        Assert.Equal(1, await rig.Governor.ReviewDueAsync(CancellationToken.None));
        await rig.Settle();

        Assert.Equal(ErasureStage.Erased, (await rig.State("s1")).Stage);
        Assert.Equal(
            [DataSubject.ErasureRequestedEvent, DataSubject.ErasureHeldEvent, DataSubject.ErasureApprovedEvent, DataSubject.SubjectErasedEvent],
            await rig.Types("s1"));
    }

    [Fact]
    public async Task A_legal_hold_overrides_a_policy_that_would_allow_and_releasing_it_resumes_the_request()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", DataSubject.PlaceLegalHoldCommand, """{"reason":"litigation"}""");
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal(ErasureStage.Held, held.Stage);
        Assert.Equal("legal-hold", held.Reason);
        Assert.Null(held.ReviewAt);
        // No schedule can release it: only lifting the hold does.
        rig.Time.Now = rig.Time.Now.AddYears(50);
        Assert.Equal(0, await rig.Governor.ReviewDueAsync(CancellationToken.None));

        await rig.Send("s1", DataSubject.ReleaseLegalHoldCommand);
        await rig.Settle();

        Assert.Equal(ErasureStage.Erased, (await rig.State("s1")).Stage);
    }

    [Fact]
    public async Task A_hold_placed_after_a_policy_hold_keeps_the_request_from_being_reviewed_away()
    {
        var rig = await NewRigAsync(new PeriodRetentionPolicy(1, new FixedActivity(rig_epoch), "tax"));
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();
        await rig.Send("s1", DataSubject.PlaceLegalHoldCommand, """{"reason":"audit"}""");

        rig.Time.Now = rig_epoch.AddYears(5);
        // The queue has not caught up with the hold yet, so the request is still "due". The governor
        // reads the subject's own stream, so it must still refuse to approve.
        await rig.Governor.ReviewDueAsync(CancellationToken.None);
        await rig.Settle();
        Assert.Equal(ErasureStage.Held, (await rig.State("s1")).Stage);
        // Once the queue knows about the hold, the request is no longer offered for review at all.
        Assert.Equal(0, await rig.Governor.ReviewDueAsync(CancellationToken.None));
    }

    private static readonly DateTimeOffset rig_epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_redelivered_request_event_does_not_approve_twice()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        var requested = (await rig.Events.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single();

        await rig.Governor.ApplyAsync(requested, CancellationToken.None);
        await rig.Governor.ApplyAsync(requested, CancellationToken.None); // at-least-once delivery

        Assert.Single((await rig.Types("s1")).Where(t => t == DataSubject.ErasureApprovedEvent));
    }

    [Fact]
    public async Task A_subject_that_moved_on_is_not_a_delivery_failure()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        var requested = (await rig.Events.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single();
        await rig.Send("s1", DataSubject.EraseSubjectCommand); // erased directly, before the governor ruled

        await rig.Governor.ApplyAsync(requested, CancellationToken.None); // must not throw

        Assert.Equal(ErasureStage.Erased, (await rig.State("s1")).Stage);
    }

    [Fact]
    public async Task A_composite_policy_holds_until_the_latest_duty_ends()
    {
        var rig = await NewRigAsync(new CompositeRetentionPolicy(
            new PeriodRetentionPolicy(6, new FixedActivity(rig_epoch), "tax"),
            new PeriodRetentionPolicy(7, new FixedActivity(rig_epoch), "employment")));

        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal("tax, employment", held.Reason);
        Assert.Equal(rig_epoch.AddYears(7), held.ReviewAt);
    }

    [Fact]
    public async Task The_queue_shows_each_request_where_it_stands()
    {
        var rig = await NewRigAsync(new PeriodRetentionPolicy(6, new FixedActivity(rig_epoch), "tax"));
        await rig.Send("held-one", DataSubject.RequestErasureCommand, """{"reason":"data-subject-request"}""", actor: "u:manager-1");
        await rig.Settle();

        var rows = await ErasureRequestsProjection.ListAsync(rig.ReadModels);
        var row = Assert.Single(rows);
        Assert.Equal("held-one", row.SubjectId);
        Assert.Equal(ErasureStage.Held, row.Stage);
        Assert.Equal("tax", row.Reason);
        Assert.Equal("u:manager-1", row.RequestedBy);
        Assert.Equal(rig_epoch.AddYears(6), row.ReviewAt);
        Assert.False(row.LegalHold);
        Assert.Single(await ErasureRequestsProjection.ListAsync(rig.ReadModels, ErasureStage.Held));
        Assert.Empty(await ErasureRequestsProjection.ListAsync(rig.ReadModels, ErasureStage.Erased));

        await rig.Send("held-one", DataSubject.PlaceLegalHoldCommand, """{"reason":"audit"}""");
        await rig.Settle();
        Assert.True(Assert.Single(await ErasureRequestsProjection.ListAsync(rig.ReadModels)).LegalHold);
    }

    [Fact]
    public async Task The_review_loop_closes_a_held_request_without_anyone_calling_review()
    {
        var rig = await NewRigAsync(new PeriodRetentionPolicy(1, new FixedActivity(rig_epoch), "tax"));
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();
        rig.Time.Now = rig_epoch.AddYears(2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var loop = rig.Governor.RunReviewLoopAsync(TimeSpan.FromMilliseconds(20), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (await rig.State("s1")).Stage != ErasureStage.Approved && (await rig.State("s1")).Stage != ErasureStage.Erased)
            await Task.Delay(25);
        cts.Cancel();
        await loop;
        await rig.Settle();

        Assert.Equal(ErasureStage.Erased, (await rig.State("s1")).Stage);
    }
}
