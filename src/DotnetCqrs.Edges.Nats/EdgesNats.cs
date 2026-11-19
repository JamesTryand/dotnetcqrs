using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace DotnetCqrs.Edges.Nats;

/// <summary>
/// The names on the carrier, the same as pocketcqrs-extensions' <c>federation</c> package (<c>subject.go</c>,
/// <c>ingest.go</c>), so a dotnetcqrs system and a pocketcqrs system read and write each other's streams. They are
/// how messages are carried, so they live here and never in the coordinating document or a generator (decision
/// 0033 in the venture-overview hub).
/// </summary>
public static class EdgesNats
{
    /// <summary>The subject an event of <paramref name="system"/> is published on.</summary>
    public static string PublishSubject(string system, string aggregate, string aggregateId) =>
        string.Join('.', "cqrs", system, "events", aggregate, aggregateId);

    /// <summary>Every subject <see cref="PublishSubject"/> can produce for <paramref name="system"/>.</summary>
    public static string SubscribeSubject(string system) => string.Join('.', "cqrs", system, "events") + ".>";

    /// <summary>The JetStream stream a system's edge messages land in.</summary>
    public static string StreamName(string system) => "federation-events-" + system;

    /// <summary>The durable consumer a receiver reads <paramref name="peer"/>'s stream through. It depends only on
    /// the peer, as federation's does: two receivers of one peer share it.</summary>
    public static string ConsumerName(string peer) => "federation-ingest-" + peer;

    /// <summary>The checkpoint name of a system's publishing consumer, as federation's <c>Publisher</c> names it.</summary>
    public static string PublisherName(string system) => "federation-publish:" + system;

    /// <summary>The command id a received message's translation on one edge carries: the same construction as
    /// federation's <c>federatedCommandID(peer, m.ID+"/"+e.ID)</c>, so a redelivery derives the same id and the
    /// decider registry skips it.</summary>
    public static string CommandId(string peer, string messageId, string edgeId)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in new[] { "federation-ingest", peer, messageId + "/" + edgeId })
        {
            h.AppendData(Encoding.UTF8.GetBytes(part));
            h.AppendData([0]);
        }
        return "federation-" + Convert.ToHexStringLower(h.GetHashAndReset().AsSpan(0, 16));
    }

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
}

/// <summary>
/// Publishes a system's edge messages: a consumer of its own log that turns each event an internal edge starts from
/// into an <see cref="EdgeMessage"/> (<see cref="Edges.Publish"/>: only the fields some edge maps) and publishes it
/// to JetStream, waiting for the server's acknowledgement before it returns. A failure throws, so the engine does not
/// move its checkpoint past a message that was not stored; a redelivery is published again under the same
/// <c>Nats-Msg-Id</c> (the event's id), which the stream drops as a duplicate. Events no edge starts from are skipped.
/// </summary>
public sealed class EdgePublisher(INatsJSContext js, string system, IReadOnlyList<EdgeOut> outs) : IConsumer
{
    public string Name => EdgesNats.PublisherName(system);

    /// <summary>Creates (or confirms) the stream this system's messages land in. Call before the engine runs this
    /// consumer; <see cref="EnsureStreamUntilAsync"/> retries while the carrier is unreachable.</summary>
    public async Task EnsureStreamAsync(CancellationToken ct)
    {
        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(EdgesNats.StreamName(system), [EdgesNats.SubscribeSubject(system)]), ct);
    }

    /// <summary><see cref="EnsureStreamAsync"/>, retried every <paramref name="every"/> until it succeeds or
    /// <paramref name="ct"/> ends: a host starts while its carrier is down, and publishes once it is back.</summary>
    public async Task EnsureStreamUntilAsync(TimeSpan every, Action<string>? log, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await EnsureStreamAsync(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"edges: stream {EdgesNats.StreamName(system)} not ready, retrying: {ex.Message}");
                await Task.Delay(every, ct);
            }
        }
    }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (Edges.Publish(outs, ev) is not { } m) return; // no edge starts from it: it stays here
        var data = JsonSerializer.SerializeToUtf8Bytes(m, EdgesNats.Json);
        var headers = new NatsHeaders { ["Nats-Msg-Id"] = ev.Id };
        var ack = await js.PublishAsync(EdgesNats.PublishSubject(system, ev.Aggregate, ev.AggregateId), data,
            headers: headers, cancellationToken: ct);
        if (ack.Error is { } err)
            throw new InvalidOperationException($"edges: publish of event {ev.Id} refused: {err.Code} {err.Description}");
        // ack.Duplicate: stored already (a redelivery after a crash between publish and checkpoint); nothing to do
    }
}
