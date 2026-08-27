namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// One scenario checked against the generated code — ports pocketcqrs's
/// <c>emschema.ScenarioResult</c>. A FAILING scenario is not a verification error: the
/// generated decider/projection is a starting point whose rules are the author's job,
/// so a scenario failing usually means the document describes behavior nobody has
/// written yet. The useful output is the list, not a verdict.
/// </summary>
public sealed record ScenarioResult(
    string SliceId,
    string ScenarioId,
    string Name,
    string Kind,
    bool Passed,
    bool Skipped,
    string Detail);
