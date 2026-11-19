using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace DotnetCqrs.Edges.Nats;

/// <summary>How an <see cref="EdgeIngest"/> retries a command that could not be applied yet.</summary>
/// <param name="MaxAttempts">Deliveries of one message before it is dead-lettered instead of retried again.</param>
/// <param name="Backoff">The pause JetStream waits before redelivering (a delayed negative acknowledgement; this
/// process never sleeps on it).</param>
/// <param name="Hold">How long a message of a newer version than any edge names waits before it is offered again
/// (30 s when null): it is held until a receiver that names its version reads it.</param>
public sealed record EdgeRetry(int MaxAttempts = 10, TimeSpan? Backoff = null, TimeSpan? Hold = null)
{
    public TimeSpan Delay => Backoff ?? TimeSpan.FromSeconds(2);
    public TimeSpan HoldFor => Hold ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Receives a peer system's edge messages and applies each in process: reads the peer's stream through a durable
/// consumer, finds the internal edges into this system that name the message's event and version
/// (<see cref="Edges.Find"/>), translates it on each (<see cref="EdgeIn.Translate"/>) and sends the command to the
/// local <see cref="DeciderRegistry"/> with a command id derived from the message and the edge, so a redelivery is
/// skipped where it was already applied. Wire-compatible with pocketcqrs-extensions' <c>federation.Ingest</c> with
/// <c>IngestConfig.Edges</c>, and the same outcomes:
/// <list type="bullet">
/// <item>applied, already applied, or refused by the decider (a refusal is the domain's answer, as at-least-once
/// delivery intends): acknowledged;</item>
/// <item>a concurrency conflict, or a failure around the decision (the store unreachable): redelivered after
/// <see cref="EdgeRetry.Delay"/>, and dead-lettered once <see cref="EdgeRetry.MaxAttempts"/> deliveries are used.
/// (federation acknowledges any error other than a conflict as a refusal; dotnetcqrs can tell the two apart, so a
/// failure is retried instead);</item>
/// <item>an aggregate this system does not have, or a message that is not an edge message: dead-lettered and
/// acknowledged;</item>
/// <item>a newer version of an event this system has edges for than any of them names (its sender was deployed
/// first): held, offered again after <see cref="EdgeRetry.HoldFor"/>, until a receiver that names the version applies
/// it; not counted against the retry budget, and only its first delivery logged;</item>
/// <item>an older version than the edges name, or an event no edge names: acknowledged and skipped (nothing here will
/// ever apply it; keep the old version's edge in the coordinating document while a version changes);</item>
/// <item>no value for an edge's target field: that edge's command is dropped, as an automation drops one.</item>
/// </list>
/// </summary>
public sealed class EdgeIngest(
    INatsJSContext js, string peer, IReadOnlyList<EdgeIn> ins, DeciderRegistry registry,
    IDeadLetterStore deadLetters, EdgeRetry? retry = null, Action<string>? log = null)
{
    private readonly EdgeRetry _retry = retry ?? new EdgeRetry();
    private readonly Action<string> _log = log ?? (_ => { });
    // message id -> the delivery at which it was first found applicable and retried, so deliveries spent held are
    // not counted against the retry budget
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _applicable = new();

    public string Name => EdgesNats.ConsumerName(peer);

    /// <summary>Reads the peer's stream until <paramref name="ct"/> ends. While the carrier is unreachable, or the
    /// peer has not created its stream yet, it retries every <paramref name="every"/> (default 2 s): a host starts
    /// with its carrier down and receives once it is back.</summary>
    public async Task RunAsync(CancellationToken ct, TimeSpan? every = null)
    {
        var wait = every ?? TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var consumer = await js.CreateOrUpdateConsumerAsync(EdgesNats.StreamName(peer), new ConsumerConfig(Name)
                {
                    DurableName = Name,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    FilterSubject = EdgesNats.SubscribeSubject(peer),
                }, ct);
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
                    await HandleAsync(msg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log($"edges: receiving from {peer} stopped, retrying: {ex.Message}");
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<byte[]> msg, CancellationToken ct)
    {
        EdgeMessage? m = null;
        try
        {
            m = JsonSerializer.Deserialize<EdgeMessage>(msg.Data ?? [], EdgesNats.Json);
        }
        catch (JsonException) { }
        if (m is null || string.IsNullOrEmpty(m.Id) || m.Data is null)
        {
            var raw = msg.Data is null ? "" : System.Text.Encoding.UTF8.GetString(msg.Data);
            await DeadLetterAsync(Unreadable(raw), "not an edge message", ct);
            await msg.AckAsync(cancellationToken: ct);
            return;
        }

        var delivered = (int)(msg.Metadata?.NumDelivered ?? 1);
        var matched = Edges.Find(ins, m);
        if (matched.Count == 0)
        {
            if (NewerThanNamed(m))
            {
                if (delivered <= 1)
                    _log($"edges: message {m.Id} ({m.System} {m.Event} v{m.Version}) is newer than this receiver names, held until one names it");
                await msg.NakAsync(new AckOpts { NakDelay = _retry.HoldFor }, ct);
                return;
            }
            _log($"edges: message {m.Id} ({m.System} {m.Event} v{m.Version}) no edge names, skipped");
            await msg.AckAsync(cancellationToken: ct);
            return;
        }

        Exception? again = null;
        foreach (var edge in matched)
        {
            if (edge.Translate(m) is not { } c)
            {
                _log($"edges: message {m.Id} has no target on edge {edge.Id}, dropped");
                continue;
            }
            var (outcome, cause) = await DispatchAsync(m, edge, c, ct);
            if (outcome == Outcome.Retry) again = cause;
            else if (outcome == Outcome.DeadLetter) await DeadLetterAsync(AsEvent(m), $"edge {edge.Id}: {cause?.Message}", ct);
        }

        if (again is null)
        {
            _applicable.TryRemove(m.Id, out _);
            await msg.AckAsync(cancellationToken: ct);
            return;
        }
        var first = _applicable.GetOrAdd(m.Id, delivered);
        if (delivered - first + 1 >= _retry.MaxAttempts)
        {
            _applicable.TryRemove(m.Id, out _);
            await DeadLetterAsync(AsEvent(m), $"retry budget exhausted: {again.Message}", ct);
            await msg.AckAsync(cancellationToken: ct);
            return;
        }
        await msg.NakAsync(new AckOpts { NakDelay = _retry.Delay }, ct);
    }

    // a later version of an event the edges name than any of them names
    private bool NewerThanNamed(EdgeMessage m)
    {
        var named = ins.Where(e => e.FromSystem == m.System && e.FromEvent == m.Event).Select(e => e.FromVersion).DefaultIfEmpty(0).Max();
        return named > 0 && m.Version > named;
    }

    private enum Outcome { Done, Retry, DeadLetter }

    private async Task<(Outcome, Exception?)> DispatchAsync(EdgeMessage m, EdgeIn edge, EdgeCommand c, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["actor"] = "reactor:" + Name,
            ["causationId"] = m.Id,
            ["correlationId"] = string.IsNullOrEmpty(m.CorrelationId) ? m.Id : m.CorrelationId,
            ["provenance"] = "federated:" + peer,
            ["commandId"] = EdgesNats.CommandId(peer, m.Id, edge.Id),
        };
        try
        {
            await registry.HandleWithMetaAsync(c.Aggregate, c.Id, c.Command, meta, ct);
            _log($"edges: message {m.Id} on edge {edge.Id} dispatched {c.Command.Name} to {c.Aggregate}/{c.Id}");
            return (Outcome.Done, null);
        }
        catch (UnknownAggregateException ex)
        {
            return (Outcome.DeadLetter, ex);
        }
        catch (ConcurrencyException ex)
        {
            return (Outcome.Retry, ex);
        }
        catch (Exception ex) when (DeciderRegistry.IsRejection(ex))
        {
            _log($"edges: message {m.Id} on edge {edge.Id}: {c.Command.Name} refused: {ex.Message}");
            return (Outcome.Done, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Outcome.Retry, ex);
        }
    }

    private async Task DeadLetterAsync(Event ev, string error, CancellationToken ct)
    {
        _log($"edges: message {ev.Id} dead-lettered: {error}");
        try { await deadLetters.AddDeadLetterAsync(Name, ev, error, ct); }
        catch (Exception ex) { _log($"edges: could not record dead letter for {ev.Id}: {ex.Message}"); }
    }

    // The message as a dead letter's event, as federation records it: its id, event name, data and correlation.
    private static Event AsEvent(EdgeMessage m) => new(
        0, m.Id, "", "", 0, m.Event, m.Data.ToJsonString(),
        new JsonObject { ["correlationId"] = m.CorrelationId, ["system"] = m.System, ["version"] = m.Version }.ToJsonString(), "");

    private static Event Unreadable(string raw) =>
        new(0, "", "", "", 0, "", JsonSerializer.Serialize(raw), "{}", "");
}
