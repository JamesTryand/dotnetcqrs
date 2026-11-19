// The process EdgesNatsKillTests kills part way through a backlog, then restarts on the same files.
//
//   EdgesKillPeer publish <events.db> <nats url>   runs an engine whose only consumer is an EdgePublisher over the
//                                                  log's ContactCaptured events (checkpoint per event, after the
//                                                  server's acknowledgement)
//   EdgesKillPeer ingest <events.db> <nats url>    runs an EdgeIngest into a "lead" aggregate whose decider accepts
//                                                  every StartLead, so only the command id keeps a redelivery from
//                                                  applying twice; dead letters in the same file
//
// EDGES_KILL_BROKEN=1 runs a deliberately broken version instead, for the test to show it fails there: the publisher
// does not wait for the acknowledgement (so the checkpoint can pass a message still on its way), and the ingest
// acknowledges each message before dispatching it (so a kill between the two loses it).
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.Edges;
using DotnetCqrs.Edges.Nats;
using DotnetCqrs.EventStore;
using NATS.Client.Core;
using NATS.Client.JetStream;

var (mode, dbPath, url) = (args[0], args[1], args[2]);
var broken = Environment.GetEnvironmentVariable("EDGES_KILL_BROKEN") == "1";
var store = await SqliteEventStore.OpenAsync(dbPath);
await using var nats = EdgesNatsConnection.Open(url, null, null, "edges-kill-" + mode);
var js = new NatsJSContext(nats);
var outs = new[] { new EdgeOut("ContactCaptured", "personal-data", "contact-captured", 2, ["contactId"]) };
var ins = new[]
{
    new EdgeIn("contact-starts-lead", "personal-data", "contact-captured", 2, "lead", "StartLead", "contactId",
        new Dictionary<string, string> { ["leadId"] = "contactId" }),
};
using var stop = new CancellationTokenSource();

if (mode == "publish")
{
    var publisher = new EdgePublisher(js, "personal-data", outs);
    await publisher.EnsureStreamUntilAsync(TimeSpan.FromMilliseconds(200), Console.Error.WriteLine, stop.Token);
    var engine = new ConsumerEngine(store, store, logger: Console.Error.WriteLine);
    engine.Register(broken ? new NoWaitPublisher(js, outs) : publisher);
    await engine.StartAsync(stop.Token);
    await Task.Delay(Timeout.Infinite);
}
else
{
    var registry = new DeciderRegistry(store);
    registry.Register("lead", new Decider<int>
    {
        InitialState = () => 0,
        Decide = (_, c) => [new NewEvent("LeadStarted", c.Payload)],
        Evolve = (n, _) => n + 1,
    });
    if (broken) await AckFirst(js, ins, registry, stop.Token);
    else await new EdgeIngest(js, "personal-data", ins, registry, store, log: Console.Error.WriteLine).RunAsync(stop.Token);
}
return 0;

// BROKEN: acknowledges a message before applying it.
static async Task AckFirst(INatsJSContext js, IReadOnlyList<EdgeIn> ins, DeciderRegistry registry, CancellationToken ct)
{
    var consumer = await js.CreateOrUpdateConsumerAsync(EdgesNats.StreamName("personal-data"),
        new NATS.Client.JetStream.Models.ConsumerConfig(EdgesNats.ConsumerName("personal-data"))
        {
            DurableName = EdgesNats.ConsumerName("personal-data"),
            AckPolicy = NATS.Client.JetStream.Models.ConsumerConfigAckPolicy.Explicit,
            FilterSubject = EdgesNats.SubscribeSubject("personal-data"),
        }, ct);
    await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
    {
        await msg.AckAsync(cancellationToken: ct);
        await Task.Delay(20, ct); // the apply still to come when the kill lands, as a slow decision would leave it
        var m = JsonSerializer.Deserialize<EdgeMessage>(msg.Data!)!;
        foreach (var e in Edges.Find(ins, m))
            if (e.Translate(m) is { } c)
                await registry.HandleWithMetaAsync(c.Aggregate, c.Id, c.Command,
                    new Dictionary<string, object> { ["commandId"] = EdgesNats.CommandId("personal-data", m.Id, e.Id) }, ct);
    }
}

// BROKEN: publishes without waiting for the server to store the message.
sealed class NoWaitPublisher(INatsJSContext js, IReadOnlyList<EdgeOut> outs) : IConsumer
{
    public string Name => EdgesNats.PublisherName("personal-data");

    public Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (Edges.Publish(outs, ev) is not { } m) return Task.CompletedTask;
        var data = JsonSerializer.SerializeToUtf8Bytes(m);
        // still on its way when the checkpoint moves (here held back 50 ms, as a slow link or a busy server would)
        _ = Task.Delay(50).ContinueWith(_ => js.Connection.PublishAsync(
            EdgesNats.PublishSubject("personal-data", ev.Aggregate, ev.AggregateId), data,
            headers: new NatsHeaders { ["Nats-Msg-Id"] = ev.Id }).AsTask()).Unwrap();
        return Task.CompletedTask;
    }
}
