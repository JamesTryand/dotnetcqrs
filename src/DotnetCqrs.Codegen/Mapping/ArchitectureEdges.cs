using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Model;
using DotnetCqrs.Edges;

namespace DotnetCqrs.Codegen.Mapping;

/// <summary>One edge example's outcome.</summary>
public sealed record EdgeExampleResult(string Edge, string Name, bool Passed, string Detail);

/// <summary>What one system's generation gets from a coordinating document: the edges into it, the events it
/// publishes for edges out of it, and how the edges' examples went.</summary>
public sealed record SystemEdges(
    string Architecture, string System, IReadOnlyList<EdgeIn> In, IReadOnlyList<EdgeOut> Out,
    IReadOnlyList<EdgeExampleResult> Examples);

/// <summary>
/// Reads a coordinating document (eventmodelschema's architecture.schema.json, 0.2.0) for one system, as pocketcqrs's
/// <c>emschema.EdgesFor</c> does: its full check is eventmodelschema's architecture-check.js; this reads only what the
/// system's edges need, refuses what it cannot carry, and runs each edge's examples against
/// <see cref="EdgeIn.Translate"/>, the code a host runs.
/// </summary>
public static class ArchitectureEdges
{
    public static SystemEdges For(string architecturePath, string system, Document document, string documentPath,
        IReadOnlyDictionary<string, string>? aggregateOverrides = null)
    {
        var arch = JsonNode.Parse(File.ReadAllText(architecturePath)) as JsonObject
            ?? throw new InvalidOperationException($"{architecturePath} is not a JSON object");
        if (arch["architectureSchemaVersion"] is null || arch["systems"] is not JsonObject systems)
            throw new InvalidOperationException($"{architecturePath} is not a coordinating document (no architectureSchemaVersion or systems)");
        var archId = (string?)arch["id"] ?? "";
        var report = new MappingReport();
        if (systems[system] is not JsonObject sys)
            throw new InvalidOperationException($"the coordinating document \"{archId}\" has no system \"{system}\"");
        if (sys["document"]?["path"] is not JsonValue pathValue)
            throw new InvalidOperationException($"system \"{system}\" is a placeholder in \"{archId}\", so it has no document to generate");
        var dir = Path.GetDirectoryName(Path.GetFullPath(architecturePath))!;
        var want = Path.GetFullPath(Path.Combine(dir, (string)pathValue!));
        var got = Path.GetFullPath(documentPath);
        if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"system \"{system}\"'s document in \"{archId}\" is {want}, not {got}");

        var edges = (arch["edges"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => (string?)e["kind"] == "internal" && e["from"] is JsonObject && e["to"] is JsonObject).ToList();
        void Refuse(string edge, string message) => report.Error($"edge \"{edge}\": {message}");

        // the events this system publishes: each public event an edge starts from. When its marker lists the fields it
        // publishes (schema 6.2.0), those, and an edge may read only those: the system's own document says what leaves
        // it, so a link in another document can rely on them. Otherwise only the fields some edge from it here maps.
        var outs = new SortedDictionary<(string Event, int Version), SortedSet<string>>();
        void Publish(string edgeId, string evId, EventDef ev, string f, SortedSet<string> set)
        {
            var field = ev.Fields?.FirstOrDefault(x => x.Name == f);
            if (field is null) { Refuse(edgeId, $"event \"{evId}\" has no field \"{f}\""); return; }
            if (field.Pii == true) { Refuse(edgeId, $"\"{f}\" is personal data: carrying a personal field over an edge is not built"); return; }
            set.Add(f);
        }
        foreach (var e in edges.Where(e => (string?)e["from"]!["system"] == system))
        {
            var id = (string)e["id"]!;
            var evId = (string)e["from"]!["event"]!;
            var version = (int)e["from"]!["version"]!;
            if (document.Events?.TryGetValue(evId, out var ev) != true)
            {
                Refuse(id, $"this system has no event \"{evId}\"");
                continue;
            }
            if (ev!.Public?.Version != version)
            {
                Refuse(id, $"event \"{evId}\" is not public at version {version} here");
                continue;
            }
            var listed = ev.Public.Fields;
            if (!outs.TryGetValue((evId, version), out var set))
            {
                set = outs[(evId, version)] = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var f in listed ?? [])
                    Publish(id, evId, ev, f, set);
            }
            foreach (var f in PublishedBy(e))
            {
                if (listed is null) Publish(id, evId, ev, f, set);
                else if (!listed.Contains(f))
                    Refuse(id, $"event \"{evId}\" does not publish \"{f}\": its public marker lists only {string.Join(", ", listed)}");
            }
        }
        var outList = outs.Select(kv => new EdgeOut(Names.TypeName(document.Events![kv.Key.Event].Name, kv.Key.Event),
            system, kv.Key.Event, kv.Key.Version, kv.Value.ToList())).ToList();

