using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// <c>GET /readyz</c> (health/telemetry contract section 4) for the lifecycle and read-model
/// dimensions: one test per row of <c>STATE-MACHINES.md</c>'s "Readiness: lifecycle" and
/// "Readiness: read_models" tables that this step covers, plus machine 1's catch-up transitions.
/// The consumer status is faked; <see cref="ConsumerEngineTests"/> covers how the engine
/// produces it.
/// </summary>
public class ReadinessTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 8, 30, 12, 345, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Node
    {
        public ManualClock Clock { get; } = new(Started);
        public List<ConsumerStatus> Consumers { get; } = [];
        public List<string> Log { get; } = [];
        public NodeHealth Health { get; }

        public Node(string role)
        {
            Health = new NodeHealth("node-3", Started, Clock);
            Health.SetIdentity(new NodeIdentity(
                "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", NodeIdentitySource.Persistent, "timesheets", "node-3",
                NodeIdentity.StackName, role, Started));
            // A reader's replication is fresh here; ReplicationTests covers the other states.
            if (role == "reader")
                Health.SetReplication(() => new ReplicationStatus(ReplicationState.Fresh, 0));
        }

        public void Set(string name, ConsumerState state, bool readModel = true, double lag = 0)
        {
            Consumers.RemoveAll(c => c.Name == name);
            Consumers.Add(new ConsumerStatus(name, readModel, state, 0, 0, lag));
        }

        public void BeginCatchUp() => Health.BeginCatchUp(() => [.. Consumers], TimeSpan.FromSeconds(60), Log.Add);

        /// <summary>Status code, status, and the reasons comma-joined (so tuples compare by value).</summary>
        public (int Code, string Status, string Reasons) Readyz()
        {
            var (code, body) = Health.Readyz();
            var reasons = string.Join(",", (IEnumerable<string>)body["reasons"]!);
            return (code, (string)body["status"]!, reasons);
        }
    }

    // --- Readiness: lifecycle ---

    [Fact]
    public void Booting_is_not_ready_starting()
    {
        var node = new Node("writer");

        Assert.Equal((503, "not_ready", "starting"), node.Readyz());
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("reader")]
    public void Catching_up_is_not_ready_catching_up(string role)
    {
        var node = new Node(role);
        node.Set("orders", ConsumerState.Behind);

        node.BeginCatchUp();

        Assert.Equal(NodeLifecycleState.CatchingUp, node.Health.Lifecycle);
        var (code, status, reasons) = node.Readyz();
        Assert.Equal((503, "not_ready"), (code, status));
        Assert.StartsWith("catching_up", reasons, StringComparison.Ordinal);
    }

    [Fact]
    public void Serving_with_every_read_model_current_is_ready_with_no_reasons()
    {
        var node = new Node("writer");
        node.Set("orders", ConsumerState.Behind);
        node.BeginCatchUp();

        node.Set("orders", ConsumerState.Current);

        Assert.Equal((200, "ready", ""), node.Readyz());
        Assert.Equal(NodeLifecycleState.Serving, node.Health.Lifecycle);
        Assert.Contains(node.Log, l => l.Contains("readiness opened", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_with_no_read_models_serves_as_soon_as_boot_completes()
    {
        var node = new Node("writer");

        node.BeginCatchUp();

        Assert.Equal(NodeLifecycleState.Serving, node.Health.Lifecycle);
    }

    [Fact]
    public void Draining_is_not_ready_draining()
    {
        var node = new Node("writer");
        node.BeginCatchUp();

        node.Health.SetLifecycle(NodeLifecycleState.Draining);

        Assert.Equal((503, "not_ready", "draining"), node.Readyz());
    }

    // --- Readiness: read_models ---

    [Theory]
    [InlineData("writer", ConsumerState.Behind, 200, "degraded", "projection_behind")]
    [InlineData("reader", ConsumerState.Behind, 503, "not_ready", "projection_behind")]
    [InlineData("writer", ConsumerState.Blocked, 200, "degraded", "projection_blocked")]
    [InlineData("reader", ConsumerState.Blocked, 503, "not_ready", "projection_blocked")]
    public void A_read_model_that_falls_behind_while_serving_reports_by_role(
        string role, ConsumerState state, int code, string status, string reason)
    {
        var node = new Node(role);
        node.Set("orders", ConsumerState.Current);
        node.BeginCatchUp();

        node.Set("orders", state);

        Assert.Equal((code, status, reason), node.Readyz());
        // Falling behind later is a condition, not a lifecycle step.
        Assert.Equal(NodeLifecycleState.Serving, node.Health.Lifecycle);
    }

    [Fact]
    public void The_worst_read_model_decides_blocked_over_behind()
    {
        var node = new Node("reader");
        node.Set("orders", ConsumerState.Current);
        node.BeginCatchUp();

        node.Set("orders", ConsumerState.Behind);
        node.Set("customers", ConsumerState.Blocked);

        Assert.Equal((503, "not_ready", "projection_blocked"), node.Readyz());
    }

    [Fact]
    public void Consumers_that_are_not_read_models_never_affect_readiness()
    {
        var node = new Node("reader");
        node.Set("orders", ConsumerState.Current);
        node.Set("ship-reactor", ConsumerState.Blocked, readModel: false, lag: 999);

        node.BeginCatchUp();

        Assert.Equal((200, "ready", ""), node.Readyz());
        Assert.Equal(0.0, Checks(node)["projection_lag_seconds"]);
    }

    [Fact]
    public void Projection_lag_is_the_largest_among_the_read_models()
    {
        var node = new Node("writer");
        node.Set("orders", ConsumerState.Current, lag: 0.25);
        node.Set("customers", ConsumerState.Current, lag: 1.5);
        node.BeginCatchUp();

        Assert.Equal(1.5, Checks(node)["projection_lag_seconds"]);
        Assert.Equal(0.0, Checks(node)["write_lag_seconds"]);
    }

    // --- Machine 1: the catch-up deadline ---

    [Fact]
    public void A_writer_past_its_catch_up_deadline_serves_anyway_and_reports_degraded()
    {
        var node = new Node("writer");
        node.Set("orders", ConsumerState.Blocked);
        node.BeginCatchUp();

        node.Clock.Now = Started.AddSeconds(59);
        Assert.Equal(503, node.Readyz().Code);

        node.Clock.Now = Started.AddSeconds(61);
        Assert.Equal((200, "degraded", "projection_blocked"), node.Readyz());
        Assert.Equal(NodeLifecycleState.Serving, node.Health.Lifecycle);
        Assert.Single(node.Log, l => l.Contains("catch-up deadline reached; serving anyway", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reader_past_its_catch_up_deadline_keeps_catching_up_until_current()
    {
        var node = new Node("reader");
        node.Set("orders", ConsumerState.Blocked);
        node.BeginCatchUp();

        node.Clock.Now = Started.AddSeconds(61);
        Assert.Equal((503, "not_ready", "catching_up,projection_blocked"), node.Readyz());
        node.Readyz();
        Assert.Single(node.Log, l => l.Contains("catch-up deadline reached", StringComparison.Ordinal));

        node.Set("orders", ConsumerState.Current);
        Assert.Equal((200, "ready", ""), node.Readyz());
    }

    [Fact]
    public void Boot_completes_only_once()
    {
        var node = new Node("writer");
        node.BeginCatchUp();

        Assert.Throws<InvalidOperationException>(node.BeginCatchUp);
    }

    // --- Over HTTP ---

    [Fact]
    public async Task Readyz_on_the_ops_port_is_503_while_booting_and_200_once_serving_in_the_contracts_shape()
    {
        var node = new Node("writer");
        await using var ops = await OpsServer.StartAsync(node.Health, port: 0, bind: "127.0.0.1");
        using var client = new HttpClient { BaseAddress = ops.Address };

        using (var booting = await client.GetAsync("/readyz"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, booting.StatusCode);
            var body = await booting.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_ready", body.GetProperty("status").GetString());
            Assert.Equal(["starting"], body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        }

        node.BeginCatchUp();
        using var serving = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, serving.StatusCode);
        var ready = await serving.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["status", "role", "node_id", "contract_version", "reasons", "checks"],
            ready.EnumerateObject().Select(p => p.Name));
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal("writer", ready.GetProperty("role").GetString());
        Assert.Equal("0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", ready.GetProperty("node_id").GetString());
        Assert.Equal("1.0", ready.GetProperty("contract_version").GetString());
        Assert.Equal(0, ready.GetProperty("reasons").GetArrayLength());
        var checks = ready.GetProperty("checks");
        Assert.Equal(
            ["write_lag_seconds", "projection_lag_seconds", "dependencies"],
            checks.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, checks.GetProperty("write_lag_seconds").GetDouble());
        Assert.Equal(JsonValueKind.Object, checks.GetProperty("dependencies").ValueKind);
    }

    // --- Settings ---

    [Fact]
    public void Unset_thresholds_take_the_defaults()
    {
        var settings = ReadinessSettings.Parse(null, "");

        Assert.Equal(ConsumerEngine.DefaultLagThreshold, settings.LagThreshold);
        Assert.Equal(ReadinessSettings.DefaultCatchUpDeadline, settings.CatchUpDeadline);
    }

    [Fact]
    public void Thresholds_are_seconds_with_decimals()
    {
        var settings = ReadinessSettings.Parse("0.5", "120");

        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.LagThreshold);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.CatchUpDeadline);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("5s")]
    public void An_invalid_threshold_fails_the_boot(string configured)
    {
        var ex = Assert.Throws<InvalidReadinessSettingException>(() => ReadinessSettings.Parse(configured, null));
        Assert.Contains(ReadinessSettings.LagThresholdVariable, ex.Message);
    }

    private static IReadOnlyDictionary<string, object?> Checks(Node node) =>
        (IReadOnlyDictionary<string, object?>)node.Health.Readyz().Body["checks"]!;
}
