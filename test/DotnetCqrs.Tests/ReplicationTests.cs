using System.Net;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// The writer heartbeat and a reader's replication freshness (health/telemetry contract section 5,
/// <c>STATE-MACHINES.md</c> machine 4 and "Readiness: replication").
/// </summary>
public class ReplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHeartbeats : IHeartbeatStore
    {
        public WriterHeartbeat? Row { get; set; }
        public bool Throws { get; set; }
        public List<(string Node, string Url, DateTimeOffset At)> Written { get; } = [];

        public Task WriteHeartbeatAsync(string writerNodeId, string writerOpsUrl, DateTimeOffset writtenAt, CancellationToken ct = default)
        {
            lock (Written) Written.Add((writerNodeId, writerOpsUrl, writtenAt));
            return Task.CompletedTask;
        }

        public Task<WriterHeartbeat?> ReadHeartbeatAsync(CancellationToken ct = default) =>
            Throws ? throw new InvalidOperationException("store gone") : Task.FromResult(Row);
    }

    /// <summary>Answers the writer's /healthz with <see cref="Status"/>, or refuses when null.</summary>
    private sealed class WriterProbe : HttpMessageHandler
    {
        public HttpStatusCode? Status { get; set; } = HttpStatusCode.OK;
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!);
            return Status is { } status
                ? Task.FromResult(new HttpResponseMessage(status))
                : throw new HttpRequestException("connection refused");
        }
    }

    private static WriterHeartbeat Row(DateTimeOffset writtenAt) =>
        new("writer-1", "http://writer:10056", HeartbeatTimestamp.Format(writtenAt), 7);

    private static (ReplicationMonitor Monitor, FakeHeartbeats Store, WriterProbe Probe) Monitor()
    {
        var store = new FakeHeartbeats();
        var probe = new WriterProbe();
        return (new ReplicationMonitor(store, new HttpClient(probe), TimeSpan.FromSeconds(5), new ManualClock(Now)), store, probe);
    }

    // --- The stores ---

    [Fact]
    public async Task Sqlite_heartbeat_is_one_row_whose_sequence_advances_and_is_never_an_event()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        Assert.Null(await store.ReadHeartbeatAsync());

        await store.WriteHeartbeatAsync("writer-1", "http://writer:10056", Now);
        await store.WriteHeartbeatAsync("writer-1", "http://writer:10056", Now.AddSeconds(1));

        Assert.Equal(new WriterHeartbeat("writer-1", "http://writer:10056", "2026-09-29T12:00:01.000Z", 2), await store.ReadHeartbeatAsync());
        Assert.Empty(await store.PollAsync(0, 10));
        Assert.Equal(0, await store.HeadPositionAsync());
    }

    [Fact]
    public async Task A_read_only_copy_without_the_table_has_no_heartbeat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");
        try
        {
            await using (var create = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await create.OpenAsync();
                await using var command = create.CreateCommand();
                command.CommandText = "CREATE TABLE unrelated (x INTEGER)";
                await command.ExecuteNonQueryAsync();
            }
            await using var copy = await SqliteEventStore.OpenReadOnlyAsync(path);

            Assert.Null(await copy.ReadHeartbeatAsync());
            await Assert.ThrowsAsync<ReadOnlyStoreException>(() => copy.WriteHeartbeatAsync("w", "http://w", Now));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- The writer's loop ---

    [Fact]
    public async Task The_writer_heartbeats_its_node_id_and_ops_url_until_cancelled()
    {
        var store = new FakeHeartbeats();
        using var cts = new CancellationTokenSource();

        var run = WriterHeartbeatLoop.RunAsync(store, "writer-1", "http://writer:10056", TimeSpan.FromMilliseconds(20), ct: cts.Token);
        for (var i = 0; i < 100 && store.Written.Count < 3; i++)
            await Task.Delay(20);
        await cts.CancelAsync();
        await run;

        Assert.True(store.Written.Count >= 3);
        Assert.All(store.Written, w => Assert.Equal(("writer-1", "http://writer:10056"), (w.Node, w.Url)));
    }

    // --- Machine 4 ---

    [Fact]
    public async Task No_heartbeat_row_is_unknown()
    {
        var (monitor, _, _) = Monitor();

        Assert.Equal(ReplicationState.Unknown, monitor.Current.State);
        Assert.Equal(new ReplicationStatus(ReplicationState.Unknown, 0), await monitor.MeasureAsync());
    }

    [Fact]
    public async Task An_unreadable_heartbeat_is_unknown()
    {
        var (monitor, store, _) = Monitor();
        store.Throws = true;

        Assert.Equal(ReplicationState.Unknown, (await monitor.MeasureAsync()).State);
    }

    [Fact]
    public async Task A_heartbeat_within_the_threshold_is_fresh_and_the_writer_is_not_asked()
    {
        var (monitor, store, probe) = Monitor();
        store.Row = Row(Now.AddSeconds(-2.5));

        Assert.Equal(new ReplicationStatus(ReplicationState.Fresh, 2.5), await monitor.MeasureAsync());
        Assert.Empty(probe.Asked);
    }

    [Fact]
    public async Task A_heartbeat_from_the_future_counts_as_age_zero()
    {
        var (monitor, store, _) = Monitor();
        store.Row = Row(Now.AddSeconds(3));

        Assert.Equal(new ReplicationStatus(ReplicationState.Fresh, 0), await monitor.MeasureAsync());
    }

    [Fact]
    public async Task Stale_while_the_writer_answers_is_stale_writer_up()
    {
        var (monitor, store, probe) = Monitor();
        store.Row = Row(Now.AddSeconds(-30));

        Assert.Equal(new ReplicationStatus(ReplicationState.StaleWriterUp, 30), await monitor.MeasureAsync());
        Assert.Equal(new Uri("http://writer:10056/healthz"), Assert.Single(probe.Asked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Stale_while_the_writer_does_not_answer_is_stale_writer_down(HttpStatusCode? answer)
    {
        var (monitor, store, probe) = Monitor();
        store.Row = Row(Now.AddSeconds(-30));
        probe.Status = answer;

        Assert.Equal(ReplicationState.StaleWriterDown, (await monitor.MeasureAsync()).State);
    }

    // --- Readiness: replication ---

    private static NodeHealth Serving(string role, ReplicationStatus? replication)
    {
        var health = new NodeHealth("node-3", Now);
        health.SetIdentity(new NodeIdentity(
            "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77", NodeIdentitySource.Persistent, "timesheets", "node-3",
            NodeIdentity.StackName, role, Now));
        if (replication is not null)
            health.SetReplication(() => replication);
        health.BeginCatchUp(() => [], TimeSpan.FromMinutes(1));
        return health;
    }

    private static (int Code, string Status, string Reasons, object? WriteLag) Readyz(NodeHealth health)
    {
        var (code, body) = health.Readyz();
        var checks = (IReadOnlyDictionary<string, object?>)body["checks"]!;
        return (code, (string)body["status"]!, string.Join(",", (IEnumerable<string>)body["reasons"]!), checks["write_lag_seconds"]);
    }

    [Theory]
    [InlineData(ReplicationState.Fresh, 200, "ready", "")]
    [InlineData(ReplicationState.StaleWriterUp, 503, "not_ready", "replication_stale")]
    [InlineData(ReplicationState.StaleWriterDown, 200, "degraded", "replication_stale")]
    [InlineData(ReplicationState.Unknown, 503, "not_ready", "replication_unknown")]
    public void A_reader_reports_its_replication_state(ReplicationState state, int code, string status, string reasons)
    {
        var health = Serving("reader", new ReplicationStatus(state, 12.5));

        Assert.Equal((code, status, reasons, (object?)12.5), Readyz(health));
    }

    [Fact]
    public void A_reader_with_no_measurement_is_unknown()
    {
        Assert.Equal((503, "not_ready", "replication_unknown", (object?)0.0), Readyz(Serving("reader", null)));
    }

    [Fact]
    public void A_writer_has_no_replication_and_zero_write_lag()
    {
        var health = Serving("writer", new ReplicationStatus(ReplicationState.StaleWriterUp, 99));

        Assert.Equal((200, "ready", "", (object?)0.0), Readyz(health));
    }

    // --- CQRS_OPS_URL ---

    [Fact]
    public void The_ops_url_defaults_to_the_host_and_ops_port() =>
        Assert.Equal("http://node-3:10056", OpsServer.AdvertisedUrl(null, null, "node-3", 10056));

    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1:10056")]
    [InlineData("::1", "http://[::1]:10056")]
    [InlineData("0.0.0.0", "http://node-3:10056")]
    public void The_ops_url_defaults_to_a_specific_bind_address(string bind, string expected) =>
        Assert.Equal(expected, OpsServer.AdvertisedUrl(null, bind, "node-3", 10056));

    [Theory]
    [InlineData("http://writer.internal:9000", "http://writer.internal:9000")]
    [InlineData("https://writer.example/ops/", "https://writer.example/ops")]
    public void A_configured_ops_url_is_used_as_given(string configured, string expected) =>
        Assert.Equal(expected, OpsServer.AdvertisedUrl(configured, null, "node-3", 10056));

    [Theory]
    [InlineData("writer:10056")]
    [InlineData("ftp://writer")]
    [InlineData("/relative")]
    public void An_invalid_ops_url_fails_the_boot(string configured)
    {
        var ex = Assert.Throws<InvalidOpsUrlException>(() => OpsServer.AdvertisedUrl(configured, null, "node-3", 10056));
        Assert.Contains(OpsServer.UrlVariable, ex.Message);
    }

    [Fact]
    public void Heartbeat_interval_and_stale_threshold_default_to_1s_and_5s_and_parse_as_seconds()
    {
        var defaults = ReadinessSettings.Parse(null, null);
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), (defaults.HeartbeatInterval, defaults.StaleThreshold));

        var set = ReadinessSettings.Parse(null, null, "0.5", "10");
        Assert.Equal((TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)), (set.HeartbeatInterval, set.StaleThreshold));

        Assert.Throws<InvalidReadinessSettingException>(() => ReadinessSettings.Parse(null, null, "fast", null));
    }
}
