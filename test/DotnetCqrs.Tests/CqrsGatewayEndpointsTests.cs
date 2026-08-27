using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetCqrs.Tests;

/// <summary>
/// Exercises the gateway over a real HTTP round trip (routing, model binding, JSON
/// serialization, status codes) via ASP.NET Core's in-memory TestServer -- not just
/// calling the handler delegate directly.
/// </summary>
public class CqrsGatewayEndpointsTests : IAsyncDisposable
{
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

    private readonly SqliteEventStore _store;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public CqrsGatewayEndpointsTests()
    {
        _store = SqliteEventStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        var registry = new DeciderRegistry(_store);
        registry.Register("task", TaskDecider());

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

    [Fact]
    public async Task A_valid_command_appends_and_returns_the_produced_events()
    {
        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask",
            new StringContent("""{"title":"write the docs"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("TaskCreated", events[0].GetProperty("type").GetString());

        var stream = await _store.LoadStreamAsync("task", "t1");
        Assert.Single(stream);
    }

    [Fact]
    public async Task An_empty_body_is_treated_as_an_empty_JSON_payload()
    {
        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unregistered_aggregate_returns_404()
    {
        var response = await _client.PostAsync("/api/cqrs/nope/x1/Anything", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_domain_rejection_returns_400()
    {
        await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null); // seed it
        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null); // duplicate

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
