using System.Net;
using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.ExtCalling;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>
/// dotnetcqrs-multi-node Milestone 4: <see cref="ExtCallerConsumer"/> dispatches its
/// follow-up command over HTTP to a <em>separate</em> instance's command gateway
/// instead of a local <see cref="DeciderRegistry"/> — and the causation/correlation
/// metadata survives the hop. One in-memory ASP.NET Core TestServer is the target
/// instance (a real <see cref="SqliteEventStore"/> behind <c>MapCqrsGateway</c>); the
/// source side is an ordinary store the consumer polls, with the third-party call
/// stubbed. Mirrors <see cref="CqrsGatewayForwardingTests"/>' two-server pattern (no
/// real listening ports).
/// </summary>
public sealed class ExtCallerRemoteDispatchTests : IAsyncDisposable
{
    private sealed class FakeThirdParty(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
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

    private readonly string _targetPath =
        Path.Combine(Path.GetTempPath(), $"dotnetcqrs-extcall-target-{Guid.NewGuid():N}.db");
    private readonly SqliteEventStore _targetStore;
    private readonly DeciderRegistry _targetRegistry;
    private readonly WebApplication _targetApp;
    private readonly HttpClient _gatewayClient;

    public ExtCallerRemoteDispatchTests()
    {
        _targetStore = SqliteEventStore.OpenAsync(_targetPath).GetAwaiter().GetResult();
        _targetRegistry = new DeciderRegistry(_targetStore);
        _targetRegistry.Register("task", TaskDecider());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_targetRegistry);
        _targetApp = builder.Build();
        _targetApp.MapCqrsGateway();
        _targetApp.StartAsync().GetAwaiter().GetResult();

        _gatewayClient = _targetApp.GetTestServer().CreateClient(); // BaseAddress http://localhost/
    }

    public async ValueTask DisposeAsync()
    {
        _gatewayClient.Dispose();
        await _targetApp.DisposeAsync();
        await _targetStore.DisposeAsync();
        if (File.Exists(_targetPath)) File.Delete(_targetPath);
    }

    private ExtCallerConsumer BuildConsumer(
        SqliteEventStore sourceStore, HttpStatusCode thirdPartyStatus = HttpStatusCode.OK, string thirdPartyBody = "approved")
        => new(new ExtCallerConfig
        {
            Name = "verify",
            Rules = [EchoRule()],
            Http = new HttpClient(new FakeThirdParty(thirdPartyStatus, thirdPartyBody)),
            Dispatcher = new GatewayFollowUpDispatcher(_gatewayClient),
            DeadLetters = sourceStore,
        });

    [Fact]
    public async Task A_follow_up_is_applied_as_a_real_event_on_the_target_instance_over_http()
    {
        await using var source = await SqliteEventStore.OpenAsync(":memory:");
        var consumer = BuildConsumer(source);

        var appended = await source.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None);

        var targetStream = await _targetStore.LoadStreamAsync("task", "verify-o1");
        Assert.Single(targetStream);
        Assert.Contains("approved", targetStream[0].Data);
        Assert.Empty(await source.ListDeadLettersAsync());
    }

    [Fact]
    public async Task The_causing_events_id_crosses_the_hop_as_causation_metadata_on_the_target_event()
    {
        await using var source = await SqliteEventStore.OpenAsync(":memory:");
        var consumer = BuildConsumer(source);

        var appended = await source.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None);

        var targetEvent = (await _targetStore.LoadStreamAsync("task", "verify-o1")).Single();
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(targetEvent.Metadata);
        Assert.NotNull(meta);
        Assert.Equal(appended[0].Id, meta!["causationId"].GetString());
        // o1's OrderPlaced is the root of the chain, so correlationId defaults to its own id
        Assert.Equal(appended[0].Id, meta["correlationId"].GetString());
    }

    [Fact]
    public async Task A_domain_rejection_at_the_target_dead_letters_the_source_event_with_the_status()
    {
        await using var source = await SqliteEventStore.OpenAsync(":memory:");
        // seed the target so its CreateTask is rejected ("already exists")
        await _targetRegistry.HandleAsync("task", "verify-o1", new Command("CreateTask", """{"title":"pre"}"""));
        var consumer = BuildConsumer(source);

        var appended = await source.AppendAsync("order", "o1", 0, [new NewEvent("OrderPlaced", "{}")]);
        await consumer.ApplyAsync(appended[0], CancellationToken.None); // must not throw

        var dl = Assert.Single(await source.ListDeadLettersAsync());
        Assert.Equal("extcall:verify", dl.Consumer);
        Assert.Contains("dispatching follow-up", dl.Error);
        Assert.Contains("400", dl.Error);
        Assert.Single(await _targetStore.LoadStreamAsync("task", "verify-o1")); // only the seed event; the follow-up was refused
    }
}
