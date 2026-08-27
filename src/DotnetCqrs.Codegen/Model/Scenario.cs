using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>
/// One scenario, shaped by <c>kind</c> per the schema's conditional
/// <c>if kind == X then requires ...</c> rules — modeled as a discriminated union
/// (via <see cref="ScenarioConverter"/>, see <see cref="SliceConverter"/>'s doc comment
/// for why not the native <c>[JsonPolymorphic]</c> attribute) rather than one flat
/// record with every field nullable, since the very next milestone (mapping) needs to
/// switch on exactly this shape.
/// </summary>
[JsonConverter(typeof(ScenarioConverter))]
public abstract record Scenario(string Id, string Name, IReadOnlyList<EventRef> Given);

public sealed record StateChangeScenario(string Id, string Name, IReadOnlyList<EventRef> Given, CommandRef When, EventsThen Then)
    : Scenario(Id, Name, Given);

public sealed record StateViewScenario(string Id, string Name, IReadOnlyList<EventRef> Given, ReadModelQuery When, ResultThen Then)
    : Scenario(Id, Name, Given);

public sealed record ErrorScenario(string Id, string Name, IReadOnlyList<EventRef> Given, CommandRef When, ErrorThen Then)
    : Scenario(Id, Name, Given);

public sealed record EventsThen(IReadOnlyList<EventRef> Events);

public sealed record ResultThen(JsonElement Result);

public sealed record ErrorThen(ErrorDetail Error);

public sealed record ErrorDetail(string Message, string? Code);

public sealed class ScenarioConverter() : DiscriminatedUnionConverter<Scenario>("kind", new Dictionary<string, Type>
{
    ["stateChange"] = typeof(StateChangeScenario),
    ["stateView"] = typeof(StateViewScenario),
    ["error"] = typeof(ErrorScenario),
});
