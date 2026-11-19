using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Deciders;
using DotnetCqrs.Edges;
using DotnetCqrs.Edges.Nats;
using DotnetCqrs.EventStore;
using Json.Schema;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace DotnetCqrs.Tests.Edges;

/// <summary>
/// The NATS carrier for a system's internal edges (<c>DotnetCqrs.Edges.Nats</c>) against a real JetStream
/// <c>nats-server</c>: what leaves is the coordinating document's <c>edgeMessage</c> with only the mapped fields, a
/// message as pocketcqrs-extensions' federation writes it is read, a redelivery is applied once, each outcome is the
/// one federation gives, and a receiver started while the carrier is down receives once it is up. Runs when
/// <c>DOTNETCQRS_NATS_SERVER</c> names a <c>nats-server</c> binary; skipped otherwise, like the telemetry tests.
/// </summary>
public class EdgesNatsTests
{
    private static string? Server => Environment.GetEnvironmentVariable("DOTNETCQRS_NATS_SERVER") is { Length: > 0 } p && File.Exists(p) ? p : null;

    // the landing-pages edge, by hand: personal data's contact-captured v2 starts commerce's lead
    private static readonly EdgeOut Out = new("ContactCaptured", "personal-data", "contact-captured", 2, ["contactId", "siteId", "utmSource"]);

    private static readonly EdgeIn In = new("contact-starts-lead", "personal-data", "contact-captured", 2, "lead", "StartLead", "contactId",
        new Dictionary<string, string> { ["leadId"] = "contactId", ["siteId"] = "siteId", ["utmSource"] = "utmSource" });

    private static Decider<bool> LeadDecider() => new()
    {
        InitialState = () => false,
        Decide = (started, command) => command.Name switch
        {
            "StartLead" when started => throw new InvalidOperationException("lead already exists"),
            "StartLead" => [new NewEvent("LeadStarted", command.Payload)],
            _ => throw new InvalidOperationException($"unknown command {command.Name}"),
        },
        Evolve = (started, ev) => started || ev.Type == "LeadStarted",
    };

    // a decider that appends on every StartLead: only the command id can keep a redelivery from applying twice
    private static Decider<int> CountingLeadDecider() => new()
    {
        InitialState = () => 0,
        Decide = (_, command) => [new NewEvent("LeadStarted", command.Payload)],
        Evolve = (n, _) => n + 1,
    };

