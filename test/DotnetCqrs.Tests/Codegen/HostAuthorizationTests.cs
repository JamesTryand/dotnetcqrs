using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A host's own <c>--verify</c> runs the command's authorization before its deciders, as the gateway does and as
/// model-only verify does: the host hands over its generated <c>CommandAuthorization.ActorRefusalAsync</c>
/// (<c>HostDeciders.Authorization</c>), and the verifier builds the read models it reads from the scenario's given
/// events with the host's own projections. Without it, a scenario whose authorization reads read models can't be
/// checked, and fails saying so rather than passing a refusal the gateway would make.
/// The host here is the schema's state-ownership example, generated and compiled with a program that verifies it.
/// </summary>
[Trait("Category", "Compiles")]
public class HostAuthorizationTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static string ProgramCs(string documentPath) => $$""""
        using DotnetCqrs.Codegen;
        using DotnetCqrs.Codegen.Mapping;
        using DotnetCqrs.Codegen.Verification;

        var doc = DocumentLoader.LoadFromFile(@"{{documentPath}}");
        var mapped = DocumentMapper.Map(doc);
        var register = (DotnetCqrs.Deciders.DeciderRegistry registry, DotnetCqrs.Crypto.IKmsClient kms) =>
        {
            registry.Register("loanRequest", Generated.LoanRequest.LoanRequestDecider.Create());
            registry.Register("branch", Generated.Branch.BranchDecider.Create());
        };

        async Task PrintAsync(string run, HostDeciders host)
        {
            foreach (var r in await ScenarioVerifier.VerifyAsync(doc, mapped, host))
                Console.WriteLine($"{run} {(r.Passed ? "PASS" : "FAIL")} {r.ScenarioId}: {r.Detail}");
        }

        await PrintAsync("with", new HostDeciders(register) { Authorization = Generated.CommandAuthorization.ActorRefusalAsync });
        await PrintAsync("without", new HostDeciders(register));
        Console.WriteLine("DONE");
        """";

    // The example, less the approvals' approvedBy: this host's deciders are the generated stand-ins, which copy the
    // payload and stamp nothing, where a real host's decider stamps the actor. Plus a role gate (schema 4.1.0
    // when.role) on a command whose authorization reads no read model: a 30-day loan is a coordinator's to ask for.
    private static string Example()
    {
        var node = JsonNode.Parse(File.ReadAllText(TestDataPath("state-ownership.json")))!;
        var scenarios = node["slices"]!.AsArray().SelectMany(s => s!["scenarios"]!.AsArray()).ToList();
        foreach (var id in new[] { "branch-volunteer-approves", "coordinator-approves-at-any-branch" })
            scenarios.Single(s => (string?)s!["id"] == id)!["then"]!["events"]![0]!.AsObject().Remove("data");
        node["commands"]!["request-loan"]!["fieldGatedRole"] = JsonNode.Parse("""{ "field": "loanDays", "value": 30, "requiredRole": "coordinator" }""");
        node["slices"]!.AsArray().Single(s => (string?)s!["id"] == "request-loan-slice")!["scenarios"]!.AsArray().Add(JsonNode.Parse("""
            {
              "id": "member-long-loan-refused", "name": "A member cannot ask for a 30-day loan", "kind": "error",
              "given": [], "when": { "commandId": "request-loan", "data": { "toolId": "t1", "branchId": "b1", "loanDays": 30 }, "actor": "m1", "role": "member" },
              "then": { "error": { "message": "not authorized" } }
            }
            """));
        return node.ToJsonString();
    }

    private static async Task<IReadOnlyList<string>> RunAsync()
    {
        var json = Example();
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(json));
        var files = mapped.Domains.SelectMany(CSharpGenerator.Generate).Append(CommandAuthorizationGenerator.Generate(mapped.Domains));
        var path = Path.Combine(Path.GetTempPath(), $"host-authorization-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var (success, output) = await InMemoryProgram.RunAsync(files, ProgramCs(path));

            Assert.True(success, $"the host did not compile, or its verify threw:{Environment.NewLine}{output}");
            Assert.Contains("DONE", output);
            return Lines(output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [Fact(Timeout = 180000)]
    public async Task A_hosts_verify_runs_the_commands_authorization_before_its_deciders()
    {
        var lines = await RunAsync();

        var with = lines.Where(l => l.StartsWith("with ")).ToList();
        Assert.Equal(9, with.Count);
        Assert.All(with, l => Assert.StartsWith("with PASS", l));
        Assert.Contains(with, l => l.StartsWith("with PASS volunteer-at-another-branch-is-refused: refused by the command's authorization: "));
        // The read models built from the given events let a volunteer at the request's branch through to the decider.
        Assert.Contains(with, l => l.StartsWith("with PASS branch-volunteer-approves: would append "));
        // The scenario's role (schema 4.1.0) is the host's to check too: the coordinator's bypasses the scope, and a
        // member's is refused by the role gate on a command whose authorization reads no read model.
        Assert.Contains(with, l => l.StartsWith("with PASS coordinator-approves-at-any-branch: would append "));
        Assert.Contains("with PASS member-long-loan-refused: refused by the command's authorization: not authorized", with);
    }

    [Fact(Timeout = 180000)]
    public async Task Without_the_hosts_authorization_a_scenario_it_would_decide_fails_saying_so()
    {
        var lines = await RunAsync();

        var refused = lines.Single(l => l.StartsWith("without ") && l.Contains(" volunteer-at-another-branch-is-refused: "));
        Assert.StartsWith("without FAIL", refused);
        Assert.Contains("HostDeciders.Authorization", refused);
        // A role to check is a reason to need the host's authorization too, though nothing is read.
        var gated = lines.Single(l => l.StartsWith("without ") && l.Contains(" member-long-loan-refused: "));
        Assert.StartsWith("without FAIL", gated);
        Assert.Contains("HostDeciders.Authorization", gated);
        // A command whose authorization reads nothing is verified as before.
        Assert.Contains(lines, l => l.StartsWith("without PASS requester-changes: "));
    }
}
