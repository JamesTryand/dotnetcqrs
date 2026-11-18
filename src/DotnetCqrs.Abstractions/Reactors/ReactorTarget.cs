using System.Text.Json;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Reactors;

/// <summary>Where a generated reaction goes (eventmodelschema 5.0.0 <c>targetIdField</c>).</summary>
public static class ReactorTarget
{
    /// <summary>The value of the event's field that names the target stream, or <c>""</c> when the event has no
    /// such non-empty string, which <see cref="ReactorConsumer"/> drops and logs.</summary>
    public static string Id(Event ev, string field)
    {
        try
        {
            using var data = JsonDocument.Parse(ev.Data);
            return data.RootElement.ValueKind == JsonValueKind.Object
                   && data.RootElement.TryGetProperty(field, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
