using DotnetCqrs.Consumers;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Outbox;
using DotnetCqrs.Retention;

namespace DotnetCqrs.Tests.Retention;

/// <summary>
/// The governor drives a request to a conclusion against a real consumer engine and the in-memory key client. A held
/// request with a review date is reviewed by an outbox entry due on that date (the rig's clock moves and the worker
/// takes what is due), never by a timer looking through a read model.
/// </summary>
public class ErasureGovernorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        public required DeciderRegistry Registry { get; init; }
        public required ConsumerEngine Engine { get; init; }
        public required InMemoryKmsClient Kms { get; init; }
        public required ErasureGovernor Governor { get; init; }
        public required ManualTime Time { get; init; }

        public Task<IReadOnlyList<Event>> Send(string subject, string command, string payload = "{}", string actor = "user:admin") =>
            Registry.HandleWithMetaAsync(DataSubject.Aggregate, subject, new Command(command, payload),
                new Dictionary<string, object> { ["actor"] = actor, ["now"] = Time.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") });

        /// <summary>Runs the engine and the reviews due until the governor's own follow-up commands are consumed.</summary>
        public async Task Settle()
        {
            for (var i = 0; i < 8; i++)
            {
                await Engine.RunOnceAsync();
                await Governor.Reviews!.ProcessDueAsync();
            }
        }

        public async Task<IReadOnlyList<string>> Types(string subject) =>
            (await Events.LoadStreamAsync(DataSubject.Aggregate, subject)).Select(e => e.Type).ToList();

        public async Task<GovernedSubjectState> State(string subject)
        {
            var decider = GovernedSubject.Create();
            var stream = await Events.LoadStreamAsync(DataSubject.Aggregate, subject);
            return stream.Aggregate(decider.InitialState(), decider.Evolve);
        }
    }

    private static async Task<Rig> NewRigAsync(IRetentionPolicy policy)
    {
        var time = new ManualTime(Epoch);
        var events = await SqliteEventStore.OpenAsync(":memory:");
        var kms = new InMemoryKmsClient();
        var registry = new DeciderRegistry(events);
        var engine = new ConsumerEngine(events, events, timeProvider: time);
        engine.Register(new SubjectKeyDestroyer(kms));
        var governor = engine.RegisterErasureRetention(registry, events, policy, reviews: events, time);
        registry.RegisterDataSubjects(); // after the module: must keep the governed decider
        return new Rig { Events = events, Registry = registry, Engine = engine, Kms = kms, Governor = governor, Time = time };
    }

    [Fact]
    public async Task With_no_retention_duty_a_request_is_approved_and_the_key_is_destroyed()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Kms.EnsureKeyAsync("s1");
        var ciphertext = await rig.Kms.EncryptAsync("s1", [1, 2, 3]);

        await rig.Send("s1", DataSubject.RequestErasureCommand, actor: "u:clerk-1");
        await rig.Settle();

        Assert.Equal(
            [DataSubject.ErasureRequestedEvent, GovernedSubject.ErasureApprovedEvent, DataSubject.SubjectErasedEvent],
            await rig.Types("s1"));
        Assert.Equal(KmsKeyState.Destroyed, (await rig.Kms.DecryptAsync("s1", ciphertext)).State);
    }

    [Fact]
    public async Task A_period_policy_holds_the_request_then_the_review_closes_it_when_the_period_ends()
    {
        var lastActivity = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var rig = await NewRigAsync(new PeriodRetentionPolicy(6, new FixedActivity(lastActivity), reason: "statutory-records"));
        await rig.Kms.EnsureKeyAsync("s1");

        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal(ErasureStage.Held, held.Stage);
        Assert.Equal("statutory-records", held.HoldReason);
        Assert.Equal(lastActivity.AddYears(6), held.ReviewAt);
        var review = Assert.Single(await ((IOutbox)rig.Events).DueAsync(ErasureGovernor.ReviewConsumer, DateTimeOffset.MaxValue, 10));
        Assert.Equal(lastActivity.AddYears(6), review.Due);

        // Not due yet: nothing moves, and the key is intact.
        await rig.Settle();
        Assert.Equal(ErasureStage.Held, (await rig.State("s1")).Stage);
        Assert.NotEqual(KmsKeyState.Destroyed, (await rig.Kms.DecryptAsync("s1", await rig.Kms.EncryptAsync("s1", [9]))).State);

        // The period ends: the review approves it, and the lifecycle closes itself.
        rig.Time.Now = lastActivity.AddYears(6).AddDays(1);
        await rig.Settle();

        Assert.Equal(
            [DataSubject.ErasureRequestedEvent, GovernedSubject.ErasureHeldEvent, GovernedSubject.ErasureApprovedEvent, DataSubject.SubjectErasedEvent],
            await rig.Types("s1"));
        Assert.Null(await ((IOutbox)rig.Events).NextDueAsync(ErasureGovernor.ReviewConsumer));
    }

    [Fact]
    public async Task A_legal_hold_overrides_a_policy_that_would_allow_and_releasing_it_resumes_the_request()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", GovernedSubject.PlaceLegalHoldCommand, """{"reason":"litigation"}""");
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal(ErasureStage.Held, held.Stage);
        Assert.Equal("legal-hold", held.HoldReason);
        Assert.Null(held.ReviewAt);
        // No schedule can release it: only lifting the hold does.
        Assert.Null(await ((IOutbox)rig.Events).NextDueAsync(ErasureGovernor.ReviewConsumer));
        rig.Time.Now = rig.Time.Now.AddYears(50);
        await rig.Settle();
        Assert.Equal(ErasureStage.Held, (await rig.State("s1")).Stage);

        await rig.Send("s1", GovernedSubject.ReleaseLegalHoldCommand);
        await rig.Settle();

        Assert.Equal(ErasureStage.Erased, (await rig.State("s1")).Stage);
    }

    [Fact]
    public async Task A_hold_placed_after_a_policy_hold_keeps_the_request_from_being_reviewed_away()
    {
        var rig = await NewRigAsync(new PeriodRetentionPolicy(1, new FixedActivity(Epoch), "tax"));
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();
        await rig.Send("s1", GovernedSubject.PlaceLegalHoldCommand, """{"reason":"audit"}""");

        // The review comes due; the governor reads the subject's own stream and must not approve.
        rig.Time.Now = Epoch.AddYears(5);
        await rig.Settle();

        Assert.Equal(ErasureStage.Held, (await rig.State("s1")).Stage);
        Assert.Null(await ((IOutbox)rig.Events).NextDueAsync(ErasureGovernor.ReviewConsumer)); // the review is done
    }

    [Fact]
    public async Task A_redelivered_hold_records_its_review_once()
    {
        var rig = await NewRigAsync(new PeriodRetentionPolicy(1, new FixedActivity(Epoch), "tax"));
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();
        var held = (await rig.Events.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single(e => e.Type == GovernedSubject.ErasureHeldEvent);

        await rig.Governor.ApplyAsync(held, CancellationToken.None); // at-least-once delivery

        Assert.Single(await ((IOutbox)rig.Events).DueAsync(ErasureGovernor.ReviewConsumer, DateTimeOffset.MaxValue, 10));
    }

    [Fact]
    public async Task A_redelivered_request_event_does_not_approve_twice()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        var requested = (await rig.Events.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single();

        await rig.Governor.ApplyAsync(requested, CancellationToken.None);
        await rig.Governor.ApplyAsync(requested, CancellationToken.None);

        Assert.Single((await rig.Types("s1")).Where(t => t == GovernedSubject.ErasureApprovedEvent));
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

    /// <summary>A refusal of what the governor sends (a legal hold placed after the approval) is the current state
    /// winning, not a delivery failure; the key stays.</summary>
    [Fact]
    public async Task A_refused_dispatch_is_not_a_delivery_failure()
    {
        var rig = await NewRigAsync(new AllowAllRetentionPolicy());
        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Send("s1", GovernedSubject.ApproveErasureCommand);
        await rig.Send("s1", GovernedSubject.PlaceLegalHoldCommand, """{"reason":"audit"}""");
        var approved = (await rig.Events.LoadStreamAsync(DataSubject.Aggregate, "s1")).Single(e => e.Type == GovernedSubject.ErasureApprovedEvent);

        await rig.Governor.ApplyAsync(approved, CancellationToken.None);

        Assert.False((await rig.State("s1")).Erased);
    }

    [Fact]
    public async Task A_composite_policy_holds_until_the_latest_duty_ends()
    {
        var rig = await NewRigAsync(new CompositeRetentionPolicy(
            new PeriodRetentionPolicy(6, new FixedActivity(Epoch), "tax"),
            new PeriodRetentionPolicy(7, new FixedActivity(Epoch), "employment")));

        await rig.Send("s1", DataSubject.RequestErasureCommand);
        await rig.Settle();

        var held = await rig.State("s1");
        Assert.Equal("tax, employment", held.HoldReason);
        Assert.Equal(Epoch.AddYears(7), held.ReviewAt);
    }

    [Fact]
    public async Task A_period_policy_allows_a_subject_with_no_activity_and_the_periods_last_instant()
    {
        Assert.True((await new PeriodRetentionPolicy(5000, new FixedActivity(null)).EvaluateAsync("s1", Epoch)).Allowed);
        var held = await new PeriodRetentionPolicy(1, new FixedActivity(Epoch)).EvaluateAsync("s1", Epoch);
        Assert.Equal((false, "retention-period"), (held.Allowed, held.Reason));
        Assert.True((await new PeriodRetentionPolicy(1, new FixedActivity(Epoch)).EvaluateAsync("s1", Epoch.AddYears(1))).Allowed);
    }

    [Fact]
    public async Task Wiring_governs_erasure_whichever_order_the_host_registers_in()
    {
        foreach (var moduleFirst in new[] { true, false })
        {
            var events = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(events);
            var engine = new ConsumerEngine(events, events);
            if (!moduleFirst) registry.RegisterDataSubjects();
            engine.RegisterErasureRetention(registry, events, new AllowAllRetentionPolicy(), reviews: events);
            if (moduleFirst) registry.RegisterDataSubjects();

            await registry.HandleWithMetaAsync(DataSubject.Aggregate, "s1", new Command(DataSubject.RequestErasureCommand, "{}"),
                new Dictionary<string, object> { ["actor"] = "a", ["now"] = "2026-10-16T09:00:00.000Z" });
            await registry.HandleWithMetaAsync(DataSubject.Aggregate, "s1", new Command(GovernedSubject.PlaceLegalHoldCommand, """{"reason":"audit"}"""),
                new Dictionary<string, object> { ["actor"] = "a", ["now"] = "2026-10-16T09:00:00.000Z" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.HandleAsync(DataSubject.Aggregate, "s1", new Command(DataSubject.EraseSubjectCommand, "{}")));
        }
    }

    [Fact]
    public async Task A_governor_needs_a_policy()
    {
        var events = await SqliteEventStore.OpenAsync(":memory:");
        var ex = Assert.Throws<ArgumentNullException>(() => new ErasureGovernor(new DeciderRegistry(events), events, null!));
        Assert.Contains("AllowAllRetentionPolicy", ex.Message);
    }

    [Fact]
    public void The_governor_and_its_reviews_have_stable_names()
    {
        Assert.Equal(("retention:governor", "retention:review"), (ErasureGovernor.ConsumerName, ErasureGovernor.ReviewConsumer));
    }
}
