using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

public sealed record Hotspot(string Note, HotspotTarget Target, bool? Resolved);

/// <summary>The schema's <c>hotspotTarget</c> is a <c>oneOf</c> three shapes,
/// discriminated by <c>kind</c> — same discriminated-union approach as
/// <see cref="Slice"/>/<see cref="Scenario"/> (see <see cref="SliceConverter"/>'s doc
/// comment for why not the native <c>[JsonPolymorphic]</c> attribute).</summary>
[JsonConverter(typeof(HotspotTargetConverter))]
public abstract record HotspotTarget;

public sealed record ElementHotspotTarget(string ElementType, string ElementId) : HotspotTarget;

public sealed record SliceHotspotTarget(string SliceId) : HotspotTarget;

public sealed record OrderingHotspotTarget(IReadOnlyList<string> SliceIds) : HotspotTarget;

public sealed class HotspotTargetConverter() : DiscriminatedUnionConverter<HotspotTarget>("kind", new Dictionary<string, Type>
{
    ["element"] = typeof(ElementHotspotTarget),
    ["slice"] = typeof(SliceHotspotTarget),
    ["ordering"] = typeof(OrderingHotspotTarget),
});
