using System.Net.Http.Json;
using System.Text.Json;
using DotnetCqrs.Host;
using DotnetCqrs.Postgres;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>
/// The reader side of the health/telemetry contract (<c>STATE-MACHINES.md</c>, machine 4) against
/// real parts, where <see cref="ReplicationTests"/> uses fakes: the writer's heartbeat loop upserts
/// into a real Postgres event store, the reader measures it through its own connection to that
/// store, and the writer's <c>/healthz</c> and the reader's <c>/readyz</c> are real ops servers over
/// HTTP. Each state is reached the way it happens in production: no heartbeat yet (Unknown), the
/// writer beating (Fresh), the heartbeat stopping while the writer answers (StaleWriterUp), and the
/// writer gone (StaleWriterDown).
///
/// <para>Not a reader <i>node</i>: no dotnetcqrs host runs as a reader yet (the generated host is
/// always the writer), so this assembles the reader's parts the way such a host would.</para>
/// </summary>
[Collection("postgres")]
public class PostgresReaderSideTests(PostgresFixture fx)
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(200);

    private static NodeHealth Serving(string role)
    {
        var health = new NodeHealth("127.0.0.1", DateTimeOffset.UtcNow);
        health.SetIdentity(new NodeIdentity(
            Guid.CreateVersion7().ToString(), NodeIdentitySource.Ephemeral, "reader-side-test", "127.0.0.1",
            NodeIdentity.StackName, role, DateTimeOffset.UtcNow));
        health.BeginCatchUp(() => [], TimeSpan.FromMinutes(1));
        return health;
    }

    private sealed record Readiness(int Code, string Status, string[] Reasons, string? Writer, string Raw)
    {
        public override string ToString() => $"{Code} {Status} ({string.Join(",", Reasons)}), writer {Writer ?? "-"}: {Raw}";
    }

    private static async Task<Readiness> ReadyzAsync(HttpClient client, Uri ops)
    {
        using var response = await client.GetAsync(new Uri(ops, "/readyz"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reasons = body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToArray();
        string? writer = null;
        if (body.GetProperty("checks").GetProperty("dependencies") is { ValueKind: JsonValueKind.Object } deps
            && deps.TryGetProperty(DependencyMonitor.Writer, out var w))
            writer = w.GetString();
        return new Readiness((int)response.StatusCode, body.GetProperty("status").GetString()!, reasons, writer, body.GetRawText());
    }

    private static async Task<Readiness> EventuallyAsync(HttpClient client, Uri ops, string what, Func<Readiness, bool> cond)
    {
        Readiness? last = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadyzAsync(client, ops);
            if (cond(last))
                return last;
            await Task.Delay(100);
        }
        Assert.Fail($"timed out waiting for {what}; last /readyz: {last}");
        return last!;
    }

    [SkippableFact]
    public async Task A_reader_reports_every_replication_state_from_a_real_writer_heartbeat()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var connectionString = await fx.NewSchemaAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var stop = new CancellationTokenSource();

        // ---- the writer: its own store connection and a real ops server
        await using var writerStore = await PostgresEventStore.OpenAsync(connectionString);
        var writerHealth = Serving("writer");
        OpsServer? writerOps = await OpsServer.StartAsync(writerHealth, 0, "127.0.0.1");
        var writerOpsUrl = writerOps.Address.ToString().TrimEnd('/');

        // ---- the reader: a separate connection to the same store, measuring the writer's row
        await using var readerStore = await PostgresEventStore.OpenAsync(connectionString);
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var monitor = new ReplicationMonitor(readerStore, probe, StaleThreshold);
        var dependencies = new DependencyMonitor(failuresToDown: 1);
        dependencies.Add(DependencyMonitor.Writer, monitor.CheckWriterAsync);
        var readerHealth = Serving("reader");
        readerHealth.SetReplication(() => monitor.Current);
        readerHealth.SetDependencies(dependencies);
        await using var readerOps = await OpsServer.StartAsync(readerHealth, 0, "127.0.0.1");
        var measuring = monitor.RunAsync(Beat, stop.Token);
        var checking = dependencies.RunAsync(Beat, stop.Token);

        try
        {
            // ---- Unknown: no heartbeat row yet
            await EventuallyAsync(client, readerOps.Address, "Unknown before the writer's first beat",
                r => r is { Code: 503, Status: "not_ready" } && r.Reasons.Contains("replication_unknown"));

            // ---- Fresh: the writer beats
            using var beating = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var heartbeat = WriterHeartbeatLoop.RunAsync(writerStore, writerHealth.Identity!.NodeId, writerOpsUrl, Beat, ct: beating.Token);
            await EventuallyAsync(client, readerOps.Address, "Fresh on a beating writer, the writer dependency up",
                r => r is { Code: 200, Status: "ready", Writer: "up" } && r.Reasons.Length == 0);

            // ---- StaleWriterUp: the heartbeat stops, the writer still answers
            await beating.CancelAsync();
            await heartbeat;
            // the writer dependency stays up: stale is this reader's problem, not a shared one
            await EventuallyAsync(client, readerOps.Address, "StaleWriterUp with the writer answering",
                r => r is { Code: 503, Status: "not_ready", Writer: "up" } && r.Reasons.SequenceEqual(["replication_stale"]));

            // ---- Fresh again once it beats again
            using var beatingAgain = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var heartbeatAgain = WriterHeartbeatLoop.RunAsync(writerStore, writerHealth.Identity!.NodeId, writerOpsUrl, Beat, ct: beatingAgain.Token);
            await EventuallyAsync(client, readerOps.Address, "Fresh again when the writer beats again",
                r => r is { Code: 200, Status: "ready", Writer: "up" } && r.Reasons.Length == 0);

            // ---- StaleWriterDown: the writer goes away entirely
            await beatingAgain.CancelAsync();
            await heartbeatAgain;
            await writerOps.DisposeAsync();
            writerOps = null;
            // a shared cause: the reader stays in the pool as degraded, and the writer dependency
            // is down with it
            await EventuallyAsync(client, readerOps.Address, "StaleWriterDown with the writer gone",
                r => r is { Code: 200, Status: "degraded", Writer: "down" }
                    && r.Reasons.SequenceEqual(["replication_stale", "dependency_unavailable"]));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(measuring, checking);
            if (writerOps is not null)
                await writerOps.DisposeAsync();
        }
    }
}
