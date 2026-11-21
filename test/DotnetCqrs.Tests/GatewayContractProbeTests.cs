using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using DotnetCqrs.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetCqrs.Tests;

/// <summary>
/// The conformance probe for the command gateway's HTTP contract, v0.1 (frozen 2026-11-21, the notebook's
/// <c>platform/cqrs-runtime-contract</c> <c>GATEWAY-HTTP-CONTRACT.md</c>): one case per row of G3 this stack can
/// reach, the G4 problem body on each error, the G5 reasons, the G6 accepted body with and without events, G7's
/// replay rules (a 409 decided again included) and G8's <c>Retry-After</c>. A real Kestrel listener on a loopback
/// port, so the request goes through the same server the generated host runs (percent-decoding included), with the
/// service-token scheme (G2), an <c>authorize</c> hook and an idempotency store wired as a generated host wires them.
/// </summary>
public sealed class GatewayContractProbeTests : IAsyncLifetime
{
    private const string OperatorRole = "operator";

    private sealed class FakeDbException(string message) : DbException(message);

    /// <summary>A clock a test moves by hand, for the retention window.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Fails the next N appends' protect step with the given failure, then lets them through: a failure
    /// around the decision, exactly where a key-service or store call would fail.</summary>
    private sealed class ScriptedProtector : IPiiProtector
    {
        public Queue<Func<Exception>> Failures { get; } = new();

        public Task<object> RevealAsync(object state, CancellationToken ct) => Task.FromResult(state);

