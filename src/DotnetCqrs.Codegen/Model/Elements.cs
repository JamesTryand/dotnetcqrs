using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>The schema's "5 elements" (Event/Command/ReadModel/Screen/Automation) plus
/// the optional notation layer (swimlane, chapter, actor lane) — all simple, flat
/// records, unlike <see cref="Slice"/>/<see cref="Scenario"/> which are conditionally
/// shaped and need polymorphic deserialization.</summary>
public sealed record Swimlane(string Id, string Name, string Kind, string? Description);

public sealed record EventDef(string Name, string SwimlaneId, string? Description, string? Aggregate, bool? EndsStream, IReadOnlyList<Field>? Fields);

/// <summary><c>RequiredRole</c>/<c>FieldGatedRole</c>/<c>RequiredOwnership</c>/<c>Scope</c>
/// are schema 2.5.0's command-authorization declarations -- see
/// <see cref="CommandFieldGatedRoleDef"/>/<see cref="CommandOwnershipDef"/>/
/// <see cref="CommandScopeDef"/> for each shape.</summary>
public sealed record CommandDef(
    string Name, string? Description, string? Reason, string? Aggregate, IReadOnlyList<Field>? Fields,
    [property: JsonConverter(typeof(RoleOrRolesConverter))] IReadOnlyList<string>? RequiredRole,
    CommandFieldGatedRoleDef? FieldGatedRole,
    CommandOwnershipDef? RequiredOwnership,
    CommandScopeDef? Scope);

/// <summary><c>RequiredRole</c> is schema 2.7.0's <c>readModel.requiredRole</c> -- the
/// read-side mirror of <see cref="CommandDef.RequiredRole"/>, same shape (a role id, or
/// a non-empty array, satisfied by membership) and same converter.</summary>
public sealed record ReadModelDef(
    string Name, string? Description, string? Question, IReadOnlyList<string>? BuiltFromEventIds,
    IReadOnlyList<Field>? Fields, IReadOnlyList<ReadModelScopeDef>? Scopes, IReadOnlyList<ReadModelFilterDef>? Filters,
    [property: JsonConverter(typeof(RoleOrRolesConverter))] IReadOnlyList<string>? RequiredRole);

/// <summary>One <c>readModel.scopes</c> entry: a query param that resolves through
/// another read model rather than naming one of this model's own columns — the
/// semi-join case (e.g. <c>pmStaffId</c> → <c>project-managers.staffId</c> →
/// <c>project-managers.projectId</c> → this model's own <c>projectId</c>).</summary>
public sealed record ReadModelScopeDef(string Param, ReadModelScopeVia Via);

public sealed record ReadModelScopeVia(string ReadModelId, string MatchParamTo, string SelectField, string FilterLocalField);

/// <summary>One <c>readModel.filters</c> entry (schema 2.4.0): a query param naming a
/// single-field WHERE-range filter with named presets (e.g. <c>dateRange</c> over
/// <c>taskDate</c>, presets <c>last7Days</c>/<c>lastCalendarMonth</c>/<c>custom</c>).
/// <c>Presets</c> is nullable/optional here even though the schema's own
/// <c>allOf</c>/<c>if</c>/<c>then</c> requires it whenever <c>kind</c> is
/// <c>"dateRange"</c> (the only kind that exists) — real documents can never reach
/// <see cref="Mapping.DocumentMapper"/> with it missing, since JSON Schema validation
/// already rejects that shape before mapping runs, but a <see cref="Document"/> built
/// directly in C# (as a defensive unit test does) can, so the mapper still guards it.</summary>
///
/// <para>Schema 3.1.0 adds <c>kind: "match"</c>: <c>Mode</c> (<c>exact</c>/<c>prefix</c>/
/// <c>contains</c>, required for match), <c>Normalize</c> (optional, default
/// <c>caseFold</c>) and <c>MinPrefixLength</c> (prefix only). They are null for a
/// dateRange filter, just as <c>Presets</c> is null for a match.</para>
public sealed record ReadModelFilterDef(string Param, string Field, string Kind, IReadOnlyList<string>? Presets,
    string? Mode = null, string? Normalize = null, int? MinPrefixLength = null);

public sealed record ScreenDef(string Name, string? Description, string? ActorLaneId);

public sealed record AutomationDef(string Name, string? Description);

public sealed record Chapter(string Name, string? Description);

public sealed record ActorLane(string Name, string? Description);
