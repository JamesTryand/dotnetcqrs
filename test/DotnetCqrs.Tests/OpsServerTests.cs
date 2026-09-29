using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// The ops port and <c>GET /healthz</c> (health/telemetry contract sections 2 and 3,
/// <c>platform/cqrs-runtime-contract/contracts/health-telemetry.md</c>). A real Kestrel listener on
/// a free port, driven over real HTTP.
/// </summary>
public class OpsServerTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 8, 30, 12, 345, TimeSpan.FromHours(1));

    private static NodeIdentity Identity() => new(
        "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", NodeIdentitySource.Persistent, "timesheets", "node-3",
        NodeIdentity.StackName, "writer", Started);

    private static async Task<JsonElement> HealthzAsync(OpsServer ops)
    {
        using var client = new HttpClient { BaseAddress = ops.Address };
        using var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task While_booting_healthz_answers_alive_with_the_unresolved_fields_null()
    {
        var health = new NodeHealth("node-3", Started);
        await using var ops = await OpsServer.StartAsync(health, port: 0);

        var body = await HealthzAsync(ops);

        Assert.Equal("alive", body.GetProperty("status").GetString());
        Assert.Equal("1.0", body.GetProperty("contract_version").GetString());
        foreach (var field in new[] { "node_id", "identity", "instance", "role" })
            Assert.Equal(JsonValueKind.Null, body.GetProperty(field).ValueKind);
        // Known from process start, so present even while booting.
        Assert.Equal("node-3", body.GetProperty("host").GetString());
        Assert.Equal("dotnetcqrs", body.GetProperty("stack").GetString());
        Assert.Equal("2026-09-29T07:30:12.345Z", body.GetProperty("started_at").GetString());
    }

    [Fact]
    public async Task Once_identity_is_resolved_every_field_is_set_in_the_contracts_order()
    {
        var health = new NodeHealth("node-3", Started);
        await using var ops = await OpsServer.StartAsync(health, port: 0);

        health.SetIdentity(Identity());
        var body = await HealthzAsync(ops);

        Assert.Equal(
            ["status", "contract_version", "node_id", "identity", "instance", "host", "stack", "role", "started_at"],
            body.EnumerateObject().Select(p => p.Name));
        Assert.All(body.EnumerateObject(), p => Assert.NotEqual(JsonValueKind.Null, p.Value.ValueKind));
        Assert.Equal("0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", body.GetProperty("node_id").GetString());
        Assert.Equal("persistent", body.GetProperty("identity").GetString());
        Assert.Equal("timesheets", body.GetProperty("instance").GetString());
        Assert.Equal("writer", body.GetProperty("role").GetString());
    }

    [Fact]
    public void For_this_process_host_and_started_at_are_known_at_once()
    {
        var health = NodeHealth.ForThisProcess();

        Assert.False(string.IsNullOrEmpty(health.Host));
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$"), (string)health.HealthzBody()["started_at"]!);
        Assert.Equal(NodeLifecycleState.Booting, health.Lifecycle);
    }

    [Theory]
    [InlineData(null, OpsServer.DefaultPort)]
    [InlineData("", OpsServer.DefaultPort)]
    [InlineData("8080", 8080)]
    [InlineData("0", 0)]
    [InlineData("65535", 65535)]
    public void The_port_is_CQRS_OPS_PORT_else_the_default(string? configured, int expected) =>
        Assert.Equal(expected, OpsServer.ParsePort(configured));

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData(" 80")]
    public void An_invalid_port_fails_the_boot(string configured)
    {
        var ex = Assert.Throws<InvalidOpsPortException>(() => OpsServer.ParsePort(configured));
        Assert.Contains(OpsServer.PortVariable, ex.Message);
    }

    [Fact]
    public async Task A_port_that_is_already_taken_fails_the_boot()
    {
        await using var first = await OpsServer.StartAsync(new NodeHealth("a", Started), port: 0);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            OpsServer.StartAsync(new NodeHealth("b", Started), first.Address.Port));
    }
}