        public Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct) =>
            Failures.TryDequeue(out var failure) ? throw failure() : Task.FromResult(events);
    }

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) => cmd.Name switch
        {
            "CreateTask" when exists => throw new AlreadyExistsException("task already exists"),
            "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
            "CompleteTask" when !exists => throw new DoesNotExistException("task does not exist"),
            "CompleteTask" => [new NewEvent("TaskCompleted", "{}")],
            "Touch" => [],
            // A refusal with capitals and punctuation of its own: it must reach the caller exactly as written.
            "Refuse" => throw new InvalidOperationException("Not today: the task's owner said NO."),
            "NeedsReveal" => throw new RevealRequiredException("decision read a protected value"),
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (_, ev) => ev.Type == "TaskCreated" || ev.Type == "TaskCompleted",
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-probe-{Guid.NewGuid():N}");
    private readonly ScriptedProtector _protector = new();
    private readonly ManualClock _clock = new();
    private SqliteEventStore _store = null!;
    private InMemoryIdempotencyStore _idempotency = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _operatorToken = "";
    private string _viewerToken = "";
    private Func<Exception>? _authorizeFailure;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteEventStore.OpenAsync(Path.Combine(_dir, "events.db"));
        var registry = new DeciderRegistry(_store);
        registry.Register("task", TaskDecider(), _protector);
        registry.Register("plain", TaskDecider());

        var tokens = new ServiceTokenStore(Path.Combine(_dir, "service-tokens.json"));
        _operatorToken = tokens.Issue("payments", OperatorRole).Token;
        _viewerToken = tokens.Issue("dashboard", "viewer").Token;
        _idempotency = new InMemoryIdempotencyStore(time: _clock);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IIdempotencyStore>(_idempotency);
        builder.Services.AddAuthentication(ServiceTokenDefaults.Scheme).AddServiceTokens(tokens);
        builder.Services.AddAuthorization();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        // The policy a generated host's CommandAuthorization would make: only an operator may send commands.
        _app.MapCqrsGateway(authorize: (user, _, _, _, _, _) => _authorizeFailure is { } failure
            ? throw failure()
            : Task.FromResult(user.FindFirstValue("role") == OperatorRole)).RequireAuthorization();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _store.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task<HttpResponseMessage> SendAsync(string path, string? body = null, string? key = null, string? token = null, bool anonymous = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!anonymous)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _operatorToken);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request);
    }

    /// <summary>G4: every non-2xx answer is a problem body with at least status, title and detail.</summary>
    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()), $"no title: {text}");
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()), $"no detail: {text}");
        return problem;
    }

    private static async Task<JsonElement> AssertEventsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        Assert.True(body.ValueKind == JsonValueKind.Object, $"G6: the accepted body is an object, got {text}");
        var events = body.GetProperty("events");
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        return events;
    }

    // ---- G3 200 / G6 ----

    [Fact]
    public async Task G3_200_decided_answers_the_events_with_data_and_metadata_as_objects()
    {
        var events = await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", """{"title":"write the docs"}"""));

        var ev = Assert.Single(events.EnumerateArray());
        Assert.Equal("TaskCreated", ev.GetProperty("type").GetString());
        Assert.Equal("task", ev.GetProperty("aggregate").GetString());
        Assert.Equal("t1", ev.GetProperty("aggregateId").GetString());
        Assert.Equal(JsonValueKind.Object, ev.GetProperty("data").ValueKind);
        Assert.Equal("write the docs", ev.GetProperty("data").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Object, ev.GetProperty("metadata").ValueKind);
        Assert.Equal("payments", ev.GetProperty("metadata").GetProperty("actor").GetString());
    }

    [Fact]
    public async Task G3_200_with_no_events_is_an_empty_array_never_null()
    {
        var events = await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/Touch"));
        Assert.Equal(0, events.GetArrayLength());
    }

    [Fact]
    public async Task G1_the_id_is_percent_decoded_by_the_gateway()
    {
        var events = await AssertEventsAsync(await SendAsync("/api/cqrs/task/" + Uri.EscapeDataString("a/b c%") + "/CreateTask"));

        Assert.Equal("a/b c%", Assert.Single(events.EnumerateArray()).GetProperty("aggregateId").GetString());
        Assert.Single(await _store.LoadStreamAsync("task", "a/b c%"));
    }

    // ---- G3 400 / G4 / G5 ----

    [Fact]
    public async Task G3_400_a_refusal_carries_the_deciders_message_verbatim()
    {
        var problem = await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/Refuse"), HttpStatusCode.BadRequest);
        Assert.Equal("Not today: the task's owner said NO.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task G5_a_command_needing_a_stream_that_is_not_there_has_reason_does_not_exist()
    {
        var problem = await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CompleteTask"), HttpStatusCode.BadRequest);
        Assert.Equal("task does not exist", problem.GetProperty("detail").GetString());
        Assert.Equal("does-not-exist", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task G5_a_create_sent_to_an_existing_stream_has_reason_already_exists()
    {
        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask"));

        var problem = await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask"), HttpStatusCode.BadRequest);
        Assert.Equal("task already exists", problem.GetProperty("detail").GetString());
        Assert.Equal("already-exists", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task G3_400_malformed_JSON_with_an_authorize_hook_wired_is_a_400_not_a_500()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", "{\"title\": "), HttpStatusCode.BadRequest);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G1_a_body_that_is_not_a_JSON_object_is_a_400()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", "[1,2]"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task G7_an_idempotency_key_over_200_characters_is_a_400()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: new string('k', 201)), HttpStatusCode.BadRequest);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
        // 200 is allowed.
        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: new string('k', 200)));
    }

    // ---- G2 401 / 403 ----

    [Fact]
    public async Task G2_401_with_no_credential()
    {
        var response = await SendAsync("/api/cqrs/task/t1/CreateTask", anonymous: true);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G2_401_with_an_unknown_token()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", token: "dcq_not-a-token-anyone-was-issued"), HttpStatusCode.Unauthorized);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G2_403_with_a_token_whose_role_may_not_send_the_command()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", token: _viewerToken), HttpStatusCode.Forbidden);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    // ---- G3 404 / 409 / 410 / 422 / 5xx ----

    [Fact]
    public async Task G3_404_unknown_aggregate()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/nope/x1/Anything"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task G3_409_lost_a_race_nothing_applied()
    {
        _protector.Failures.Enqueue(() => new ConcurrencyException("task", "t1", 1, 0));
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask"), HttpStatusCode.Conflict);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G3_410_the_subject_was_erased()
    {
        _protector.Failures.Enqueue(() => new SubjectErasedException("t1"));
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask"), HttpStatusCode.Gone);
    }

    [Fact]
    public async Task G3_422_an_idempotency_key_reused_for_a_different_request()
    {
        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", """{"title":"a"}""", key: "k-422"));

        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", """{"title":"b"}""", key: "k-422"), (HttpStatusCode)422);
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t2/CreateTask", """{"title":"a"}""", key: "k-422"), (HttpStatusCode)422);
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/Touch", """{"title":"a"}""", key: "k-422"), (HttpStatusCode)422);
        Assert.Empty(await _store.LoadStreamAsync("task", "t2"));
    }

    [Fact]
    public async Task G3_503_a_dependency_failure_before_the_decision_carries_Retry_After()
    {
        _protector.Failures.Enqueue(() => new FakeDbException("could not connect"));
        var response = await SendAsync("/api/cqrs/task/t1/CreateTask");
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        AssertRetryAfter(response);
    }

    [Fact]
    public async Task G3_503_a_dependency_failure_in_the_authorization_before_the_decision()
    {
        // A policy reading a read model whose store is down: before the decision, so 503, not 500.
        _authorizeFailure = () => new FakeDbException("read model store down");
        var response = await SendAsync("/api/cqrs/task/t1/CreateTask");
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        AssertRetryAfter(response);
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G3_503_a_reader_with_no_writer_carries_Retry_After()
    {
        await using var reader = await StartAsync(app => app.MapCqrsGateway(forward: CqrsGatewayEndpoints.RefuseReadOnly));
        var response = await reader.Client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);
        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        AssertRetryAfter(response);
    }

    private static void AssertRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        Assert.True(retryAfter?.Delta is { } delta && delta > TimeSpan.Zero, "G8: a 503 carries Retry-After in seconds");
    }

    [Fact]
    public async Task G3_500_a_fault_in_the_host()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/plain/t1/NeedsReveal"), HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task G3_502_and_504_a_reader_that_cannot_reach_its_writer()
    {
        await using var unreachable = await StartAsync(app => app.MapCqrsGateway(forward: CqrsGatewayEndpoints.ForwardTo(
            new HttpClient(new FailingHandler(new HttpRequestException("refused"))) { BaseAddress = new Uri("http://writer.invalid/") })));
        await AssertProblemAsync(await unreachable.Client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null), HttpStatusCode.BadGateway);

        await using var slow = await StartAsync(app => app.MapCqrsGateway(forward: CqrsGatewayEndpoints.ForwardTo(
            new HttpClient(new FailingHandler(new TaskCanceledException("timed out"))) { BaseAddress = new Uri("http://writer.invalid/") })));
        await AssertProblemAsync(await slow.Client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null), HttpStatusCode.GatewayTimeout);
    }

    // ---- G7 replay rules ----

    [Fact]
    public async Task G7_the_same_key_and_request_replays_the_first_200_verbatim_and_decides_nothing_again()
    {
        var first = await SendAsync("/api/cqrs/task/t1/CreateTask", """{"title":"a"}""", key: "k-replay");
        var firstBody = await first.Content.ReadAsStringAsync();
        await AssertEventsAsync(first);

        var again = await SendAsync("/api/cqrs/task/t1/CreateTask", """{"title":"a"}""", key: "k-replay");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstBody, await again.Content.ReadAsStringAsync());
        Assert.Single(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G7_a_409_is_not_replayed_the_same_key_is_decided_again()
    {
        _protector.Failures.Enqueue(() => new ConcurrencyException("task", "t1", 1, 0));
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-409"), HttpStatusCode.Conflict);

        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-409"));
        Assert.Single(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task G7_a_400_is_never_recorded_so_does_not_exist_succeeds_once_the_stream_is_there()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CompleteTask", key: "k-400"), HttpStatusCode.BadRequest);
        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask"));

        var events = await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CompleteTask", key: "k-400"));
        Assert.Equal("TaskCompleted", Assert.Single(events.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task G7_a_403_is_never_recorded_so_a_caller_who_may_send_it_is_decided()
    {
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-403", token: _viewerToken), HttpStatusCode.Forbidden);
        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-403"));
    }

    [Fact]
    public async Task G7_a_503_is_never_recorded_so_the_retry_is_decided()
    {
        _protector.Failures.Enqueue(() => new FakeDbException("could not connect"));
        await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-503"), HttpStatusCode.ServiceUnavailable);

        await AssertEventsAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-503"));
    }

    [Fact]
    public async Task G7_400_401_403_404_410_and_503_leave_nothing_recorded()
    {
        await SendAsync("/api/cqrs/task/t1/Refuse", key: "n-400");
        await SendAsync("/api/cqrs/task/t1/Refuse", key: "n-401", anonymous: true);
        await SendAsync("/api/cqrs/task/t1/Refuse", key: "n-403", token: _viewerToken);
        await SendAsync("/api/cqrs/nope/t1/Refuse", key: "n-404");
        _protector.Failures.Enqueue(() => new SubjectErasedException("t1"));
        await SendAsync("/api/cqrs/task/t1/CreateTask", key: "n-410");
        _protector.Failures.Enqueue(() => new FakeDbException("down"));
        await SendAsync("/api/cqrs/task/t1/CreateTask", key: "n-503");

        foreach (var key in new[] { "n-400", "n-401", "n-403", "n-404", "n-410", "n-503" })
            Assert.Null(await _idempotency.FindAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task G7_a_recorded_answer_is_kept_at_least_7_days()
    {
        var first = await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-week");
        var firstBody = await first.Content.ReadAsStringAsync();

        _clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);
        var replay = await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-week");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

        // Past the window it is gone, so the command is decided again (and refused: the task exists).
        _clock.Now += TimeSpan.FromDays(1);
        var problem = await AssertProblemAsync(await SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-week"), HttpStatusCode.BadRequest);
        Assert.Equal("already-exists", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task G7_concurrent_duplicates_under_one_key_are_decided_once()
    {
        var sends = Enumerable.Range(0, 8).Select(_ => SendAsync("/api/cqrs/task/t1/CreateTask", key: "k-race")).ToList();
        var responses = await Task.WhenAll(sends);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public void G7_a_retention_under_7_days_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryIdempotencyStore(TimeSpan.FromDays(6)));
        Assert.Equal(TimeSpan.FromDays(7), IdempotencyRetention.Default);
        Assert.Equal(TimeSpan.FromDays(30), IdempotencyRetention.Parse("30"));
        Assert.Throws<InvalidIdempotencySettingException>(() => IdempotencyRetention.Parse("6"));
        Assert.Throws<InvalidIdempotencySettingException>(() => IdempotencyRetention.Parse("a week"));
    }

    // ---- helpers ----

    private sealed class FailingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class Started(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private async Task<Started> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new DeciderRegistry(_store));
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new Started(app, new HttpClient { BaseAddress = new Uri(address) });
    }
}
