using System.Net;
using DotnetCqrs.EventStore;
using DotnetCqrs.ExtCalling;

namespace DotnetCqrs.Tests;

/// <summary>
/// The external caller's outbox (D40, timesheets' paradigm audit R4). The consumer only records the event; the worker
/// makes the call outside the engine, retries it after its back-off, and dispatches the response's follow-ups. A
/// follow-up refused for a concurrency conflict is dispatched again at once (its decider decides afresh), up to three
/// times, then is an attempt like a failed call, retried later without calling the outside service again. Before, the
/// call (and a sleep between its retries) ran inside the engine, and a conflict dead-lettered the event.
/// </summary>
public class ExtCallOutboxTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Dispatches by asking <paramref name="answer"/> what to do with each attempt (null: applied).</summary>
    private sealed class FakeDispatcher(Func<FollowUpDispatch, int, Exception?> answer) : IFollowUpDispatcher
    {
        public List<string> Applied { get; } = [];
        public Dictionary<string, int> Attempts { get; } = [];
        public TaskCompletionSource AllApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Expected { get; init; } = 1;

        public Task DispatchAsync(FollowUpDispatch followUp, CancellationToken ct)
        {
            var attempt = Attempts[followUp.Id] = Attempts.GetValueOrDefault(followUp.Id) + 1;
            if (answer(followUp, attempt) is { } refusal) throw refusal;
            Applied.Add(followUp.Id);
            if (Applied.Count == Expected) AllApplied.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static ConcurrencyException Conflict(string id) => new("task", id, 2, 1);

    private static Rule TwoTasksRule() => new()
    {
        EventType = "OrderPlaced",
        BuildRequest = _ => new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/verify"),
        HandleResponse = (ev, _) => Task.FromResult<IReadOnlyList<FollowUp>>(
            [new FollowUp("task", $"a-{ev.AggregateId}", "CreateTask", "{}"), new FollowUp("task", $"b-{ev.AggregateId}", "CreateTask", "{}")]),
    };

    private static (ExtCallerConsumer Consumer, FakeHandler Handler) Build(SqliteEventStore store, IFollowUpDispatcher dispatcher,
        TimeProvider? time = null, RetryPolicy? retry = null, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new FakeHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [TwoTasksRule()], Http = new HttpClient(handler), Dispatcher = dispatcher,
            DeadLetters = store, Outbox = store, Time = time ?? TimeProvider.System, Retry = retry ?? new RetryPolicy(),
        });
        return (consumer, handler);
    }

    private static async Task<Event> PlacedAsync(SqliteEventStore store, string orderId = "o1") =>
        (await store.AppendAsync("order", orderId, 0, [new NewEvent("OrderPlaced", "{}")]))[0];

    [Fact]
    public async Task The_engine_only_records_the_event_once_however_often_it_is_delivered()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var dispatcher = new FakeDispatcher((_, _) => null) { Expected = 2 };
        var (consumer, handler) = Build(store, dispatcher);
        var placed = await PlacedAsync(store);

        await consumer.ApplyAsync(placed, CancellationToken.None);
        await consumer.ApplyAsync(placed, CancellationToken.None);

        Assert.Equal(0, handler.CallCount);
        Assert.Single(await store.DueAsync("extcall:verify", DateTimeOffset.UtcNow, 10));
        Assert.Equal(1, await consumer.Worker.ProcessDueAsync());
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Empty(await store.DueAsync("extcall:verify", DateTimeOffset.UtcNow, 10));
    }

    [Fact]
    public async Task A_follow_up_meeting_a_conflict_is_dispatched_again_at_once()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        // a-o1 conflicts twice, then applies (its third dispatch, the last of the three at once).
        var dispatcher = new FakeDispatcher((f, attempt) => f.Id == "a-o1" && attempt <= 2 ? Conflict(f.Id) : null) { Expected = 2 };
        var (consumer, handler) = Build(store, dispatcher);

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Equal(3, dispatcher.Attempts["a-o1"]);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(await store.ListDeadLettersAsync());
        Assert.Null(await store.NextDueAsync("extcall:verify"));
    }

    [Fact]
    public async Task A_remote_gateways_409_is_a_conflict_too()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var dispatcher = new FakeDispatcher((f, attempt) => f.Id == "a-o1" && attempt == 1 ? new GatewayDispatchException(409, "conflict") : null) { Expected = 2 };
        var (consumer, _) = Build(store, dispatcher);

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [Fact]
    public async Task A_lasting_conflict_is_retried_after_the_back_off_without_calling_out_again_from_where_it_stopped()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var clock = new ExtCallerTests.ManualClock(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        // b-o1 conflicts on its first three dispatches (all at once), then applies.
        var dispatcher = new FakeDispatcher((f, attempt) => f.Id == "b-o1" && attempt <= 3 ? Conflict(f.Id) : null) { Expected = 2 };
        var (consumer, handler) = Build(store, dispatcher, clock, new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMinutes(5)));

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();
        Assert.Equal(["a-o1"], dispatcher.Applied);
        Assert.Equal(3, dispatcher.Attempts["b-o1"]);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), await store.NextDueAsync("extcall:verify"));

        await consumer.Worker.ProcessDueAsync(); // not due yet
        Assert.Equal(3, dispatcher.Attempts["b-o1"]);

        clock.Advance(TimeSpan.FromMinutes(5));
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied); // a-o1 not dispatched again
        Assert.Equal(1, dispatcher.Attempts["a-o1"]);
        Assert.Equal(1, handler.CallCount); // the response was kept: no second call
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [Theory]
    [InlineData("a-o1")]
    [InlineData("b-o1")]
    public async Task A_worker_stopped_mid_dispatch_resumes_without_calling_out_or_dispatching_again(string stoppedAt)
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        using var stop = new CancellationTokenSource();
        // The worker is stopped (the host shutting down) as it dispatches a follow-up the first time.
        var dispatcher = new FakeDispatcher((f, attempt) =>
        {
            if (f.Id != stoppedAt || attempt > 1) return null;
            stop.Cancel();
            return new OperationCanceledException(stop.Token);
        }) { Expected = 2 };
        var (consumer, handler) = Build(store, dispatcher);

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.Worker.ProcessDueAsync(stop.Token));
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
        Assert.Equal(stoppedAt == "a-o1" ? 2 : 1, dispatcher.Attempts["a-o1"]); // a-o1 applied once either way
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [Fact]
    public async Task A_conflict_lasting_past_the_attempts_is_dead_lettered()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var dispatcher = new FakeDispatcher((f, _) => f.Id == "a-o1" ? Conflict(f.Id) : null);
        var (consumer, _) = Build(store, dispatcher); // one attempt

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await consumer.Worker.ProcessDueAsync();

        Assert.Equal(3, dispatcher.Attempts["a-o1"]);
        var dead = Assert.Single(await store.ListDeadLettersAsync());
        Assert.Contains("dispatching follow-up 1/2", dead.Error);
        Assert.Null(await store.NextDueAsync("extcall:verify"));
    }

    [Fact]
    public async Task A_recorded_call_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"extcall-outbox-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = await SqliteEventStore.OpenAsync(path))
            {
                var (consumer, handler) = Build(store, new FakeDispatcher((_, _) => null));
                await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
                Assert.Equal(0, handler.CallCount);
            }
            await using (var reopened = await SqliteEventStore.OpenAsync(path))
            {
                var dispatcher = new FakeDispatcher((_, _) => null) { Expected = 2 };
                var (consumer, handler) = Build(reopened, dispatcher);
                await consumer.Worker.ProcessDueAsync();
                Assert.Equal(1, handler.CallCount);
                Assert.Equal(["a-o1", "b-o1"], dispatcher.Applied);
            }
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task The_running_worker_is_woken_by_a_recorded_event_and_again_when_a_retry_falls_due()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var dispatcher = new FakeDispatcher((_, _) => null) { Expected = 2 };
        var calls = 0;
        // The first call fails; the retry, 200 ms later, succeeds.
        var (consumer, _) = Build(store, dispatcher, retry: new RetryPolicy(MaxAttempts: 2, Backoff: TimeSpan.FromMilliseconds(200)),
            respond: _ => new HttpResponseMessage(++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var stop = new CancellationTokenSource();
        var running = consumer.Worker.RunAsync(stop.Token);

        await consumer.ApplyAsync(await PlacedAsync(store), CancellationToken.None);
        await dispatcher.AllApplied.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, calls);
        stop.Cancel();
        await running;
    }
}
