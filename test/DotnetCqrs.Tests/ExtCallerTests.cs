using System.Net;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.ExtCalling;

namespace DotnetCqrs.Tests;

public class ExtCallerTests
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

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) => cmd.Name switch
        {
            "CreateTask" when exists => throw new InvalidOperationException("task already exists"),
            "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (_, ev) => ev.Type == "TaskCreated",
    };

    private static Rule EchoRule() => new()
    {
        EventType = "OrderPlaced",
        BuildRequest = ev => new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/verify")
        {
            Content = new StringContent(ev.AggregateId),
        },
        HandleResponse = async (ev, resp) =>
        {
            var body = await resp.Content.ReadAsStringAsync();
            return [new FollowUp("task", $"verify-{ev.AggregateId}", "CreateTask", $$"""{"title":"{{body}}"}""")];
        },
    };

    private static (SqliteEventStore Store, DeciderRegistry Registry) SetUpRegistry(SqliteEventStore store)
    {
        var registry = new DeciderRegistry(store);
        registry.Register("task", TaskDecider());
        return (store, registry);
    }

    [Fact]
    public async Task A_successful_call_dispatches_the_follow_up_through_the_registry()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (_, registry) = SetUpRegistry(store);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("approved") });
        var http = new HttpClient(handler);
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [EchoRule()], Http = http, Dispatcher = new InProcessFollowUpDispatcher(registry), DeadLetters = store,
        });

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None);

        var taskStream = await store.LoadStreamAsync("task", "verify-o1");
        Assert.Single(taskStream);
        Assert.Contains("approved", taskStream[0].Data);
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [Fact]
    public async Task An_event_with_no_matching_rule_is_ignored()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (_, registry) = SetUpRegistry(store);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [EchoRule()], Http = new HttpClient(handler), Dispatcher = new InProcessFollowUpDispatcher(registry), DeadLetters = store,
        });

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("SomethingElse", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None); // must not throw, must not call the handler

        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [Fact]
    public async Task An_always_failing_call_retries_up_to_MaxAttempts_then_dead_letters_without_throwing()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (_, registry) = SetUpRegistry(store);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify",
            Rules = [EchoRule()],
            Http = new HttpClient(handler),
            Dispatcher = new InProcessFollowUpDispatcher(registry),
            DeadLetters = store,
            Retry = new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMilliseconds(1)),
        });

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None); // must not throw

        Assert.Equal(3, handler.CallCount);
        var dl = Assert.Single(await store.ListDeadLettersAsync());
        Assert.Equal("extcall:verify", dl.Consumer);
        Assert.Contains("calling out", dl.Error);
        Assert.Empty(await store.LoadStreamAsync("task", "verify-o1"));
    }

    [Fact]
    public async Task A_HandleResponse_failure_dead_letters_without_dispatching_anything()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (_, registry) = SetUpRegistry(store);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var rule = new Rule
        {
            EventType = "OrderPlaced",
            BuildRequest = _ => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            HandleResponse = (_, _) => throw new InvalidOperationException("malformed response"),
        };
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [rule], Http = new HttpClient(handler), Dispatcher = new InProcessFollowUpDispatcher(registry), DeadLetters = store,
        });

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None);

        var dl = Assert.Single(await store.ListDeadLettersAsync());
        Assert.Contains("handling response", dl.Error);
    }

    [Fact]
    public async Task A_follow_up_rejected_by_its_target_decider_dead_letters_the_source_event()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (_, registry) = SetUpRegistry(store);
        // seed the target so CreateTask is rejected ("already exists")
        await registry.HandleAsync("task", "verify-o1", new Command("CreateTask", """{"title":"x"}"""));

        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("y") });
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [EchoRule()], Http = new HttpClient(handler), Dispatcher = new InProcessFollowUpDispatcher(registry), DeadLetters = store,
        });

        var appended = await store.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None); // must not throw

        var dl = Assert.Single(await store.ListDeadLettersAsync());
        Assert.Contains("dispatching follow-up", dl.Error);
    }

    [Fact]
    public void Two_rules_claiming_the_same_event_type_are_rejected_at_construction()
    {
        var rule = EchoRule();
        Assert.Throws<ArgumentException>(() => new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify",
            Rules = [rule, rule],
            Http = new HttpClient(),
            Dispatcher = null!,
            DeadLetters = null!,
        }));
    }

    [Fact]
    public void The_checkpoint_key_is_prefixed_with_extcall()
    {
        var consumer = new ExtCallerConsumer(new ExtCallerConfig
        {
            Name = "verify", Rules = [], Http = new HttpClient(), Dispatcher = null!, DeadLetters = null!,
        });
        Assert.Equal("extcall:verify", consumer.Name);
    }

    // --- GatewayFollowUpDispatcher (dotnetcqrs-multi-node Milestone 4) ---

    private static FollowUpDispatch SampleDispatch(string payload = """{"title":"x"}""") => new(
        "task", "verify-o1", "CreateTask", payload,
        Actor: "extcall:verify", CausationId: "ev-1", CorrelationId: "corr-1", CommandId: "extcall-abc123");

    [Fact]
    public async Task Gateway_dispatcher_posts_the_follow_up_to_the_gateway_route_with_headers_and_body()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        string? contentType = null;
        var handler = new FakeHandler(req =>
        {
            seen = req;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            contentType = req.Content.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"events":[]}""") };
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.invalid/") };
        var dispatcher = new GatewayFollowUpDispatcher(http);

        await dispatcher.DispatchAsync(SampleDispatch(), CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("http://gateway.invalid/api/cqrs/task/verify-o1/CreateTask", seen.RequestUri!.ToString());
        Assert.Equal("ev-1", seen.Headers.GetValues("Causation-Id").Single());
        Assert.Equal("corr-1", seen.Headers.GetValues("Correlation-Id").Single());
        Assert.Equal("extcall-abc123", seen.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("application/json", contentType);
        Assert.Equal("""{"title":"x"}""", body);
    }

    [Fact]
    public async Task Gateway_dispatcher_sends_an_empty_payload_as_an_empty_json_object()
    {
        string? body = null;
        var handler = new FakeHandler(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.invalid/") };
        var dispatcher = new GatewayFollowUpDispatcher(http);

        await dispatcher.DispatchAsync(SampleDispatch(payload: ""), CancellationToken.None);

        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task Gateway_dispatcher_omits_the_provenance_headers_when_their_ids_are_empty()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(req => { seen = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://host.invalid/gw/") };
        var dispatcher = new GatewayFollowUpDispatcher(http);

        await dispatcher.DispatchAsync(
            new FollowUpDispatch("task", "t1", "CreateTask", "{}", "extcall:x", "", "", ""), CancellationToken.None);

        // a BaseAddress that carries its own path prefix is preserved
        Assert.Equal("http://host.invalid/gw/api/cqrs/task/t1/CreateTask", seen!.RequestUri!.ToString());
        Assert.False(seen.Headers.Contains("Causation-Id"));
        Assert.False(seen.Headers.Contains("Correlation-Id"));
        Assert.False(seen.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task Gateway_dispatcher_throws_GatewayDispatchException_preserving_the_status_on_non_2xx()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("task already exists"),
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.invalid/") };
        var dispatcher = new GatewayFollowUpDispatcher(http);

        var ex = await Assert.ThrowsAsync<GatewayDispatchException>(
            () => dispatcher.DispatchAsync(SampleDispatch(), CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("task already exists", ex.Body);
    }

    [Fact]
    public void Gateway_dispatcher_ctor_rejects_an_HttpClient_with_no_base_address()
    {
        Assert.Throws<ArgumentException>(() => new GatewayFollowUpDispatcher(new HttpClient()));
    }
}
