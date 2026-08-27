namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// What <see cref="ScenarioVerifier"/> hands to the compiled harness: every scenario
/// already resolved to generated names/types, so the harness itself is a fixed,
/// reusable program (see <c>HarnessProgram.txt</c>) rather than something generated per
/// document. Only used for JSON serialization to the harness's input file — the
/// harness has its own matching record definitions, deliberately not shared code,
/// since it compiles as a separate scratch program.
/// </summary>
public sealed record HarnessInput(
    IReadOnlyList<CommandScenarioInput> CommandScenarios,
    IReadOnlyList<ViewScenarioInput> ViewScenarios);

public sealed record GivenEventInput(string Type, string Data);

/// <summary>A stateChange or error scenario, ready for the harness to run without
/// consulting the document again.</summary>
public sealed record CommandScenarioInput(
    string SliceId,
    string ScenarioId,
    string Name,
    string Kind, // "stateChange" | "error"
    string DeciderTypeName, // e.g. "Generated.Order.OrderDecider"
    string Aggregate,
    string StreamId,
    IReadOnlyList<GivenEventInput> Given, // this aggregate's own events only -- see ScenarioVerifier's SplitGiven
    string CommandName,
    string CommandPayload,
    IReadOnlyList<string> ExpectedEventTypes); // empty/unused for "error" kind

/// <summary>A stateView scenario, ready for the harness to run without consulting the
/// document again.</summary>
public sealed record ViewScenarioInput(
    string SliceId,
    string ScenarioId,
    string Name,
    string ProjectionTypeName, // e.g. "Generated.Order.OrderSummaryProjection"
    string Aggregate,
    string TableName,
    string KeyColumn,
    string StreamId,
    IReadOnlyList<GivenEventInput> Given,
    string? QueryParams,
    string ExpectedResult);
