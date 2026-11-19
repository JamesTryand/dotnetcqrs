using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DotnetCqrs.Edges.Nats;
using DotnetCqrs.EventStore;
using Microsoft.Data.Sqlite;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace DotnetCqrs.Tests.Edges;

/// <summary>
/// The fault-injection floor (decision 0003 in the venture-overview hub) for the NATS carrier of a system's edges:
/// the publisher and the ingest are each a real process (<c>test/EdgesKillPeer</c>), killed part way through a
/// backlog of 600 edge messages and restarted on the same files, as pocketcqrs-extensions' federation kill test does.
/// <list type="bullet">
/// <item>The publisher waits for the server's acknowledgement before its checkpoint moves, so a kill between the two
/// publishes a message again; the <c>Nats-Msg-Id</c> (the event's id) must keep the stream at one message per event,
/// and none may be missing.</item>
/// <item>The ingest applies each message and then acknowledges it, so a kill between the two redelivers it; the
/// command id must keep every lead to exactly one event (the decider here accepts a repeat), none missing, nothing
/// dead-lettered.</item>
/// </list>
/// Each kill must land part way through, so a badly timed run fails instead of passing without testing anything; and
/// the same rounds against a deliberately broken publisher (no wait for the acknowledgement) and ingest (acknowledge
/// first) must fail. Runs when <c>DOTNETCQRS_NATS_SERVER</c> names a <c>nats-server</c> binary.
/// </summary>
[Trait("Category", "Slow")]
public class EdgesNatsKillTests
{
    private const int Backlog = 600;
    private const int Rounds = 5;

    private static string? Server => Environment.GetEnvironmentVariable("DOTNETCQRS_NATS_SERVER") is { Length: > 0 } p && File.Exists(p) ? p : null;

    // test/DotnetCqrs.Tests/bin/<config>/net10.0 -> test/EdgesKillPeer/bin/<config>/net10.0/EdgesKillPeer.dll
    private static string Peer
    {
        get
        {
            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var config = tfm.Parent!.Name;
            return Path.Combine(tfm.Parent.Parent!.Parent!.Parent!.FullName, "EdgesKillPeer", "bin", config, tfm.Name, "EdgesKillPeer.dll");
        }
    }

