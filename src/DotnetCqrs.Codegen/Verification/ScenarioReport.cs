namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// Formats <see cref="ScenarioVerifier"/>'s results as pass/fail/skip lines and turns
/// them into a process exit code. Lives here, not in DotnetCqrs.Codegen.Cli, because
/// Milestone 9's generated project needs the exact same print/exit-code logic from its
/// own <c>--verify</c> mode and cannot reference the CLI project (it's a separately
/// compiled project that only references DotnetCqrs.Codegen, which it needs anyway to
/// call ScenarioVerifier itself). See README.md's Milestone 7 "Forward-looking
/// constraint".
/// </summary>
public static class ScenarioReport
{
    /// <summary>Prints one line per result and returns a process exit code: 0 unless at
    /// least one scenario hard-failed. A hard failure is a scenario that ran and did
    /// not pass -- <see cref="ScenarioResult.Skipped"/> is a different condition (its
    /// command/read model was never generated, so it never got the chance to run) and
    /// does not by itself fail the run. Deliberately does not try to distinguish
    /// "expected" failures (e.g. a scenario documenting behavior nobody has written
    /// yet) from real ones -- that judgment is the document author's, not this
    /// helper's; it reports, it doesn't editorialize.</summary>
    public static int Print(IEnumerable<ScenarioResult> results, TextWriter writer)
    {
        var hardFailed = false;
        foreach (var r in results)
        {
            var status = r.Skipped ? "SKIP" : r.Passed ? "PASS" : "FAIL";
            writer.WriteLine($"{status} [{r.Kind}] {r.SliceId}/{r.ScenarioId} \"{r.Name}\": {r.Detail}");
            if (!r.Skipped && !r.Passed)
                hardFailed = true;
        }
        return hardFailed ? 1 : 0;
    }
}