        // the edges into this system, and their examples
        var ins = new List<EdgeIn>();
        var examples = new List<EdgeExampleResult>();
        foreach (var e in edges.Where(e => (string?)e["to"]!["system"] == system).OrderBy(e => (string?)e["id"], StringComparer.Ordinal))
        {
            var id = (string)e["id"]!;
            var cmdId = (string)e["to"]!["command"]!;
            if (document.Commands?.TryGetValue(cmdId, out var cmd) != true)
            {
                Refuse(id, $"this system has no command \"{cmdId}\"");
                continue;
            }
            if (cmd!.Public is null) { Refuse(id, $"command \"{cmdId}\" is not public here"); continue; }
            var target = SingleTarget(e["targetId"]);
            if (target is null) { Refuse(id, "a target id made of several fields is not translated yet: name one field"); continue; }
            var aggregate = !string.IsNullOrEmpty(cmd.Aggregate) ? cmd.Aggregate
                : aggregateOverrides?.GetValueOrDefault(cmdId);
            if (string.IsNullOrEmpty(aggregate))
            {
                Refuse(id, $"command \"{cmdId}\" has no aggregate (tag it, or --aggregate-override {cmdId}=<name>)");
                continue;
            }
            var fields = (e["fields"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
            foreach (var cf in fields.Keys)
                if (cmd.Fields?.Any(f => f.Name == cf) != true)
                    Refuse(id, $"command \"{cmdId}\" has no field \"{cf}\"");
            var from = e["from"]!;
            var edge = new EdgeIn(id, (string)from["system"]!, (string)from["event"]!, (int)from["version"]!,
                Names.LowerFirst(Names.SanitizeName(aggregate)), Names.TypeName(cmd.Name, cmdId), target, fields);
            ins.Add(edge);

            var sent = PublishedFields(edges, edge.FromSystem, edge.FromEvent, edge.FromVersion);
            foreach (var ex in (e["examples"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var name = (string?)ex["name"] ?? "";
                var given = ex["given"] as JsonObject ?? [];
                var then = ex["then"] as JsonObject ?? [];
                var message = new EdgeMessage("example", edge.FromSystem, edge.FromEvent, edge.FromVersion, DotnetCqrs.Edges.Edges.Only(given, sent));
                var translated = edge.Translate(message);
                string? detail = null;
                if (translated is null)
                    detail = $"\"{target}\" is empty, so the command has no target and is dropped";
                else if (translated.Id != (string?)then["targetId"])
                    detail = $"the target id is \"{translated.Id}\", not \"{(string?)then["targetId"]}\"";
                else
                {
                    var gotFields = JsonNode.Parse(translated.Command.Payload)!;
                    var wantFields = then["fields"] ?? new JsonObject();
                    if (!JsonNode.DeepEquals(Sorted(gotFields), Sorted(wantFields)))
                        detail = $"the command's fields are {Sorted(gotFields).ToJsonString()}, not {Sorted(wantFields).ToJsonString()}";
                }
                if (detail is not null) Refuse(id, $"example \"{name}\": {detail}");
                examples.Add(new EdgeExampleResult(id, name, detail is null,
                    detail ?? $"{edge.Command} to {edge.Aggregate} {translated!.Id}"));
            }
        }
        if (report.Errors.Count > 0) throw new DocumentMappingException(report);
        return new SystemEdges(archId, system, ins, outList, examples);
    }

    private static IEnumerable<string> PublishedBy(JsonObject e)
    {
        foreach (var kv in e["fields"] as JsonObject ?? [])
            yield return (string)kv.Value!;
        if (SingleTarget(e["targetId"]) is { } t) yield return t;
    }

    private static List<string> PublishedFields(List<JsonObject> edges, string system, string ev, int version) =>
        edges.Where(e => (string?)e["from"]!["system"] == system && (string?)e["from"]!["event"] == ev && (int?)e["from"]!["version"] == version)
            .SelectMany(PublishedBy).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static string? SingleTarget(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s,
        JsonArray { Count: 1 } a when a[0] is JsonValue v1 && v1.TryGetValue<string>(out var s1) => s1,
        _ => null,
    };

    private static JsonNode Sorted(JsonNode node) => node is JsonObject o
        ? new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))
        : node.DeepClone();
}
