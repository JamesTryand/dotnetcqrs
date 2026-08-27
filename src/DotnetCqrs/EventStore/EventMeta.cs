using System.Text.Json;

namespace DotnetCqrs.EventStore;

/// <summary>Reads well-known keys out of an <see cref="Event"/>'s metadata JSON —
/// shared by reactors and extcaller, both of which chain causation/correlation
/// through a reaction.</summary>
public static class EventMeta
{
    /// <summary>Inherits the event's own "correlationId" metadata, defaulting to the
    /// event's own id if absent — it is then the root of the chain.</summary>
    public static string CorrelationId(Event ev)
    {
        if (string.IsNullOrEmpty(ev.Metadata)) return ev.Id;
        try
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Metadata);
            if (meta is not null && meta.TryGetValue("correlationId", out var corr) && corr.ValueKind == JsonValueKind.String)
            {
                var value = corr.GetString();
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch (JsonException)
        {
            // malformed metadata: fall through to the event's own id, same as absent
        }
        return ev.Id;
    }
}
