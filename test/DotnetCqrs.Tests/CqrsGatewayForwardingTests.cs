using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>
/// dotnetcqrs-multi-node Milestone 2: a secondary's gateway proxies a command to the
/// master instead of refusing it, and Milestone 1's read-only replica then picks up
/// the resulting event on its next catch-up pass -- proving the two milestones
/// compose, not just that each works alone. Two in-memory ASP.NET Core TestServers (no
/// real listening ports, matching CqrsGatewayEndpointsTests' own established pattern):
/// the secondary's forward HttpClient is wired straight to the master TestServer's own
/// in-memory HttpMessageHandler.
/// </summary>
public sealed class CqrsGatewayForwardingTests : IAsyncDisposable
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

    private readonly string _eventsPath = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-forward-{Guid.NewGuid():N}.db");
    private readonly SqliteEventStore _masterStore;
    private readonly WebApplication _masterApp;
    private readonly WebApplication _secondaryApp;
    private readonly HttpClient _secondaryClient;

    public CqrsGatewayForwardingTests()
    {
        _masterStore = SqliteEventStore.OpenAsync(_eventsPath).GetAwaiter().GetResult();
        var masterRegistry = new DeciderRegistry(_masterStore);
        masterRegistry.Register("task", TaskDecider());

        var masterBuilder = WebApplication.CreateBuilder();
        masterBuilder.WebHost.UseTestServer();
        masterBuilder.Services.AddSingleton(masterRegistry);
        _masterApp = masterBuilder.Build();
        _masterApp.MapCqrsGateway();
        _masterApp.StartAsync().GetAwaiter().GetResult();

        var forwardClient = _masterApp.GetTestServer().CreateClient();

        var secondaryBuilder = WebApplication.CreateBuilder();
        secondaryBuilder.WebHost.UseTestServer();
        // MapCqrsGateway's endpoint delegate still declares a DeciderRegistry
        // parameter for DI to resolve even on the forward path (it's just never read
        // there) -- any registry satisfies that; a real secondary never decides with
        // it, only a forwarding node's Program.cs would ever construct one this way.
        secondaryBuilder.Services.AddSingleton(new DeciderRegistry(_masterStore));
        _secondaryApp = secondaryBuilder.Build();
        _secondaryApp.MapCqrsGateway(forward: CqrsGatewayEndpoints.ForwardTo(forwardClient));
        _secondaryApp.StartAsync().GetAwaiter().GetResult();

        _secondaryClient = _secondaryApp.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _secondaryClient.Dispose();
        await _secondaryApp.DisposeAsync();
        await _masterApp.DisposeAsync();
        await _masterStore.DisposeAsync();
        if (File.Exists(_eventsPath)) File.Delete(_eventsPath);
    }

    [Fact]
    public async Task A_command_posted_to_the_secondary_lands_as_a_real_event_on_the_masters_store()
    {
        var response = await _secondaryClient.PostAsync("/api/cqrs/task/t1/CreateTask",
            new StringContent("""{"title":"write the docs"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("TaskCreated", events[0].GetProperty("type").GetString());

        var stream = await _masterStore.LoadStreamAsync("task", "t1");
        Assert.Single(stream);
    }

    [Fact]
    public async Task A_domain_rejection_forwarded_through_the_secondary_still_returns_400()
    {
        await _secondaryClient.PostAsync("/api/cqrs/task/t1/CreateTask", content: null); // seed it
        var response = await _secondaryClient.PostAsync("/api/cqrs/task/t1/CreateTask", content: null); // duplicate

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_forwarded_write_is_then_visible_to_the_secondarys_own_read_only_replica_on_its_next_catch_up_pass()
    {
        await _secondaryClient.PostAsync("/api/cqrs/task/t1/CreateTask",
            new StringContent("""{"title":"ship it"}""", Encoding.UTF8, "application/json"));

        await using var replica = await SqliteEventStore.OpenReadOnlyAsync(_eventsPath);
        await using var checkpoints = await SqliteEventStore.OpenAsync(
            Path.Combine(Path.GetTempPath(), $"dotnetcqrs-forward-checkpoints-{Guid.NewGuid():N}.db"));
        var engine = new ConsumerEngine(replica, checkpoints);
        var seen = new List<string>();
        engine.Register(new RecordingProjection(seen));

        await engine.RunOnceAsync();

        Assert.Contains("t1", seen);
    }

    private sealed class RecordingProjection(List<string> seen) : DotnetCqrs.Projections.IProjection
    {
        public string Name => "recording";
        public IReadOnlyList<string> Tables => [];
        public Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            if (ev.Type == "TaskCreated") seen.Add(ev.AggregateId);
            return Task.CompletedTask;
        }
    }
}
