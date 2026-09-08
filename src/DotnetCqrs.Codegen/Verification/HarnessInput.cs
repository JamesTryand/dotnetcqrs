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

/// <summary>A stateView scenario's given event, with a per-event row key already
/// resolved for the main projection and each active scope's via-projection — see
/// <see cref="ScenarioVerifier"/>'s row-key resolution doc comment. Distinct rows in the
/// same table (e.g. two different flagged entries) need distinct keys; events about the
/// same entity (or carrying no identifying data at all) need the SAME key so they fold
/// onto one row, matching how a real event stream would replay.</summary>
public sealed record ViewGivenEventInput(
    string Type,
    string Data,
    string MainKey,
    IReadOnlyDictionary<string, string> ViaKeys); // via-projection type name -> row key

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
    IReadOnlyList<ViewGivenEventInput> Given,
    string? QueryParams,
    string? AsOf, // schema 2.6.0 -- when present, pins "today" for a dateRange preset in QueryParams instead of the live clock
    string ExpectedResult,
    IReadOnlyList<ViewScopeInput> Scopes, // only entries whose param is present in QueryParams
    IReadOnlyList<ViewFilterInput> Filters); // every readModel.filters entry -- the harness only acts on one whose param is actually present in QueryParams

/// <summary>One active <c>readModel.scopes</c> entry for this scenario's query --
/// resolved to the VIA read model's own generated projection, so the harness can seed
/// its table from the same `given` events before running the scoped query. A stateView
/// scenario's `given` is already NOT split by aggregate (see <c>BuildViewScenario</c>),
/// so it's real fixture history for the via projection too, whichever stream it
/// actually belongs to.</summary>
public sealed record ViewScopeInput(
    string Param,
    string ViaProjectionTypeName,
    string ViaTableName,
    string MatchParamToColumn,
    string SelectColumn,
    string FilterLocalColumn);

/// <summary>One <c>readModel.filters</c> entry (schema 2.4.0), resolved to this read
/// model's own physical column name — see <c>Domain.ReadModelFilter</c>. Passed
/// whole rather than pre-filtered by whether its param appears in <c>QueryParams</c>
/// (unlike <see cref="ViewScopeInput"/>, which needs another read model to have been
/// generated at all): a filter references no other element, so there's no failure mode
/// to check ahead of time, and the harness's own <c>SelectRowsAsync</c> only consults an
/// entry whose param it actually sees.</summary>
public sealed record ViewFilterInput(string Param, string Field, string Kind);
