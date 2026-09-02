using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>
/// A <see cref="Field"/>'s optional <c>derivation</c> — when present, the field is
/// COMPUTED as a fold over named events instead of copied from a same-named payload
/// key. Shaped by <c>kind</c> per the schema's <c>allOf</c>/<c>if</c>/<c>then</c> rules,
/// so modeled as a discriminated union (see <see cref="ScenarioConverter"/>'s doc
/// comment on <c>Scenario</c> for why: the very next milestone (mapping) needs to
/// switch on exactly this shape).
/// </summary>
[JsonConverter(typeof(FieldDerivationConverter))]
public abstract record FieldDerivation(string Kind);

/// <summary>Boolean fold: <c>onEventIds</c> set it true, <c>offEventIds</c> set it
/// false. Both required — a toggle with no way back off is just <see cref="CountDerivation"/>
/// with extra steps.</summary>
public sealed record ToggleDerivation(string Kind, IReadOnlyList<string> OnEventIds, IReadOnlyList<string> OffEventIds, bool? Initial)
    : FieldDerivation(Kind);

/// <summary>Integer fold: <c>incrementOnEventIds</c> add one, <c>decrementOnEventIds</c>
/// subtract one. <c>rowKeyField</c> names the payload field on those events identifying
/// the target row, since the counted events are not on the read model's own stream —
/// defaults to the read model's own key field when omitted.</summary>
public sealed record CountDerivation(string Kind, IReadOnlyList<string> IncrementOnEventIds, IReadOnlyList<string>? DecrementOnEventIds, string? RowKeyField)
    : FieldDerivation(Kind);

/// <summary>Numeric fold: the same shape as <see cref="CountDerivation"/> but adding/
/// subtracting a named payload amount instead of a flat one.</summary>
public sealed record SumDerivation(string Kind, IReadOnlyList<string> AddOnEventIds, IReadOnlyList<string>? SubtractOnEventIds, string AmountField, string? RowKeyField)
    : FieldDerivation(Kind);

/// <summary>Grouped-rollup fold (schema 2.3.0): the owning <see cref="Field"/> is a
/// <c>cardinality: "list"</c> field with <c>subfields</c> (schema-enforced, not
/// re-checked here) -- one nested row per distinct value of <c>groupByField</c>, the
/// payload field on a contributing event whose value becomes that row's key. Carries no
/// row-key/amount of its own: each <see cref="Field.Subfields"/> entry is an ordinary
/// <see cref="Field"/> that may carry its own <see cref="ToggleDerivation"/>/
/// <see cref="CountDerivation"/>/<see cref="SumDerivation"/>, computed within its group
/// rather than across the whole read model -- the field named by <c>groupByField</c>
/// itself needs none, since its value is just the grouping key, copied straight from the
/// matching event payload.</summary>
public sealed record GroupByDerivation(string Kind, string GroupByField) : FieldDerivation(Kind);

public sealed class FieldDerivationConverter() : DiscriminatedUnionConverter<FieldDerivation>("kind", new Dictionary<string, Type>
{
    ["toggle"] = typeof(ToggleDerivation),
    ["count"] = typeof(CountDerivation),
    ["sum"] = typeof(SumDerivation),
    ["groupBy"] = typeof(GroupByDerivation),
});
