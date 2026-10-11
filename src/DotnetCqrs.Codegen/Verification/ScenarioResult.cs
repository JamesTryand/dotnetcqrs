namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// One scenario checked against the generated code — ports pocketcqrs's
/// <c>emschema.ScenarioResult</c>. A FAILING scenario is not a verification error: the
/// generated decider/projection is a starting point whose rules are the author's job,
/// so a scenario failing usually means the document describes behavior nobody has
/// written yet. The useful output is the list, not a verdict.
///
/// <para><c>ByHand</c> (schema 3.12.0): the scenario reached an outcome whose condition is
/// hand-written (<c>when: "hand"</c>). The generated decider can't make that choice, so the
/// scenario is neither passed nor failed here: it was accepted, its events are one of the declared
/// outcomes, and the choice itself is checked against a host's own deciders.</para>
/// </summary>
public sealed record ScenarioResult(
    string SliceId,
    string ScenarioId,
    string Name,
    string Kind,
    bool Passed,
    bool Skipped,
    string Detail,
    bool ByHand = false);
