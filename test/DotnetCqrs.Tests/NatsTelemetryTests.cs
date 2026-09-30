using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotnetCqrs.Host.Telemetry;
using DotnetCqrs.Host.Telemetry.Nats;
using NATS.Client.Core;

namespace DotnetCqrs.Tests;

/// <summary>
/// The NATS binding of the telemetry push against a real <c>nats-server</c>: the subject is
/// <c>cqrs.telemetry.metrics.&lt;node_id&gt;</c>, the payload is the snapshot, and a bus that goes
/// away and comes back costs snapshots, not the node. Runs when <c>DOTNETCQRS_NATS_SERVER</c> names
/// a <c>nats-server</c> binary (build one from github.com/nats-io/nats-server); skipped otherwise,
/// like the Postgres tests without <c>DOTNETCQRS_PG</c>.
/// </summary>
public class NatsTelemetryTests
{
    private const string NodeId = "0192b5c4-7e1a-7c3e-9f00-5b2d8a1c4e77";

    private static string? Server => Environment.GetEnvironmentVariable("DOTNETCQRS_NATS_SERVER") is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class RunningServer : IDisposable
    {
        private Process? _process;
        public int Port { get; }
        public string Url => $"nats://127.0.0.1:{Port}";

        public RunningServer(int? port = null)
        {
            Port = port ?? FreePort();
            Start();
        }

        public void Start()
        {
            _process = Process.Start(new ProcessStartInfo(Server!, ["-a", "127.0.0.1", "-p", Port.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    using var c = new TcpClient();
                    c.Connect(IPAddress.Loopback, Port);
                    return;
                }
                catch (SocketException)
                {
                    Thread.Sleep(50);
                }
            }
            throw new TimeoutException("nats-server did not start");
        }

        public void Stop()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }

        public void Dispose() => Stop();
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(25);
        return condition();
    }

    [SkippableFact(Timeout = 120000)]
    public async Task A_snapshot_arrives_on_the_node_ids_subject_and_survives_a_bus_restart()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER does not name a nats-server binary");
        using var server = new RunningServer();

        // a monitor on the bus, subscribed to every node's snapshots
        await using var monitor = new NatsConnection(NatsOpts.Default with { Url = server.Url });
        var received = new ConcurrentQueue<(string Subject, byte[] Data)>();
        using var stopMonitor = new CancellationTokenSource();
        var listening = new TaskCompletionSource();
        var monitorTask = Task.Run(async () =>
        {
            await using var sub = await monitor.SubscribeCoreAsync<byte[]>("cqrs.telemetry.metrics.>", cancellationToken: stopMonitor.Token);
            listening.SetResult();
            await foreach (var msg in sub.Msgs.ReadAllAsync(stopMonitor.Token))
                received.Enqueue((msg.Subject, msg.Data ?? []));
        });
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var health = TelemetryTests.ServingNode();
        var log = new ConcurrentQueue<string>();
        var transport = NatsTelemetryTransport.Register(new TelemetryTransports()).Create(new Uri(server.Url), log.Enqueue);
        var publisher = new TelemetryPublisher(health, transport, TimeSpan.FromMilliseconds(100), log.Enqueue);
        publisher.Start();

        // 1. it arrives, on the contract's subject, as the contract's payload
        Assert.True(await WaitAsync(() => !received.IsEmpty), "no snapshot arrived: " + string.Join(" | ", log));
        var (subject, data) = received.First();
        Assert.Equal("cqrs.telemetry.metrics." + NodeId, subject);
        var root = JsonDocument.Parse(data).RootElement;
        Assert.Equal("1.0", root.GetProperty("contract_version").GetString());
        Assert.Equal(NodeId, root.GetProperty("node_id").GetString());
        Assert.Equal(11, root.GetProperty("series").EnumerateObject().Count());

        // 2. the bus goes away: snapshots are dropped, and the node neither notices nor changes
        var statusBefore = health.Readyz().Body["status"];
        server.Stop();
        Assert.True(await WaitAsync(() => publisher.Dropped >= 3), "nothing was dropped while the bus was down");
        Assert.Equal(statusBefore, health.Readyz().Body["status"]);
        var droppedWhileDown = publisher.Dropped;

        // 3. the bus comes back on the same address: publishing resumes on its own
        received.Clear();
        server.Start();
        Assert.True(await WaitAsync(() => !received.IsEmpty, 40), "publishing did not resume: " + string.Join(" | ", log));
        Assert.True(publisher.Published > 1);
        Assert.True(droppedWhileDown > 0);

        await publisher.DisposeAsync();
        await stopMonitor.CancelAsync();
        try { await monitorTask; } catch (OperationCanceledException) { }
    }
}
