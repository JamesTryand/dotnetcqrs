namespace DotnetCqrs.Codegen.Model;

/// <summary>A parsed, schema-valid EventModeling document — one swimlane-organized
/// model of events/commands/read models/screens/automations and the slices that wire
/// them together, per eventmodeling.org's methodology.</summary>
public sealed record Document(
    string EventModelingSchemaVersion,
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<Swimlane> Swimlanes,
    IReadOnlyDictionary<string, Chapter>? Chapters,
    IReadOnlyDictionary<string, ActorLane>? ActorLanes,
    IReadOnlyDictionary<string, EventDef>? Events,
    IReadOnlyDictionary<string, CommandDef>? Commands,
    IReadOnlyDictionary<string, ReadModelDef>? ReadModels,
    IReadOnlyDictionary<string, ScreenDef>? Screens,
    IReadOnlyDictionary<string, AutomationDef>? Automations,
    IReadOnlyList<Slice> Slices,
    IReadOnlyDictionary<string, Hotspot>? Hotspots);
