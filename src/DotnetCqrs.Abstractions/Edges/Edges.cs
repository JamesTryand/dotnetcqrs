using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Edges;

/// <summary>
/// eventmodelschema's architecture schema (0.2.0) <c>edgeMessage</c>: what crosses between two systems, whatever
/// carries it. A public event at its public version, with only the fields some internal edge from it maps. The same
/// shape pocketcqrs's <c>edges.Message</c> sends and reads, so either runtime can be on either end of an edge.
/// </summary>
public sealed record EdgeMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("data")] JsonObject Data,
    [property: JsonPropertyName("correlationId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CorrelationId = null);

/// <summary>One public event this system publishes for its internal edges: its type in this store, its name and
/// public version in the coordinating document, and the only fields that leave.</summary>
public sealed record EdgeOut(string EventType, string System, string Event, int Version, IReadOnlyList<string> Fields);

/// <summary>One internal edge into this system: the source event at its version, the local aggregate and command,
/// the source field the target stream id comes from (rule 2), and each command field's source field (rule 1).</summary>
public sealed record EdgeIn(
    string Id, string FromSystem, string FromEvent, int FromVersion,
    string Aggregate, string Command, string TargetField, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>Whether <paramref name="m"/> is this edge's source event, at its version.</summary>
    public bool Matches(EdgeMessage m) => m.System == FromSystem && m.Event == FromEvent && m.Version == FromVersion;

    /// <summary>The command <paramref name="m"/> becomes on this edge, and the stream it goes to; <c>null</c> when
    /// the message has no value for <see cref="TargetField"/> (the command has nowhere to go, so it is dropped).</summary>
    public EdgeCommand? Translate(EdgeMessage m)
    {
        var id = Edges.TargetId(m.Data[TargetField]);
        if (id is null) return null;
        var payload = new JsonObject();
        foreach (var (commandField, eventField) in Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (m.Data.TryGetPropertyValue(eventField, out var value))
                payload[commandField] = value?.DeepClone();
        return new EdgeCommand(Aggregate, id, new DotnetCqrs.Deciders.Command(Command, payload.ToJsonString()));
    }
}

/// <summary>A translated command and the stream it goes to.</summary>
public sealed record EdgeCommand(string Aggregate, string Id, Command Command);

/// <summary>What a system's edge tables mean. No carrier is named here: a host hands <see cref="Publish"/>'s
/// messages to whatever sends them, and each message it receives to <see cref="Find"/> and
/// <see cref="EdgeIn.Translate"/>. Which carrier, and its subjects and credentials, is surface tooling's.</summary>
public static class Edges
{
    /// <summary>The message <paramref name="ev"/> becomes, or <c>null</c> when no internal edge starts from its
    /// type (it stays in this system).</summary>
    public static EdgeMessage? Publish(IReadOnlyList<EdgeOut> outs, Event ev)
    {
        var o = outs.FirstOrDefault(x => x.EventType == ev.Type);
        if (o is null) return null;
        var data = JsonNode.Parse(ev.Data) as JsonObject
            ?? throw new InvalidOperationException($"event {ev.Id} ({ev.Type}) data is not an object");
        return new EdgeMessage(ev.Id, o.System, o.Event, o.Version, Only(data, o.Fields), Correlation(ev));
    }

    /// <summary>Keeps only the named fields of <paramref name="data"/>: what a sender publishes.</summary>
    public static JsonObject Only(JsonObject data, IEnumerable<string> fields)
    {
        var kept = new JsonObject();
        foreach (var f in fields)
            if (data.TryGetPropertyValue(f, out var v))
                kept[f] = v?.DeepClone();
        return kept;
    }

    /// <summary>The edges in <paramref name="ins"/> whose source is <paramref name="m"/>'s event, by id.</summary>
    public static IReadOnlyList<EdgeIn> Find(IEnumerable<EdgeIn> ins, EdgeMessage m) =>
        ins.Where(e => e.Matches(m)).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    /// <summary>A target field's value as a stream id: a non-empty string, or a number written plainly (as
    /// pocketcqrs writes it); anything else has no target.</summary>
    public static string? TargetId(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s.Length > 0 ? s : null;
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        if (v.TryGetValue<double>(out var d)) return d.ToString("R", CultureInfo.InvariantCulture);
        if (v.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static string Correlation(Event ev)
    {
        try
        {
            if (JsonNode.Parse(ev.Metadata) is JsonObject meta && meta["correlationId"] is JsonValue c &&
                c.TryGetValue<string>(out var corr) && corr.Length > 0)
                return corr;
        }
        catch (JsonException) { }
        return ev.Id;
    }
}
