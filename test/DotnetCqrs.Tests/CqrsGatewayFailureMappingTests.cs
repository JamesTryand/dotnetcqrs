using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>
/// The gateway's status codes must keep "the decider refused" (400) apart from "a
/// dependency failed" (503) and "the host is mis-wired" (500): a client retries the
/// second, never the first. Each failure is raised from the dispatch shell around a pure
/// <c>Decide</c> -- here the aggregate's <see cref="IPiiProtector"/>, which runs between
/// decide and append exactly where a key-service call would fail.
/// </summary>
public class CqrsGatewayFailureMappingTests : IAsyncDisposable
{
    private const string Secret = "kms.internal:8200";

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class ThrowingProtector : IPiiProtector
    {
        public Func<Exception>? Failure { get; set; }

        public Task<object> RevealAsync(object state, CancellationToken ct) => Task.FromResult(state);

        public Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct) =>
            Failure is null ? Task.FromResult(events) : throw Failure();
    }

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) => cmd.Name switch
        {
            "CreateTask" when exists => throw new InvalidOperationException("task already exists"),
            "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
            "NeedsReveal" => throw new RevealRequiredException("decision read a protected value"),
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (_, ev) => ev.Type == "TaskCreated",
    };

    private readonly SqliteEventStore _store;
    private readonly ThrowingProtector _protector = new();
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public CqrsGatewayFailureMappingTests()
    {
        _store = SqliteEventStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        var registry = new DeciderRegistry(_store);
        registry.Register("task", TaskDecider(), _protector);
        registry.Register("plain", TaskDecider()); // no protector: RevealRequired escapes

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        _app = builder.Build();
        _app.MapCqrsGateway();
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _store.DisposeAsync();
    }

    public static TheoryData<string> UnavailableFailures =>
        ["db", "http", "kms-protocol", "read-only", "timeout", "cancelled"];

    private static Exception MakeFailure(string kind) => kind switch
    {
        "db" => new FakeDbException($"could not connect to {Secret}"),
        "http" => new HttpRequestException($"Connection refused ({Secret})"),
        "kms-protocol" => new KmsProtocolException($"encrypt response from {Secret} was empty"),
        "read-only" => new ReadOnlyStoreException("append"),
        "timeout" => new TimeoutException($"{Secret} did not answer"),
        "cancelled" => new TaskCanceledException($"HttpClient.Timeout calling {Secret}"),
        "kms-key-missing" => new KmsKeyNotFoundException("subject-1"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private async Task<(HttpStatusCode Status, JsonElement Problem)> PostAsync(string path)
    {
        var response = await _client.PostAsync(path, content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    [Theory]
    [MemberData(nameof(UnavailableFailures))]
    public async Task A_dependency_failure_returns_503_without_leaking_internals(string kind)
    {
        _protector.Failure = () => MakeFailure(kind);

        var (status, problem) = await PostAsync("/api/cqrs/task/t1/CreateTask");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("dependency unavailable", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(Secret, problem.GetRawText());
        Assert.Empty(await _store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task A_known_wiring_fault_returns_500_not_400()
    {
        // The decision read protected state and no protector is registered to reveal it:
        // the host is mis-wired, the command was never decided.
        var (status, problem) = await PostAsync("/api/cqrs/plain/t1/NeedsReveal");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("internal error", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_missing_kms_key_is_a_500_wiring_fault()
    {
        _protector.Failure = () => MakeFailure("kms-key-missing");

        var (status, _) = await PostAsync("/api/cqrs/task/t1/CreateTask");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }

    [Fact]
    public async Task An_unclassified_failure_outside_Decide_returns_500_not_400()
    {
        // Not a known dependency type and not thrown by Decide: a bug in the shell. Before,
        // it was indistinguishable from a rejection and answered 400 with its message.
        _protector.Failure = () => new InvalidOperationException($"protector bug near {Secret}");

        var (status, problem) = await PostAsync("/api/cqrs/task/t1/CreateTask");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("internal error", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(Secret, problem.GetRawText());
    }

    [Fact]
    public async Task A_domain_rejection_still_returns_400_with_the_deciders_message()
    {
        await PostAsync("/api/cqrs/task/t1/CreateTask");

        var (status, problem) = await PostAsync("/api/cqrs/task/t1/CreateTask");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("task already exists", problem.GetProperty("detail").GetString());
    }
}
