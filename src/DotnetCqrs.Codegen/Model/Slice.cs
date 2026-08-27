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
    string ScreenId, string CommandId, IReadOnlyList<string> EventIds)
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
    string CommandId, IReadOnlyList<string> ResultEventIds)
    : Slice(Id, Name, SwimlaneId, ChapterId, BusinessCapability, Status, Scenarios);

public sealed class SliceConverter() : DiscriminatedUnionConverter<Slice>("pattern", new Dictionary<string, Type>
{
    ["stateChange"] = typeof(StateChangeSlice),
    ["stateView"] = typeof(StateViewSlice),
    ["automation"] = typeof(AutomationSlice),
});
