using System.Net;
using System.Security.Claims;
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
/// Erasure cannot be undone, and the gateway allows a command that has no declared policy, so the built-in
/// <c>dataSubject</c> aggregate fails CLOSED: with no <c>authorize</c> policy wired at all, the gateway refuses
/// every one of its commands (a scaffold host is unauthenticated, so without this anyone could shred anyone).
/// A host that wires a policy decides for itself; every other aggregate behaves as before.
/// </summary>
public class CqrsGatewayDataSubjectDefaultTests
{
    private sealed record Host(SqliteEventStore Store, WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(
        Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>>? authorize)
    {
        var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.RegisterDataSubjects();
        registry.Register("task", new Decider<bool>
        {
            InitialState = () => false,
            Decide = (_, cmd) => [new NewEvent("TaskCreated", cmd.Payload)],
            Evolve = (_, ev) => ev.Type == "TaskCreated",
        });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        if (authorize is null) app.MapCqrsGateway(); else app.MapCqrsGateway(authorize: authorize);
        await app.StartAsync();
        return new Host(store, app, app.GetTestServer().CreateClient());
    }

    [Theory]
    [InlineData("RequestErasure")]
    [InlineData("ApproveErasure")]
    [InlineData("PlaceLegalHold")]
    [InlineData("EraseSubject")]
    public async Task With_no_authorize_policy_every_dataSubject_command_is_refused_and_nothing_is_recorded(string command)
    {
        await using var host = await StartAsync(authorize: null);

        var response = await host.Client.PostAsync($"/api/cqrs/dataSubject/s1/{command}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await host.Store.LoadStreamAsync(DataSubject.Aggregate, "s1"));
        Assert.Contains("authorize policy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("datasubject")]
    [InlineData("DATASUBJECT")]
    [InlineData("dataSubject")]
    public async Task A_differently_cased_route_cannot_slip_past_the_guard(string aggregate)
    {
        await using var host = await StartAsync(authorize: null);

        var response = await host.Client.PostAsync($"/api/cqrs/{aggregate}/s1/EraseSubject", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await host.Store.LoadStreamAsync(DataSubject.Aggregate, "s1"));
    }

    [Fact]
    public async Task Other_aggregates_are_unaffected_when_no_policy_is_wired()
    {
        await using var host = await StartAsync(authorize: null);

        var response = await host.Client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await host.Store.LoadStreamAsync("task", "t1"));
    }

    [Fact]
    public async Task A_host_that_wires_a_policy_decides_for_itself()
    {
        await using var allow = await StartAsync((_, _, _, _, _, _) => Task.FromResult(true));
        var allowed = await allow.Client.PostAsync("/api/cqrs/dataSubject/s1/RequestErasure", content: null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(await allow.Store.LoadStreamAsync(DataSubject.Aggregate, "s1"));

        await using var deny = await StartAsync((_, _, _, _, _, _) => Task.FromResult(false));
        var denied = await deny.Client.PostAsync("/api/cqrs/dataSubject/s1/EraseSubject", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await deny.Store.LoadStreamAsync(DataSubject.Aggregate, "s1"));
    }
}
