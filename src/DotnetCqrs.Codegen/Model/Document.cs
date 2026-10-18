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
    IReadOnlyDictionary<string, Hotspot>? Hotspots,
    DataSubjectsDef? DataSubjects = null);

/// <summary>The document-wide <c>dataSubjects</c> (schema 3.2.0): per-subject rules for the runtime's
/// built-in <c>dataSubject</c> aggregate, which no document declares as an aggregate of its own.</summary>
public sealed record DataSubjectsDef(ErasureAuthorizationDef? Erasure);

/// <summary><c>dataSubjects.erasure</c>: who may REQUEST a subject's erasure (schema 3.7.1). <see cref="Self"/> lets a
/// caller request their own subject id's (resolved through <see cref="Via"/> when given); <see cref="Roles"/> may
/// request any subject's. It never authorises the erasure itself, nor holding or approving a request.</summary>
public sealed record ErasureAuthorizationDef(bool? Self, IReadOnlyList<string>? Roles, CommandOwnershipVia? Via);
