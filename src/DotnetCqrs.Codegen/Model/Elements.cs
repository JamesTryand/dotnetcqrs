namespace DotnetCqrs.Codegen.Model;

/// <summary>The schema's "5 elements" (Event/Command/ReadModel/Screen/Automation) plus
/// the optional notation layer (swimlane, chapter, actor lane) — all simple, flat
/// records, unlike <see cref="Slice"/>/<see cref="Scenario"/> which are conditionally
/// shaped and need polymorphic deserialization.</summary>
public sealed record Swimlane(string Id, string Name, string Kind, string? Description);

public sealed record EventDef(string Name, string SwimlaneId, string? Description, string? Aggregate, bool? EndsStream, IReadOnlyList<Field>? Fields);

public sealed record CommandDef(string Name, string? Description, string? Reason, string? Aggregate, IReadOnlyList<Field>? Fields);

public sealed record ReadModelDef(string Name, string? Description, string? Question, IReadOnlyList<string>? BuiltFromEventIds, IReadOnlyList<Field>? Fields, IReadOnlyList<ReadModelScopeDef>? Scopes);

/// <summary>One <c>readModel.scopes</c> entry: a query param that resolves through
/// another read model rather than naming one of this model's own columns — the
/// semi-join case (e.g. <c>pmStaffId</c> → <c>project-managers.staffId</c> →
/// <c>project-managers.projectId</c> → this model's own <c>projectId</c>).</summary>
public sealed record ReadModelScopeDef(string Param, ReadModelScopeVia Via);

public sealed record ReadModelScopeVia(string ReadModelId, string MatchParamTo, string SelectField, string FilterLocalField);

public sealed record ScreenDef(string Name, string? Description, string? ActorLaneId);

public sealed record AutomationDef(string Name, string? Description);

public sealed record Chapter(string Name, string? Description);

public sealed record ActorLane(string Name, string? Description);
