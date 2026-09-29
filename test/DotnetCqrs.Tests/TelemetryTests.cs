using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Host;
using DotnetCqrs.Host.Telemetry;

namespace DotnetCqrs.Tests;

/// <summary>
/// The optional telemetry push (health/telemetry contract section 8): its settings, the payload,
/// and the publisher's best-effort behaviour against a fake transport. <see cref="NatsTelemetryTests"/>
/// covers the NATS binding against a real server.
/// </summary>
public class TelemetryTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 29, 8, 30, 12, 345, TimeSpan.Zero);
    private const string NodeId = "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77";

    internal static NodeHealth ServingNode(IReadOnlyList<ConsumerStatus>? consumers = null)
    {
        var health = new NodeHealth("node-3", Started);
        health.SetIdentity(new NodeIdentity(NodeId, NodeIdentitySource.Persistent, "timesheets", "node-3",
            NodeIdentity.StackName, "writer", Started));
        health.BeginCatchUp(() => consumers ?? [], TimeSpan.FromMinutes(1));
        return health;
    }

    private sealed class FakeTransport : ITelemetryTransport
    {
        public ConcurrentQueue<(string Key, byte[] Payload)> Sent { get; } = new();
        public volatile bool Fail;
        public volatile bool Hang;
        public bool Disposed { get; private set; }

        public async ValueTask PublishAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
            if (Fail)
                throw new IOException("bus is down");
            Sent.Enqueue((key, payload.ToArray()));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(10);
        return condition();
    }

    // --- Settings ---

    [Fact]
    public void Unset_means_off_with_the_default_interval()
    {
        var s = TelemetrySettings.Parse(null, "");

        Assert.False(s.Enabled);
        Assert.Null(s.Url);
        Assert.Equal(TimeSpan.FromSeconds(15), s.Interval);
    }

    [Fact]
    public void A_url_turns_it_on_and_the_interval_is_seconds_with_decimals()
    {
        var s = TelemetrySettings.Parse("nats://bus.example:4222", "0.5");

        Assert.True(s.Enabled);
        Assert.Equal("nats", s.Url!.Scheme);
        Assert.Equal(TimeSpan.FromMilliseconds(500), s.Interval);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("amqp://")]
    public void An_invalid_url_fails_the_boot_without_echoing_it(string url)
    {
        var ex = Assert.Throws<InvalidTelemetrySettingException>(() => TelemetrySettings.Parse(url, null));

        Assert.Contains(TelemetrySettings.UrlVariable, ex.Message);
        Assert.DoesNotContain(url, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("5s")]
    public void An_invalid_interval_fails_the_boot(string interval)
    {
        var ex = Assert.Throws<InvalidTelemetrySettingException>(() => TelemetrySettings.Parse(null, interval));

        Assert.Contains(TelemetrySettings.IntervalVariable, ex.Message);
    }

    [Fact]
    public void A_scheme_with_no_transport_fails_the_boot_and_never_leaks_credentials()
    {
        var transports = new TelemetryTransports().Register("nats", (_, _) => new FakeTransport());
        var url = new Uri("kafka://user:s3cret@bus.example:9092");

        var ex = Assert.Throws<InvalidTelemetrySettingException>(() => transports.Create(url, _ => { }));

        Assert.Contains("kafka", ex.Message);
        Assert.Contains("nats", ex.Message); // what is available
        Assert.DoesNotContain("s3cret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_scheme_builds_its_transport_case_insensitively()
    {
        var made = new FakeTransport();
        var transports = new TelemetryTransports().Register("nats", (_, _) => made);

        Assert.Same(made, transports.Create(new Uri("NATS://bus:4222"), _ => { }));
    }

    // --- Payload ---

    private static Dictionary<string, JsonElement> Series(byte[] payload) =>
        JsonDocument.Parse(payload).RootElement.GetProperty("series").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());

    [Fact]
    public async Task The_payload_has_the_contracts_envelope_and_every_series()
    {
        var health = ServingNode([new ConsumerStatus("orders", true, ConsumerState.Current, 3, 0, 0)]);
        var snapshot = await health.Metrics.SnapshotAsync(health);

        var bytes = TelemetryPayload.Serialize(snapshot, NodeId, "writer", new DateTimeOffset(2026, 9, 29, 12, 0, 0, 123, TimeSpan.Zero));

        var root = JsonDocument.Parse(bytes).RootElement;
        Assert.Equal(["contract_version", "node_id", "role", "sent_at", "series"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("1.0", root.GetProperty("contract_version").GetString());
        Assert.Equal(NodeId, root.GetProperty("node_id").GetString());
        Assert.Equal("writer", root.GetProperty("role").GetString());
        Assert.Equal("2026-09-29T12:00:00.123Z", root.GetProperty("sent_at").GetString());
        Assert.Equal(
            ["cqrs_node_info", "cqrs_readiness_status", "cqrs_commands_total", "cqrs_command_duration_seconds",
             "cqrs_events_appended_total", "cqrs_write_lag_seconds", "cqrs_projection_lag_seconds", "cqrs_consumer_lag",
             "cqrs_consumer_state", "cqrs_dependency_up", "cqrs_deadletter_depth"],
            Series(bytes).Keys);
    }

    [Fact]
    public async Task The_payload_carries_exactly_the_figures_metrics_renders()
    {
        var health = ServingNode([new ConsumerStatus("orders", true, ConsumerState.Current, 3, 0, 0)]);
        health.Metrics.RecordCommand(200, TimeSpan.FromMilliseconds(3));
        health.Metrics.RecordCommand(200, TimeSpan.FromMilliseconds(80));
        health.Metrics.RecordCommand(409, TimeSpan.FromMilliseconds(2));
        health.Metrics.EventAppended();
        health.Metrics.SetDeadLetterDepth(_ => Task.FromResult(2L)); // known, so numeric in both forms
        var snapshot = await health.Metrics.SnapshotAsync(health);

        var text = NodeMetrics.Render(snapshot);
        var series = Series(TelemetryPayload.Serialize(snapshot, NodeId, "writer", Started));

        // Every plain sample in /metrics is in the JSON with the same labels and value.
        var fromText = new List<(string Name, string Labels, string Value)>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')))
        {
            var space = line.LastIndexOf(' ');
            var head = line[..space];
            var brace = head.IndexOf('{', StringComparison.Ordinal);
            fromText.Add((brace < 0 ? head : head[..brace], brace < 0 ? "" : head[brace..], line[(space + 1)..]));
        }
        foreach (var (name, labels, value) in fromText.Where(s => !s.Name.StartsWith("cqrs_command_duration_seconds", StringComparison.Ordinal)))
        {
            var match = series[name].EnumerateArray().Single(e =>
                "{" + string.Join(",", e.GetProperty("labels").EnumerateObject().Select(l => $"{l.Name}=\"{l.Value.GetString()}\"")) + "}"
                == (labels == "" ? "{}" : labels));
            Assert.Equal(double.Parse(value, CultureInfo.InvariantCulture), match.GetProperty("value").GetDouble());
        }

        // Histograms: cumulative buckets, sum and count for each outcome.
        var accepted = series["cqrs_command_duration_seconds"].EnumerateArray().Single(e => e.GetProperty("labels").GetProperty("status").GetString() == "accepted");
        Assert.Equal(2, accepted.GetProperty("count").GetInt64());
        Assert.Equal(2, accepted.GetProperty("buckets").GetProperty("+Inf").GetInt64());
        Assert.Equal(1, accepted.GetProperty("buckets").GetProperty("0.005").GetInt64()); // only the 3ms one
        Assert.Equal(2, accepted.GetProperty("buckets").GetProperty("0.1").GetInt64());
        Assert.Equal(15, accepted.GetProperty("buckets").EnumerateObject().Count()); // 14 boundaries + +Inf
        Assert.Equal(0.083, accepted.GetProperty("sum").GetDouble(), 3);
        Assert.Equal(5, series["cqrs_command_duration_seconds"].GetArrayLength()); // one per outcome
    }

    [Fact]
    public async Task A_value_that_is_not_known_is_null_not_NaN()
    {
        var health = ServingNode(); // no dead-letter source set: cqrs_deadletter_depth is NaN in /metrics
        var snapshot = await health.Metrics.SnapshotAsync(health);
        Assert.Contains("cqrs_deadletter_depth NaN", NodeMetrics.Render(snapshot));

        var series = Series(TelemetryPayload.Serialize(snapshot, NodeId, "writer", Started));

        Assert.Equal(JsonValueKind.Null, series["cqrs_deadletter_depth"][0].GetProperty("value").ValueKind);
    }

    // --- Publisher ---

    [Fact]
    public async Task It_publishes_at_once_then_on_the_interval_keyed_by_the_node_id()
    {
        var transport = new FakeTransport();
        var publisher = new TelemetryPublisher(ServingNode(), transport, TimeSpan.FromMilliseconds(40));

        publisher.Start();
        Assert.True(await WaitAsync(() => transport.Sent.Count >= 4));
        await publisher.DisposeAsync();

        Assert.All(transport.Sent, s => Assert.Equal(NodeId, s.Key));
        Assert.Equal("ready", JsonDocument.Parse(transport.Sent.First().Payload).RootElement
            .GetProperty("series").GetProperty("cqrs_readiness_status").EnumerateArray()
            .Single(e => e.GetProperty("value").GetInt32() == 1).GetProperty("labels").GetProperty("status").GetString());
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task It_waits_for_an_identity_because_the_key_is_the_node_id()
    {
        var transport = new FakeTransport();
        var health = new NodeHealth("node-3", Started); // booting: no identity yet
        var publisher = new TelemetryPublisher(health, transport, TimeSpan.FromMilliseconds(40));

        publisher.Start();
        await Task.Delay(400);
        Assert.Empty(transport.Sent);

        health.SetIdentity(new NodeIdentity(NodeId, NodeIdentitySource.Persistent, "timesheets", "node-3",
            NodeIdentity.StackName, "writer", Started));

        Assert.True(await WaitAsync(() => !transport.Sent.IsEmpty));
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task A_dead_bus_drops_snapshots_logs_once_and_resumes_without_a_backlog()
    {
        var transport = new FakeTransport { Fail = true };
        var log = new ConcurrentQueue<string>();
        var publisher = new TelemetryPublisher(ServingNode(), transport, TimeSpan.FromMilliseconds(30), log.Enqueue);

        publisher.Start();
        Assert.True(await WaitAsync(() => publisher.Dropped >= 8));
        Assert.Empty(transport.Sent);                                  // never queued
        Assert.Single(log, l => l.Contains("dropped", StringComparison.Ordinal)); // logged once, not per drop

        transport.Fail = false;
        Assert.True(await WaitAsync(() => !transport.Sent.IsEmpty));
        await Task.Delay(200);
        await publisher.DisposeAsync();

        // What arrived after recovery is what the ticks since then produced (about 7), not the 8+ dropped.
        Assert.InRange(transport.Sent.Count, 1, 12);
        Assert.Single(log, l => l.Contains("resumed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hanging_bus_is_dropped_after_the_timeout_and_never_blocks_the_next_snapshot()
    {
        var transport = new FakeTransport { Hang = true };
        var publisher = new TelemetryPublisher(ServingNode(), transport, TimeSpan.FromMilliseconds(30),
            publishTimeout: TimeSpan.FromMilliseconds(80));

        publisher.Start();
        Assert.True(await WaitAsync(() => publisher.Dropped >= 3)); // each attempt gave up after ~80ms
        transport.Hang = false;

        Assert.True(await WaitAsync(() => !transport.Sent.IsEmpty));
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_bus_changes_neither_readiness_nor_liveness()
    {
        var transport = new FakeTransport { Fail = true };
        var health = ServingNode();
        var before = health.Readyz();
        var publisher = new TelemetryPublisher(health, transport, TimeSpan.FromMilliseconds(20));

        publisher.Start();
        Assert.True(await WaitAsync(() => publisher.Dropped >= 5));
        var during = health.Readyz();
        await publisher.DisposeAsync();

        Assert.Equal(before.StatusCode, during.StatusCode);
        Assert.Equal(before.Body["status"], during.Body["status"]);
        Assert.Equal((IEnumerable<string>)before.Body["reasons"]!, (IEnumerable<string>)during.Body["reasons"]!);
        var deps = (IReadOnlyDictionary<string, string>)((IReadOnlyDictionary<string, object?>)during.Body["checks"]!)["dependencies"]!;
        Assert.DoesNotContain(deps.Keys, k => k.Contains("nats", StringComparison.OrdinalIgnoreCase) || k.Contains("telemetry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_draining_begins_one_more_snapshot_goes_out_saying_so_before_stop_returns()
    {
        var transport = new FakeTransport();
        var health = ServingNode();
        // a long interval: only the first and the drain snapshot can arrive during this test
        var publisher = new TelemetryPublisher(health, transport, TimeSpan.FromMinutes(5));
        publisher.Start();
        Assert.True(await WaitAsync(() => transport.Sent.Count == 1));

        health.BeginDraining();
        publisher.NotifyDraining();
        await publisher.StopAsync();

        Assert.Equal(2, transport.Sent.Count);
        var last = JsonDocument.Parse(transport.Sent.Last().Payload).RootElement.GetProperty("series");
        Assert.Equal("not_ready", last.GetProperty("cqrs_readiness_status").EnumerateArray()
            .Single(e => e.GetProperty("value").GetInt32() == 1).GetProperty("labels").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Stopping_does_not_wait_for_the_interval()
    {
        var publisher = new TelemetryPublisher(ServingNode(), new FakeTransport(), TimeSpan.FromMinutes(5));
        publisher.Start();
        await Task.Delay(100);

        var started = DateTime.UtcNow;
        await publisher.DisposeAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
