using System.Net;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// Required dependencies (health/telemetry contract section 4.6, <c>STATE-MACHINES.md</c> machine 3
/// and the "Readiness: event_store" and "Readiness: shared_dependencies" tables).
/// </summary>
public class DependencyTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    // --- Machine 3 ---

    [Fact]
    public void A_first_check_that_fails_is_down_at_once()
    {
        var deps = new DependencyMonitor(failuresToDown: 3);

        deps.Record("kms", ok: false);

        Assert.Equal([("kms", false)], deps.States());
    }

    [Fact]
    public void An_up_dependency_goes_down_only_after_N_consecutive_failures_and_back_up_on_one_success()
    {
        var log = new List<string>();
        var deps = new DependencyMonitor(failuresToDown: 3, log.Add);
        deps.Record("kms", ok: true);

        deps.Record("kms", ok: false);
        deps.Record("kms", ok: false);
        Assert.Equal([("kms", true)], deps.States());

        deps.Record("kms", ok: false);
        Assert.Equal([("kms", false)], deps.States());

        deps.Record("kms", ok: true);
        Assert.Equal([("kms", true)], deps.States());
        Assert.Equal(["dependency kms: down", "dependency kms: up again"], log);
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var deps = new DependencyMonitor(failuresToDown: 2);
        deps.Record("kms", ok: true);
        deps.Record("kms", ok: false);
        deps.Record("kms", ok: true);
        deps.Record("kms", ok: false);

        Assert.Equal([("kms", true)], deps.States());
    }

    [Fact]
    public async Task Checks_that_throw_are_failures_and_the_event_store_is_listed_first()
    {
        var deps = new DependencyMonitor(failuresToDown: 1);
        deps.Add("kms", _ => throw new HttpRequestException("refused"));
        deps.Add(DependencyMonitor.EventStore, _ => Task.CompletedTask);

        await deps.CheckAllAsync();

        Assert.Equal([(DependencyMonitor.EventStore, true), ("kms", false)], deps.States());
    }

    // --- Readiness ---

    private static NodeHealth Serving(string role, params (string Name, bool Up)[] dependencies)
    {
        var health = new NodeHealth("node-3", Started);
        health.SetIdentity(new NodeIdentity(
            "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", NodeIdentitySource.Persistent, "timesheets", "node-3",
            NodeIdentity.StackName, role, Started));
        if (role == "reader")
            health.SetReplication(() => new ReplicationStatus(ReplicationState.Fresh, 0));
        var deps = new DependencyMonitor(failuresToDown: 1);
        foreach (var (name, up) in dependencies)
            deps.Record(name, up);
        health.SetDependencies(deps);
        health.BeginCatchUp(() => [], TimeSpan.FromMinutes(1));
        return health;
    }

    private static (int Code, string Status, string Reasons, string Dependencies) Readyz(NodeHealth health)
    {
        var (code, body) = health.Readyz();
        var checks = (IReadOnlyDictionary<string, object?>)body["checks"]!;
        var deps = (IReadOnlyDictionary<string, string>)checks["dependencies"]!;
        return (code, (string)body["status"]!, string.Join(",", (IEnumerable<string>)body["reasons"]!),
            string.Join(",", deps.Select(d => $"{d.Key}={d.Value}")));
    }

    [Theory]
    [InlineData("writer", 200, "degraded")]
    [InlineData("reader", 503, "not_ready")]
    public void The_event_store_down_is_local_so_not_ready_except_on_the_writer(string role, int code, string status)
    {
        var health = Serving(role, (DependencyMonitor.EventStore, false));

        Assert.Equal((code, status, "event_store_unavailable", "event_store=down"), Readyz(health));
    }

    [Theory]
    [InlineData("writer")]
    [InlineData("reader")]
    public void A_shared_dependency_down_is_degraded_on_any_role(string role)
    {
        var health = Serving(role, (DependencyMonitor.EventStore, true), (DependencyMonitor.Kms, false), (DependencyMonitor.Writer, false));

        Assert.Equal((200, "degraded", "dependency_unavailable", "event_store=up,kms=down,writer=down"), Readyz(health));
    }

    [Fact]
    public void Every_dependency_up_is_ready_and_listed()
    {
        var health = Serving("writer", (DependencyMonitor.EventStore, true), (DependencyMonitor.Kms, true));

        Assert.Equal((200, "ready", "", "event_store=up,kms=up"), Readyz(health));
    }

    [Fact]
    public async Task A_dependency_down_shows_in_metrics_as_zero()
    {
        var health = Serving("writer", (DependencyMonitor.EventStore, true), (DependencyMonitor.Kms, false));

        var metrics = await health.Metrics.RenderAsync(health);

        Assert.Contains("cqrs_dependency_up{dependency=\"event_store\"} 1\n", metrics, StringComparison.Ordinal);
        Assert.Contains("cqrs_dependency_up{dependency=\"kms\"} 0\n", metrics, StringComparison.Ordinal);
    }

    // --- The writer, on a reader ---

    private sealed class OneRow(WriterHeartbeat? row) : IHeartbeatStore
    {
        public Task WriteHeartbeatAsync(string writerNodeId, string writerOpsUrl, DateTimeOffset writtenAt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WriterHeartbeat?> ReadHeartbeatAsync(CancellationToken ct = default) => Task.FromResult(row);
    }

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    [Fact]
    public async Task The_writer_check_passes_when_the_heartbeats_writer_answers()
    {
        var monitor = new ReplicationMonitor(new OneRow(new WriterHeartbeat("w", "http://writer:10056", "2026-09-29T12:00:00.000Z", 1)),
            new HttpClient(new Answer(HttpStatusCode.OK)), TimeSpan.FromSeconds(5));

        await monitor.CheckWriterAsync();
    }

    [Fact]
    public async Task The_writer_check_fails_with_no_heartbeat_or_a_writer_that_does_not_answer()
    {
        var none = new ReplicationMonitor(new OneRow(null), new HttpClient(new Answer(HttpStatusCode.OK)), TimeSpan.FromSeconds(5));
        var down = new ReplicationMonitor(new OneRow(new WriterHeartbeat("w", "http://writer:10056", "2026-09-29T12:00:00.000Z", 1)),
            new HttpClient(new Answer(HttpStatusCode.ServiceUnavailable)), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(() => none.CheckWriterAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => down.CheckWriterAsync());
    }
}
