namespace DotnetCqrs.Codegen.Model;

/// <summary>One field of an <see cref="EventDef"/>/<see cref="CommandDef"/>/
/// <see cref="ReadModelDef"/>, per the schema's <c>field</c> definition.</summary>
public sealed record Field(
    string Name,
    string Type,
    string? Description,
    bool? Optional,
    string? Cardinality,
    bool? IdAttribute,
    bool? Pii,
    IReadOnlyList<Field>? Subfields);
