using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>
/// <c>GET /metrics</c> (health/telemetry contract sections 6 and 7): every <c>cqrs_</c> series
/// present from the first scrape, counters zero-initialised for every outcome, the fixed buckets,
/// and command outcomes recorded by the gateway.
/// </summary>
public class MetricsTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 8, 30, 12, 345, TimeSpan.Zero);

    private static readonly string[] Families =
    [
        "cqrs_node_info", "cqrs_readiness_status", "cqrs_commands_total", "cqrs_command_duration_seconds",
        "cqrs_events_appended_total", "cqrs_write_lag_seconds", "cqrs_projection_lag_seconds", "cqrs_consumer_lag",
        "cqrs_consumer_state", "cqrs_dependency_up", "cqrs_deadletter_depth",
    ];

    private static NodeIdentity Identity() => new(
        "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", NodeIdentitySource.Persistent, "timesheets", "node-3",
        NodeIdentity.StackName, "writer", Started);

    /// <summary>The value of the one sample line <paramref name="series"/> (name plus labels, exactly).</summary>
    private static string Value(string body, string series)
    {
        var line = Assert.Single(body.Split('\n'), l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return line[(series.Length + 1)..];
    }

    [Fact]
    public async Task While_booting_every_family_is_present_and_the_counters_are_zero_for_every_outcome()
    {
        var health = new NodeHealth("node-3", Started);

        var body = await health.Metrics.RenderAsync(health);

        foreach (var family in Families)
            Assert.Contains($"# TYPE {family} ", body, StringComparison.Ordinal);
        foreach (var outcome in NodeMetrics.Outcomes)
        {
            Assert.Equal("0", Value(body, $"cqrs_commands_total{{status=\"{outcome}\"}}"));
            Assert.Equal("0", Value(body, $"cqrs_command_duration_seconds_count{{status=\"{outcome}\"}}"));
        }
        Assert.Equal("0", Value(body, "cqrs_events_appended_total"));
        Assert.Equal("1", Value(body, "cqrs_readiness_status{status=\"not_ready\"}"));
        Assert.Equal("0", Value(body, "cqrs_readiness_status{status=\"ready\"}"));
        // Identity is not resolved yet; the dead-letter source is not wired yet.
        Assert.Equal("1", Value(body,
            "cqrs_node_info{node_id=\"\",instance=\"\",host=\"node-3\",role=\"\",stack=\"dotnetcqrs\",contract_version=\"1.0\"}"));
        Assert.Equal("NaN", Value(body, "cqrs_deadletter_depth"));
    }

    [Fact]
    public async Task The_duration_buckets_are_the_contracts_fixed_boundaries()
    {
        var health = new NodeHealth("node-3", Started);

        var body = await health.Metrics.RenderAsync(health);

        var boundaries = Regex.Matches(body, "cqrs_command_duration_seconds_bucket\\{status=\"accepted\",le=\"([^\"]+)\"\\}")
            .Select(m => m.Groups[1].Value);
        Assert.Equal(
            ["0.001", "0.0025", "0.005", "0.01", "0.025", "0.05", "0.1", "0.25", "0.5", "1", "2.5", "5", "10", "30", "+Inf"],
            boundaries);
    }

    [Theory]
    [InlineData(200, "accepted")]
    [InlineData(400, "rejected")]
    [InlineData(401, "rejected")]
    [InlineData(403, "rejected")]
    [InlineData(404, "rejected")]
    [InlineData(410, "rejected")]
    [InlineData(422, "rejected")]
    [InlineData(409, "conflict")]
    [InlineData(503, "unavailable")]
    [InlineData(500, "error")]
    [InlineData(502, "error")]
    [InlineData(504, "error")]
    public void Outcomes_follow_the_contracts_status_table(int httpStatus, string outcome) =>
        Assert.Equal(outcome, NodeMetrics.OutcomeOf(httpStatus));

    [Fact]
    public async Task A_recorded_command_counts_once_and_fills_the_cumulative_buckets_it_fits()
    {
        var health = new NodeHealth("node-3", Started);

        health.Metrics.RecordCommand(200, TimeSpan.FromMilliseconds(30));
        health.Metrics.RecordCommand(409, TimeSpan.FromMilliseconds(2));
        var body = await health.Metrics.RenderAsync(health);

        Assert.Equal("1", Value(body, "cqrs_commands_total{status=\"accepted\"}"));
        Assert.Equal("1", Value(body, "cqrs_commands_total{status=\"conflict\"}"));
        Assert.Equal("0", Value(body, "cqrs_command_duration_seconds_bucket{status=\"accepted\",le=\"0.025\"}"));
        Assert.Equal("1", Value(body, "cqrs_command_duration_seconds_bucket{status=\"accepted\",le=\"0.05\"}"));
        Assert.Equal("1", Value(body, "cqrs_command_duration_seconds_bucket{status=\"accepted\",le=\"+Inf\"}"));
        Assert.Equal(0.03, double.Parse(Value(body, "cqrs_command_duration_seconds_sum{status=\"accepted\"}"), CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public async Task Once_serving_the_identity_readiness_and_every_consumer_are_reported()
    {
        var health = new NodeHealth("node-3", Started);
        health.SetIdentity(Identity());
        health.Metrics.EventAppended();
        health.Metrics.SetDeadLetterDepth(_ => Task.FromResult(2L));
        health.BeginCatchUp(() =>
        [
            new ConsumerStatus("orders", true, ConsumerState.Current, 7, 0, 0.25),
            new ConsumerStatus("ship", false, ConsumerState.Blocked, 3, 4, 90),
        ], TimeSpan.FromMinutes(1));

        var body = await health.Metrics.RenderAsync(health);

        Assert.Equal("1", Value(body,
            "cqrs_node_info{node_id=\"0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77\",instance=\"timesheets\",host=\"node-3\",role=\"writer\",stack=\"dotnetcqrs\",contract_version=\"1.0\"}"));
        Assert.Equal("1", Value(body, "cqrs_readiness_status{status=\"ready\"}"));
        Assert.Equal("1", Value(body, "cqrs_events_appended_total"));
        Assert.Equal("2", Value(body, "cqrs_deadletter_depth"));
        Assert.Equal("0", Value(body, "cqrs_write_lag_seconds"));
        // Only read models have a projection lag; every consumer has a lag and a state.
        Assert.Equal("0.25", Value(body, "cqrs_projection_lag_seconds{read_model=\"orders\"}"));
        Assert.DoesNotContain("read_model=\"ship\"", body, StringComparison.Ordinal);
        Assert.Equal("4", Value(body, "cqrs_consumer_lag{consumer=\"ship\"}"));
        Assert.Equal("1", Value(body, "cqrs_consumer_state{consumer=\"ship\",state=\"blocked\"}"));
        Assert.Equal("0", Value(body, "cqrs_consumer_state{consumer=\"ship\",state=\"current\"}"));
        Assert.Equal("1", Value(body, "cqrs_consumer_state{consumer=\"orders\",state=\"current\"}"));
    }

    [Fact]
    public async Task An_unknown_lag_or_a_failing_dead_letter_read_is_NaN()
    {
        var health = new NodeHealth("node-3", Started);
        health.SetIdentity(Identity());
        health.Metrics.SetDeadLetterDepth(_ => throw new InvalidOperationException("store gone"));
        health.BeginCatchUp(() => [new ConsumerStatus("orders", true, ConsumerState.Behind, null, null, null)], TimeSpan.FromMinutes(1));

        var body = await health.Metrics.RenderAsync(health);

        Assert.Equal("NaN", Value(body, "cqrs_projection_lag_seconds{read_model=\"orders\"}"));
        Assert.Equal("NaN", Value(body, "cqrs_consumer_lag{consumer=\"orders\"}"));
        Assert.Equal("NaN", Value(body, "cqrs_deadletter_depth"));
    }

    [Fact]
    public async Task Metrics_on_the_ops_port_is_prometheus_text()
    {
        var health = new NodeHealth("node-3", Started);
        await using var ops = await OpsServer.StartAsync(health, port: 0, bind: "127.0.0.1");
        using var client = new HttpClient { BaseAddress = ops.Address };

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("version=0.0.4", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Contains("# TYPE cqrs_commands_total counter", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gateway_records_each_commands_outcome_when_the_host_registers_the_metrics()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("task", new Decider<bool>
        {
            InitialState = () => false,
            Decide = (exists, cmd) => exists
                ? throw new InvalidOperationException("task already exists")
                : [new NewEvent("TaskCreated", cmd.Payload)],
            Evolve = (_, ev) => ev.Type == "TaskCreated",
        });
        var health = new NodeHealth("node-3", Started);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(health.Metrics);
        await using var app = builder.Build();
        app.MapCqrsGateway();
        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();

        using (await client.PostAsJsonAsync("/api/cqrs/task/t1/CreateTask", new { title = "a" })) { }
        using (await client.PostAsJsonAsync("/api/cqrs/task/t1/CreateTask", new { title = "a" })) { }
        using (await client.PostAsJsonAsync("/api/cqrs/nosuch/x/Go", new { })) { }

        var body = await health.Metrics.RenderAsync(health);
        Assert.Equal("1", Value(body, "cqrs_commands_total{status=\"accepted\"}"));
        Assert.Equal("2", Value(body, "cqrs_commands_total{status=\"rejected\"}"));
    }
}
