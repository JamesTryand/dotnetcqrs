using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>
/// Proves <see cref="CqrsGatewayEndpoints.MapCqrsGateway"/>'s new <c>authorize</c> hook
/// (schema 2.5.0's command authorization, the pluggable seam
/// <c>Generated.CommandAuthorization.AuthorizeAsync</c> plugs into) end to end over a
/// real HTTP round trip -- a rejection returns 403 and the decider never runs (no event
/// appended), an approval dispatches normally, and the hook actually receives the
/// request's own parsed JSON payload (not just aggregate/command/aggregateId), the one
/// thing <see cref="CommandAuthorizationGeneratorTests"/> can't itself prove since it
/// calls the generated evaluator directly rather than through the gateway.
/// </summary>
public class CqrsGatewayAuthorizeHookTests : IAsyncDisposable
{
    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (_, cmd) => [new NewEvent("TaskCreated", cmd.Payload)],
        Evolve = (_, ev) => ev.Type == "TaskCreated",
    };

    private readonly SqliteEventStore _store;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    /// <summary>Reassignable per test -- the constructor wires a closure that forwards to
    /// whatever this points to at request time, so each <c>[Fact]</c> can set its own
    /// authorize behavior without a fresh fixture.</summary>
    private Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>> _authorize =
        (_, _, _, _, _, _) => Task.FromResult(true);

    public CqrsGatewayAuthorizeHookTests()
    {
        _store = SqliteEventStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        var registry = new DeciderRegistry(_store);
        registry.Register("task", TaskDecider());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        _app = builder.Build();
        _app.MapCqrsGateway(authorize: (user, aggregate, command, aggregateId, payload, ct) =>
            _authorize(user, aggregate, command, aggregateId, payload, ct));
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _store.DisposeAsync();
    }

    [Fact]
    public async Task A_rejecting_authorize_hook_returns_403_and_the_decider_never_runs()
    {
        _authorize = (_, _, _, _, _, _) => Task.FromResult(false);

        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stream = await _store.LoadStreamAsync("task", "t1");
        Assert.Empty(stream);
    }

    [Fact]
    public async Task An_approving_authorize_hook_lets_the_command_dispatch_normally()
    {
        _authorize = (_, _, _, _, _, _) => Task.FromResult(true);

        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await _store.LoadStreamAsync("task", "t1");
        Assert.Single(stream);
    }

    [Fact]
    public async Task The_hook_receives_the_requests_own_parsed_JSON_payload()
    {
        // Reads the JsonElement DURING the call, not after -- HandleAsync disposes the
        // backing JsonDocument once authorize returns (same lifetime as the rest of the
        // request), so a real authorize implementation (the generated
        // CommandAuthorization.AuthorizeAsync included) is only ever expected to read
        // it synchronously within its own call, never stash it for later.
        var wasObject = false;
        string? role = null;
        _authorize = (_, _, _, _, payload, _) =>
        {
            wasObject = payload.ValueKind == JsonValueKind.Object;
            role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;
            return Task.FromResult(true);
        };

        var response = await _client.PostAsJsonAsync("/api/cqrs/task/t2/CreateTask", new { role = "manager" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(wasObject);
        Assert.Equal("manager", role);
    }

    [Fact]
    public async Task The_hook_receives_aggregate_command_and_aggregateId_correctly()
    {
        (string Aggregate, string Command, string AggregateId)? seen = null;
        _authorize = (_, aggregate, command, aggregateId, _, _) =>
        {
            seen = (aggregate, command, aggregateId);
            return Task.FromResult(true);
        };

        await _client.PostAsync("/api/cqrs/task/t3/CreateTask", content: null);

        Assert.Equal(("task", "CreateTask", "t3"), seen);
    }
}
