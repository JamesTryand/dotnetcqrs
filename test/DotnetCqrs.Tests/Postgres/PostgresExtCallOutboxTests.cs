using System.Net;
using DotnetCqrs.EventStore;
using DotnetCqrs.ExtCalling;
using DotnetCqrs.Postgres;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="ExtCallOutboxTests"/>: the outbox
/// <see cref="PostgresEventStore"/> keeps, driven by the real consumer and worker. Recording is idempotent; a lasting
/// conflict is retried after the back-off from where it stopped, without calling out again; past the attempts it is
/// dead-lettered; and a recorded call survives reopening the store.</summary>
[Collection("postgres")]
public class PostgresExtCallOutboxTests(PostgresFixture fx)
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeDispatcher(Func<FollowUpDispatch, int, Exception?> answer) : IFollowUpDispatcher
    {
        public List<string> Applied { get; } = [];
        public Dictionary<string, int> Attempts { get; } = [];

        public Task DispatchAsync(FollowUpDispatch followUp, CancellationToken ct)
        {
            var attempt = Attempts[followUp.Id] = Attempts.GetValueOrDefault(followUp.Id) + 1;
            if (answer(followUp, attempt) is { } refusal) throw refusal;
            Applied.Add(followUp.Id);
            return Task.CompletedTask;
        }
    }

    private static (ExtCallerConsumer Consumer, FakeHandler Handler) Build(
        PostgresEventStore store, IFollowUpDispatcher dispatcher, TimeProvider? time = null, RetryPolicy? retry = null)
    {
        var handler = new FakeHandler();
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify",
            Rules =
            [
                new Rule
                {
                    EventType = "OrderPlaced",
                    BuildRequest = _ => new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/verify"),
                    HandleResponse = (ev, _) => Task.FromResult<IReadOnlyList<FollowUp>>(
                        [new FollowUp("task", $"a-{ev.AggregateId}", "CreateTask", "{}"), new FollowUp("task", $"b-{ev.AggregateId}", "CreateTask", "{}")]),
                },
            ],
            Http = new HttpClient(handler), Dispatcher = dispatcher, DeadLetters = store, Outbox = store,
            Time = time ?? TimeProvider.System, Retry = retry ?? new RetryPolicy(),
        });
        return (consumer, handler);
    }

    private static async Task<Event> PlacedAsync(PostgresEventStore store) =>
        (await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]))[0];

    [SkippableFact]
    public async Task The_engine_only_records_the_event_once_and_the_worker_calls_and_dispatches()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());
        var dispatcher = new FakeDispatcher((_, _) => null);
        var (consumer, handler) = Build(store, dispatcher);
        var placed = await PlacedAsync(store);

        await consumer.ApplyAsync(placed, CancellationToken.None);
        await consumer.ApplyAsync(placed, CancellationToken.None);

        Assert.Equal(0, handler.CallCount);
        Assert.Single(await store.DueAsync("extcall:verify", DateTimeOffset.UtcNow, 10));
        Assert.Equal(1, await consumer.Worker.ProcessDueAsync());
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Null(await store.NextDueAsync("extcall:verify"));
    }

    [SkippableFact]
    public async Task A_lasting_conflict_is_retried_after_the_back_off_without_calling_out_again()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());
        var clock = new ExtCallerTests.ManualClock(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        // b-o1 conflicts on its first three dispatches (all at once), then applies.
        var dispatcher = new FakeDispatcher((f, attempt) => f.Id == "b-o1" && attempt <= 3 ? new ConcurrencyException("task", f.Id, 2, 1) : null);
        var (consumer, handler) = Build(store, dispatcher, clock, new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMinutes(5)));

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();
        Assert.Equal(["a-o1"], dispatcher.Applied);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), await store.NextDueAsync("extcall:verify"));
        await consumer.Worker.ProcessDueAsync(); // not due yet
        Assert.Equal(3, dispatcher.Attempts["b-o1"]);

        clock.Advance(TimeSpan.FromMinutes(5));
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Equal(1, dispatcher.Attempts["a-o1"]);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [SkippableFact]
    public async Task A_conflict_lasting_past_the_attempts_is_dead_lettered()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());
        var dispatcher = new FakeDispatcher((f, _) => f.Id == "a-o1" ? new ConcurrencyException("task", f.Id, 2, 1) : null);
        var (consumer, _) = Build(store, dispatcher);

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();

        var dead = Assert.Single(await store.ListDeadLettersAsync());
        Assert.Contains("dispatching follow-up 1/2", dead.Error);
        Assert.Null(await store.NextDueAsync("extcall:verify"));
    }

    [SkippableFact]
    public async Task A_recorded_call_survives_reopening_the_store()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var log = await fx.NewSchemaAsync();
        await using (var store = await PostgresEventStore.OpenAsync(log))
        {
            var (consumer, handler) = Build(store, new FakeDispatcher((_, _) => null));
            await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
            Assert.Equal(0, handler.CallCount);
        }
        await using var reopened = await PostgresEventStore.OpenAsync(log);
        var dispatcher = new FakeDispatcher((_, _) => null);
        var (again, againHandler) = Build(reopened, dispatcher);

        await again.Worker.ProcessDueAsync();

        Assert.Equal(1, againHandler.CallCount);
        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
    }
}
