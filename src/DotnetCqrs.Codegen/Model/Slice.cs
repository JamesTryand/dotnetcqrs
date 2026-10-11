using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>
/// One slice, shaped by <c>pattern</c> per the schema's three conditional shapes
/// (<c>stateChange</c>/<c>stateView</c>/<c>automation</c>) — a discriminated union via
/// <see cref="SliceConverter"/> (see its doc comment for why not the native
/// <c>[JsonPolymorphic]</c> attribute).
/// </summary>
[JsonConverter(typeof(SliceConverter))]
public abstract record Slice(
    string Id,
    string Name,
    string SwimlaneId,
    string? ChapterId,
    string? BusinessCapability,
    string Status,
    IReadOnlyList<Scenario> Scenarios);

public sealed record StateChangeSlice(
    string Id, string Name, string SwimlaneId, string? ChapterId, string? BusinessCapability,
    string Status, IReadOnlyList<Scenario> Scenarios,
    string ScreenId, string CommandId, IReadOnlyList<string> EventIds,
    IReadOnlyList<OutcomeDef>? Outcomes = null)
    : Slice(Id, Name, SwimlaneId, ChapterId, BusinessCapability, Status, Scenarios);

public sealed record StateViewSlice(
    string Id, string Name, string SwimlaneId, string? ChapterId, string? BusinessCapability,
    string Status, IReadOnlyList<Scenario> Scenarios,
    string ScreenId, string ReadModelId)
    : Slice(Id, Name, SwimlaneId, ChapterId, BusinessCapability, Status, Scenarios);

public sealed record AutomationSlice(
    string Id, string Name, string SwimlaneId, string? ChapterId, string? BusinessCapability,
    string Status, IReadOnlyList<Scenario> Scenarios,
    string AutomationId, IReadOnlyList<string> TriggerEventIds, string? ReadModelId,
    string CommandId, IReadOnlyList<string> ResultEventIds,
    IReadOnlyList<OutcomeDef>? Outcomes = null)
    : Slice(Id, Name, SwimlaneId, ChapterId, BusinessCapability, Status, Scenarios);

public sealed class SliceConverter() : DiscriminatedUnionConverter<Slice>("pattern", new Dictionary<string, Type>
{
    ["stateChange"] = typeof(StateChangeSlice),
    ["stateView"] = typeof(StateViewSlice),
    ["automation"] = typeof(AutomationSlice),
});

/// <summary>One alternative of a slice's <c>outcomes</c>: the events emitted together, and
/// (schema 3.12.0) when it applies and the values it fixes on them. 3.3.0's form, a bare list
/// of ids, reads as an alternative with no <c>When</c>. <c>When</c> is kept as raw JSON (the
/// string <c>"hand"</c>, or a list of <c>{ "all": [...] }</c> groups) for the mapper to
/// resolve; <c>Set</c> is event id, then field name, then value.</summary>
[JsonConverter(typeof(OutcomeDefConverter))]
public sealed record OutcomeDef(
    IReadOnlyList<string> EventIds,
    string? Description = null,
    JsonElement? When = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Set = null);

/// <summary>One <c>{ "all": [...] }</c> group of a declared <c>when</c>: it holds when every
/// condition does. The conditions are <c>refuseWhen</c>'s.</summary>
public sealed record OutcomeConditionDef(IReadOnlyList<RefusalConditionDef> All);

/// <summary>Reads an alternative in either form: a list of event ids (3.3.0) or an object
/// (3.12.0).</summary>
public sealed class OutcomeDefConverter : JsonConverter<OutcomeDef>
{
    private sealed record ObjectForm(
        IReadOnlyList<string> EventIds, string? Description, JsonElement? When,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? Set);

    public override OutcomeDef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return new OutcomeDef(JsonSerializer.Deserialize<List<string>>(ref reader, options)!);
        var form = JsonSerializer.Deserialize<ObjectForm>(ref reader, options)!;
        return new OutcomeDef(form.EventIds, form.Description, form.When, form.Set);
    }

    public override void Write(Utf8JsonWriter writer, OutcomeDef value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new ObjectForm(value.EventIds, value.Description, value.When, value.Set), options);
}
