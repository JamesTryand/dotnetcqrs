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
    DataSubjectsDef? DataSubjects = null,
    // schema 6.0.0: messages entering (an ingress names one; how it is carried is a source's) and leaving with
    // nothing coming back. Nothing is generated from either.
    IReadOnlyDictionary<string, IngressDef>? Ingresses = null,
    IReadOnlyDictionary<string, OutputDef>? Outputs = null,
    // schema 6.2.0: what this system relies on from others, by each one's document id. Nothing is generated from it:
    // an expected event is never this system's own, and nothing here can append one.
    IReadOnlyDictionary<string, ExpectationDef>? Expects = null);

/// <summary>Schema 6.2.0 <c>expects</c>: what one system relies on from another, as written against that system's
/// release <see cref="WrittenAgainst"/>: the public events it uses, each with only the fields it reads.</summary>
public sealed record ExpectationDef(string WrittenAgainst, string? Description, IReadOnlyList<ExpectedEventDef> Events);

/// <summary>One public event an expectation relies on, at its public version.</summary>
public sealed record ExpectedEventDef(string Event, int Version, string? Description, IReadOnlyList<ExpectedFieldDef> Fields);

/// <summary>One field relied on: its name and type, and its cardinality where it matters.</summary>
public sealed record ExpectedFieldDef(string Name, string Type, string? Cardinality = null, string? Description = null);

/// <summary>The document-wide <c>dataSubjects</c> (schema 3.2.0): per-subject rules for the runtime's
/// built-in <c>dataSubject</c> aggregate, which no document declares as an aggregate of its own.</summary>
public sealed record DataSubjectsDef(ErasureAuthorizationDef? Erasure);

/// <summary><c>dataSubjects.erasure</c>: who may REQUEST a subject's erasure (schema 3.7.1). <see cref="Self"/> lets a
/// caller request their own subject id's (resolved through <see cref="Via"/> when given); <see cref="Roles"/> may
/// request any subject's. It never authorises the erasure itself, nor holding or approving a request.</summary>
public sealed record ErasureAuthorizationDef(bool? Self, IReadOnlyList<string>? Roles, CommandOwnershipVia? Via);