    [SkippableFact(Timeout = 900000)]
    public async Task The_publisher_survives_a_real_process_kill()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        Assert.Null(await PublisherRoundsAsync(broken: false));
    }

    [SkippableFact(Timeout = 900000)]
    public async Task A_publisher_that_does_not_wait_for_the_acknowledgement_is_caught()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        Assert.NotNull(await PublisherRoundsAsync(broken: true));
    }

    [SkippableFact(Timeout = 900000)]
    public async Task The_ingest_survives_a_real_process_kill()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        Assert.Null(await IngestRoundsAsync(broken: false));
    }

    [SkippableFact(Timeout = 900000)]
    public async Task An_ingest_that_acknowledges_before_it_applies_is_caught()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        Assert.NotNull(await IngestRoundsAsync(broken: true));
    }

    // null when every round passed; otherwise what failed
    private static async Task<string?> PublisherRoundsAsync(bool broken)
    {
        var rng = new Random(5);
        for (var round = 0; round < Rounds; round++)
        {
            using var dir = new TempDir();
            using var server = new NatsServer(dir.Path);
            await WriteBacklogAsync(Path.Combine(dir.Path, "pd.db"));
            await using var nats = new NatsConnection(new NatsOpts { Url = server.Url });
            var js = new NatsJSContext(nats);
            async Task<int> Stored()
            {
                try { return (int)(await js.GetStreamAsync(EdgesNats.StreamName("personal-data"))).Info.State.Messages; }
                catch (NatsJSApiException) { return 0; }
            }

            using (var p = PeerProcess.Start("publish", Path.Combine(dir.Path, "pd.db"), server.Url, broken))
            {
                var atKill = await PartWayAsync(rng, Stored, p);
                p.Kill();
                if (atKill >= Backlog) return $"round {round}: the publisher was killed after it had finished";
            }
            using (PeerProcess.Start("publish", Path.Combine(dir.Path, "pd.db"), server.Url, broken))
            {
                if (await SettleAsync(Stored, Backlog, TimeSpan.FromSeconds(10)) is { } stalled)
                    return $"round {round}: the stream stopped at {stalled} of {Backlog}";
                await Task.Delay(500);
                if (await Stored() is var n && n != Backlog)
                    return $"round {round}: the stream holds {n} messages for {Backlog} events";
            }
        }
        return null;
    }

    private static async Task<string?> IngestRoundsAsync(bool broken)
    {
        var rng = new Random(7);
        for (var round = 0; round < Rounds; round++)
        {
            using var dir = new TempDir();
            using var server = new NatsServer(dir.Path);
            var pd = Path.Combine(dir.Path, "pd.db");
            var cm = Path.Combine(dir.Path, "cm.db");
            await WriteBacklogAsync(pd);
            await using (var source = await SqliteEventStore.OpenAsync(pd))
            await using (var nats = new NatsConnection(new NatsOpts { Url = server.Url }))
            {
                // the backlog on the stream first, by the real publisher, in process
                var js = new NatsJSContext(nats);
                var publisher = new EdgePublisher(js, "personal-data",
                    [new DotnetCqrs.Edges.EdgeOut("ContactCaptured", "personal-data", "contact-captured", 2, ["contactId"])]);
                await publisher.EnsureStreamAsync(CancellationToken.None);
                foreach (var ev in await source.PollAsync(0, Backlog + 10))
                    await publisher.ApplyAsync(ev, CancellationToken.None);
            }
            await using (var target = await SqliteEventStore.OpenAsync(cm)) { } // the file, before two processes read it
            Task<int> Leads() => Task.FromResult(CountLeads(cm).Distinct);

            using (var p = PeerProcess.Start("ingest", cm, server.Url, broken))
            {
                var atKill = await PartWayAsync(rng, Leads, p);
                p.Kill();
                if (atKill >= Backlog) return $"round {round}: the ingest was killed after it had finished";
            }
            using (PeerProcess.Start("ingest", cm, server.Url, broken))
            {
                // a message fetched but not acknowledged before the kill comes back after JetStream's ack wait (30 s
                // by default): a delay, not a loss, so the stall allowance is longer
                if (await SettleAsync(Leads, Backlog, TimeSpan.FromSeconds(45)) is { } stalled)
                    return $"round {round}: the ingest stopped at {stalled} of {Backlog} leads";
                await Task.Delay(1000);
            }
            var (distinct, total) = CountLeads(cm);
            if (total != distinct) return $"round {round}: {total} lead events for {distinct} leads: a redelivery applied twice";
            await using var check = await SqliteEventStore.OpenAsync(cm);
            if ((await check.ListDeadLettersAsync()).Count is var letters && letters > 0) return $"round {round}: {letters} dead letters";
        }
        return null;
    }

    private static async Task WriteBacklogAsync(string path)
    {
        await using var store = await SqliteEventStore.OpenAsync(path);
        for (var i = 0; i < Backlog; i++)
            await store.AppendAsync("contact", $"c{i}", 0,
                [new NewEvent("ContactCaptured", new JsonObject { ["contactId"] = $"c{i}", ["email"] = $"p{i}@example.test" }.ToJsonString())]);
    }

    private static (int Distinct, int Total) CountLeads(string path)
    {
        if (!File.Exists(path)) return (0, 0);
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT aggregate_id), COUNT(*) FROM events WHERE aggregate = 'lead'";
        try
        {
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt32(1));
        }
        catch (SqliteException) { return (0, 0); } // the table not made yet, or busy: no progress this look
    }

    // waits for progress to start, lets it run a random moment, and says how far it had got just before the kill
    private static async Task<int> PartWayAsync(Random rng, Func<Task<int>> progress, PeerProcess p)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (await progress() == 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"never started:\n{p.Tail()}");
            await Task.Delay(5);
        }
        await Task.Delay(rng.Next(60));
        return await progress();
    }

    // null once progress reaches want; otherwise where it stalled for longer than stall
    private static async Task<int?> SettleAsync(Func<Task<int>> progress, int want, TimeSpan stall)
    {
        int last = -1;
        var since = DateTime.UtcNow;
        while (true)
        {
            var got = await progress();
            if (got >= want) return null;
            if (got != last) (last, since) = (got, DateTime.UtcNow);
            else if (DateTime.UtcNow - since > stall) return got;
            await Task.Delay(100);
        }
    }

    private sealed class PeerProcess : IDisposable
    {
        private readonly Process _p;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();

        private PeerProcess(Process p) => _p = p;

        public static PeerProcess Start(string mode, string db, string url, bool broken)
        {
            var info = new ProcessStartInfo("dotnet", [Peer, mode, db, url])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            info.Environment["EDGES_KILL_BROKEN"] = broken ? "1" : "";
            var pp = new PeerProcess(Process.Start(info)!);
            pp._p.OutputDataReceived += (_, e) => { if (e.Data is { } l) pp._log.Enqueue(l); };
            pp._p.ErrorDataReceived += (_, e) => { if (e.Data is { } l) pp._log.Enqueue(l); };
            pp._p.BeginOutputReadLine();
            pp._p.BeginErrorReadLine();
            return pp;
        }

        public string Tail() => string.Join('\n', _log.TakeLast(20));

        public void Kill()
        {
            if (!_p.HasExited) { _p.Kill(entireProcessTree: true); _p.WaitForExit(10000); }
        }

        public void Dispose() { Kill(); _p.Dispose(); }
    }

    private sealed class NatsServer : IDisposable
    {
        private readonly Process _process;
        public string Url { get; }

        public NatsServer(string dir)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"nats://127.0.0.1:{port}";
            _process = Process.Start(new ProcessStartInfo(Server!, ["-js", "-sd", Path.Combine(dir, "js"), "-a", "127.0.0.1", "-p", port.ToString()])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); EdgesNatsTests.WaitForJetStream(Url); return; }
                catch (SocketException) { Thread.Sleep(50); }
            }
            throw new TimeoutException("nats-server did not start");
        }

        public void Dispose()
        {
            if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(5000); }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "edges-kill-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