    // ready once JetStream itself answers, not just the port: under load the gap is seconds (as federation's natsReady)
    internal static void WaitForJetStream(string url)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var nats = new NatsConnection(new NatsOpts { Url = url });
                try { new NatsJSContext(nats).GetAccountInfoAsync(cts.Token).AsTask().GetAwaiter().GetResult(); return; }
                finally { nats.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception) when (DateTime.UtcNow < until) { Thread.Sleep(100); }
        }
    }

    private sealed class NatsServer : IDisposable
    {
        private Process? _process;
        private readonly string _store = Path.Combine(Path.GetTempPath(), "edges-nats-" + Guid.NewGuid().ToString("N"));
        public int Port { get; } = FreePort();
        public string Url => $"nats://127.0.0.1:{Port}";

        public void Start()
        {
            _process = Process.Start(new ProcessStartInfo(Server!, ["-js", "-sd", _store, "-a", "127.0.0.1", "-p", Port.ToString()])
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
                try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, Port); EdgesNatsTests.WaitForJetStream(Url); return; }
                catch (SocketException) { Thread.Sleep(50); }
            }
            throw new TimeoutException("nats-server did not start");
        }

        public void Dispose()
        {
            if (_process is { HasExited: false }) { _process.Kill(); _process.WaitForExit(5000); }
            try { Directory.Delete(_store, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    private static async Task<Event> CaptureAsync(SqliteEventStore store, string contactId, string? correlationId = null)
    {
        var data = new JsonObject
        {
            ["contactId"] = contactId, ["siteId"] = "s1", ["email"] = "person@example.test", ["utmSource"] = "newsletter",
        }.ToJsonString();
        var meta = correlationId is null ? "{}" : new JsonObject { ["correlationId"] = correlationId }.ToJsonString();
        await store.AppendAsync("contact", contactId, 0, [new NewEvent("ContactCaptured", data, meta)]);
        return (await store.LoadStreamAsync("contact", contactId))[0];
    }

    private static async Task<IReadOnlyList<byte[]>> StoredAsync(INatsJSContext js, string system)
    {
        var stream = await js.GetStreamAsync(EdgesNats.StreamName(system));
        var found = new List<byte[]>();
        for (ulong seq = 1; seq <= stream.Info.State.LastSeq; seq++)
        {
            var m = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq });
            found.Add(m.Message.Data.ToArray());
        }
        return found;
    }

    private static async Task<IReadOnlyList<Event>> EventuallyAsync(SqliteEventStore store, string aggregate, string id, int want)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        IReadOnlyList<Event> stream = [];
        while (DateTime.UtcNow < until)
        {
            stream = await store.LoadStreamAsync(aggregate, id);
            if (stream.Count >= want) return stream;
            await Task.Delay(100);
        }
        return stream;
    }

    [SkippableFact]
    public async Task What_leaves_is_one_edgeMessage_per_event_with_only_the_mapped_fields()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        using var server = new NatsServer();
        server.Start();
        await using var nats = new NatsConnection(new NatsOpts { Url = server.Url });
        var js = new NatsJSContext(nats);
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var publisher = new EdgePublisher(js, "personal-data", [Out]);
        await publisher.EnsureStreamAsync(CancellationToken.None);

        var ev = await CaptureAsync(store, "c-1", "corr-1");
        await publisher.ApplyAsync(ev, CancellationToken.None);
        await publisher.ApplyAsync(ev, CancellationToken.None); // a redelivery after a crash before the checkpoint

        var stored = await StoredAsync(js, "personal-data");
        Assert.Single(stored);
        using var doc = JsonDocument.Parse(stored[0]);
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Edges", "edgeMessage.schema.json")));
        Assert.True(schema.Evaluate(doc.RootElement).IsValid, Encoding.UTF8.GetString(stored[0]));
        Assert.Equal(["id", "system", "event", "version", "data", "correlationId"],
            doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["contactId", "siteId", "utmSource"], doc.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.Equal(ev.Id, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("corr-1", doc.RootElement.GetProperty("correlationId").GetString());
    }

    [SkippableFact]
    public async Task A_message_as_federation_writes_it_starts_the_lead_once_however_often_it_is_delivered()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        using var server = new NatsServer();
        server.Start();
        await using var nats = new NatsConnection(new NatsOpts { Url = server.Url });
        var js = new NatsJSContext(nats);
        await js.CreateOrUpdateStreamAsync(new StreamConfig(EdgesNats.StreamName("personal-data"), [EdgesNats.SubscribeSubject("personal-data")]));
        // exactly the bytes Go's json.Marshal of edges.Message writes (fields in struct order, data keys sorted)
        var goBytes = Encoding.UTF8.GetBytes(
            """{"id":"ev-1","system":"personal-data","event":"contact-captured","version":2,"data":{"contactId":"c-9","siteId":"s1","utmSource":"newsletter"},"correlationId":"corr-9"}""");
        // the same message stored twice, as a publisher that lost its acknowledgement and outlived the dedup window
        // would leave it: the receiver's command id, not the carrier and not the decider (which here accepts a
        // repeat), keeps the lead to one
        await js.PublishAsync(EdgesNats.PublishSubject("personal-data", "contact", "c-9"), goBytes, headers: new NatsHeaders { ["Nats-Msg-Id"] = "a" });
        await js.PublishAsync(EdgesNats.PublishSubject("personal-data", "contact", "c-9"), goBytes, headers: new NatsHeaders { ["Nats-Msg-Id"] = "b" });

        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("lead", CountingLeadDecider());
        using var stop = new CancellationTokenSource();
        var ingest = new EdgeIngest(js, "personal-data", [In], registry, store);
        var run = ingest.RunAsync(stop.Token);

        var lead = await EventuallyAsync(store, "lead", "c-9", 1);
        await Task.Delay(1000); // the second delivery is handled by now
        stop.Cancel();
        await run;

        lead = await store.LoadStreamAsync("lead", "c-9");
        Assert.Single(lead);
        var payload = JsonNode.Parse(lead[0].Data)!;
        Assert.Equal("c-9", (string?)payload["leadId"]);
        Assert.Equal("newsletter", (string?)payload["utmSource"]);
        var meta = JsonNode.Parse(lead[0].Metadata)!;
        Assert.Equal(EdgesNats.CommandId("personal-data", "ev-1", "contact-starts-lead"), (string?)meta["commandId"]);
        Assert.Equal("corr-9", (string?)meta["correlationId"]);
        Assert.Empty(await store.ListDeadLettersAsync());
    }

    [SkippableFact]
    public async Task Each_outcome_is_the_one_federation_gives()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        using var server = new NatsServer();
        server.Start();
        await using var nats = new NatsConnection(new NatsOpts { Url = server.Url });
        var js = new NatsJSContext(nats);
        await js.CreateOrUpdateStreamAsync(new StreamConfig(EdgesNats.StreamName("personal-data"), [EdgesNats.SubscribeSubject("personal-data")]));
        async Task Send(string id, string json) =>
            await js.PublishAsync(EdgesNats.PublishSubject("personal-data", "contact", id), Encoding.UTF8.GetBytes(json),
                headers: new NatsHeaders { ["Nats-Msg-Id"] = id });

        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("lead", LeadDecider());
        await registry.HandleAsync("lead", "c-refused", new Command("StartLead", "{}")); // already started, by hand

        var toNowhere = In with { Id = "to-nowhere", Aggregate = "nowhere" };
        await Send("v1", """{"id":"v1","system":"personal-data","event":"contact-captured","version":1,"data":{"contactId":"c-v1"}}""");
        await Send("refused", """{"id":"refused","system":"personal-data","event":"contact-captured","version":2,"data":{"contactId":"c-refused"}}""");
        await Send("no-target", """{"id":"no-target","system":"personal-data","event":"contact-captured","version":2,"data":{"siteId":"s1"}}""");
        await Send("junk", "not json");
        await Send("ok", """{"id":"ok","system":"personal-data","event":"contact-captured","version":2,"data":{"contactId":"c-ok"}}""");

        using var stop = new CancellationTokenSource();
        var log = new List<string>();
        var ingest = new EdgeIngest(js, "personal-data", [In, toNowhere], registry, store, log: l => { lock (log) log.Add(l); });
        var run = ingest.RunAsync(stop.Token);
        await EventuallyAsync(store, "lead", "c-ok", 1);
        await Task.Delay(1000);
        stop.Cancel();
        await run;

        // a version no edge names: skipped, not a dead letter, no lead
        Assert.Empty(await store.LoadStreamAsync("lead", "c-v1"));
        // a refusal: the domain's answer, acknowledged, nothing more appended
        Assert.Single(await store.LoadStreamAsync("lead", "c-refused"));
        Assert.Single(await store.LoadStreamAsync("lead", "c-ok"));
        var letters = await store.ListDeadLettersAsync();
        // an aggregate this system does not have, once per message on that edge (ok, refused); and the junk message
        Assert.Equal(3, letters.Count);
        Assert.Equal(2, letters.Count(l => l.Error.StartsWith("edge to-nowhere:")));
        Assert.Single(letters, l => l.Error == "not an edge message");
        Assert.All(letters, l => Assert.Equal(EdgesNats.ConsumerName("personal-data"), l.Consumer));
        Assert.Contains(log, l => l.Contains("no-target") && l.Contains("dropped"));
    }

    // The receiving side's retry is what this catches (without it the ingest gives up while the carrier is down).
    // The sending side has two defences, the connection's RetryOnInitialConnect and EnsureStreamUntilAsync's
    // retry, and the NATS client itself waits for its first connection, so removing either or both still passes
    // here (checked 2026-11-19): the property holds, but this test does not pin which defence gives it.
    [SkippableFact]
    public async Task Both_ends_start_while_the_carrier_is_down_and_the_lead_arrives_once_it_is_up()
    {
        Skip.If(Server is null, "DOTNETCQRS_NATS_SERVER is not set");
        using var server = new NatsServer(); // not started yet
        await using var sender = EdgesNatsConnection.Open(server.Url, null, null, "test-sender");
        await using var receiver = EdgesNatsConnection.Open(server.Url, null, null, "test-receiver");
        var senderJs = new NatsJSContext(sender);
        var receiverJs = new NatsJSContext(receiver);
        await using var pdStore = await SqliteEventStore.OpenAsync(":memory:");
        await using var cmStore = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(cmStore);
        registry.Register("lead", LeadDecider());
        using var stop = new CancellationTokenSource();

        var publisher = new EdgePublisher(senderJs, "personal-data", [Out]);
        var ensured = publisher.EnsureStreamUntilAsync(TimeSpan.FromMilliseconds(200), null, stop.Token);
        var run = new EdgeIngest(receiverJs, "personal-data", [In], registry, cmStore).RunAsync(stop.Token, TimeSpan.FromMilliseconds(200));
        await Task.Delay(1000);
        Assert.False(ensured.IsCompleted); // still waiting for the carrier, not failed

        server.Start();
        await ensured;
        var ev = await CaptureAsync(pdStore, "c-late");
        await publisher.ApplyAsync(ev, CancellationToken.None);

        var lead = await EventuallyAsync(cmStore, "lead", "c-late", 1);
        stop.Cancel();
        await run;
        Assert.Single(lead);
    }
}
