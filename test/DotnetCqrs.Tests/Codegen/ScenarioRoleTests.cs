using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 4.1.0 <c>when.role</c>: the role a command scenario's actor holds. Verify runs the command's authorization
/// with it, as the gateway does with the caller's resolved role: <c>requiredRole</c> and <c>fieldGatedRole</c> must
/// admit it, and a <c>requiredOwnership</c> (via) or <c>scope</c> whose <c>bypassRoles</c> name it is skipped. Without
/// it, a scenario's actor holds the command's required roles and none of its bypass roles, as before.
/// The document is the schema's state-ownership example, whose approval scope a coordinator bypasses.
/// </summary>
[Trait("Category", "Compiles")]
public class ScenarioRoleTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static JsonNode Example() => JsonNode.Parse(File.ReadAllText(TestDataPath("state-ownership.json")))!;

    private static JsonArray Scenarios(JsonNode node, string sliceId) =>
        node["slices"]!.AsArray().Single(s => (string?)s!["id"] == sliceId)!["scenarios"]!.AsArray();

    private static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(JsonNode node)
    {
        var doc = DocumentLoader.Parse(node.ToJsonString());
        return await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));
    }

    private static ScenarioResult Single(IReadOnlyList<ScenarioResult> results, string id) => results.Single(r => r.ScenarioId == id);

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_role_named_in_bypassRoles_skips_the_scope()
    {
        var results = await VerifyAsync(Example());

        var coordinator = Single(results, "coordinator-approves-at-any-branch");
        Assert.True(coordinator.Passed, coordinator.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_role_the_same_actor_is_refused_by_the_scope()
    {
        // The scenario must be able to fail: the role, not the actor, is what lets the coordinator through.
        var node = Example();
        Scenarios(node, "approve-loan-request-slice").Single(s => (string?)s!["id"] == "coordinator-approves-at-any-branch")!["when"]!.AsObject().Remove("role");

        var results = await VerifyAsync(node);

        var coordinator = Single(results, "coordinator-approves-at-any-branch");
        Assert.False(coordinator.Passed);
        Assert.Contains("the command's authorization refused it: only a volunteer at this loan request's branch may approve it", coordinator.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_role_requiredRole_does_not_admit_is_refused_and_a_scenario_without_a_role_holds_it()
    {
        var node = Example();
        node["commands"]!["approve-loan-request"]!["requiredRole"] = new JsonArray("volunteer", "coordinator");
        Scenarios(node, "approve-loan-request-slice").Add(JsonNode.Parse("""
            {
              "id": "member-cannot-approve", "name": "A member, even one at the branch, cannot approve", "kind": "error",
              "given": [
                { "eventId": "loan-requested", "data": { "loanRequestId": "r1", "memberId": "m1", "toolId": "t1", "branchId": "b1", "loanDays": 3 } },
                { "eventId": "volunteer-joined-branch", "data": { "enrolmentId": "b1:v1", "branchId": "b1", "volunteerId": "v1" } }
              ],
              "when": { "commandId": "approve-loan-request", "data": {}, "actor": "v1", "role": "member" },
              "then": { "error": { "message": "not authorized" } }
            }
            """));

        var results = await VerifyAsync(node);

        var member = Single(results, "member-cannot-approve");
        Assert.True(member.Passed, member.Detail);
        Assert.Equal("refused by the command's authorization: not authorized", member.Detail);
        // Without a role, the volunteer holds the required role, as before 4.1.0; the coordinator's role is admitted.
        Assert.True(Single(results, "branch-volunteer-approves").Passed);
        Assert.True(Single(results, "coordinator-approves-at-any-branch").Passed);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_fieldGatedRole_checks_the_role_only_when_its_condition_applies()
    {
        var node = Example();
        node["commands"]!["request-loan"]!["fieldGatedRole"] = JsonNode.Parse("""{ "field": "loanDays", "value": 30, "requiredRole": "coordinator" }""");
        var scenarios = Scenarios(node, "request-loan-slice");
        scenarios.Add(JsonNode.Parse("""
            {
              "id": "member-long-loan-refused", "name": "A member cannot ask for a 30-day loan", "kind": "error",
              "given": [], "when": { "commandId": "request-loan", "data": { "toolId": "t1", "branchId": "b1", "loanDays": 30 }, "actor": "m1", "role": "member" },
              "then": { "error": { "message": "not authorized" } }
            }
            """));
        scenarios.Add(JsonNode.Parse("""
            {
              "id": "coordinator-long-loan", "name": "A coordinator can", "kind": "stateChange",
              "given": [], "when": { "commandId": "request-loan", "data": { "toolId": "t1", "branchId": "b1", "loanDays": 30 }, "actor": "c1", "role": "coordinator" },
              "then": { "events": [{ "eventId": "loan-requested" }] }
            }
            """));
        scenarios.Add(JsonNode.Parse("""
            {
              "id": "member-short-loan", "name": "A member's 3-day loan is not gated", "kind": "stateChange",
              "given": [], "when": { "commandId": "request-loan", "data": { "toolId": "t1", "branchId": "b1", "loanDays": 3 }, "actor": "m1", "role": "member" },
              "then": { "events": [{ "eventId": "loan-requested" }] }
            }
            """));

        var results = await VerifyAsync(node);

        Assert.All(new[] { "member-long-loan-refused", "coordinator-long-loan", "member-short-loan" },
            id => Assert.True(Single(results, id).Passed, $"{id}: {Single(results, id).Detail}"));
    }
}
