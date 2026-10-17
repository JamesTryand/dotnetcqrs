using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.15.0 <c>requiredOwnership.stateField</c>: ownership decided by the aggregate, comparing the field of its
/// own state that names the owner with the command's actor, never from a read model. A
/// <c>via</c> ownership check reads a projection before the command, so the first edit after a create was refused
/// while the row wasn't projected yet.
/// </summary>
[Trait("Category", "Compiles")]
public class StateOwnershipTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static JsonNode Example() => JsonNode.Parse(File.ReadAllText(TestDataPath("state-ownership.json")))!;

    private static IReadOnlyList<DotnetCqrs.Codegen.Domain.Domain> MapAll(JsonNode node) =>
        DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains;

    // The loan request's own domain: the example's other aggregate, branch, is only there for the approval scope.
    private static DotnetCqrs.Codegen.Domain.Domain Map(JsonNode node) =>
        MapAll(node).Single(d => d.Aggregate == "loanRequest");

    [Fact]
    public void A_state_owner_is_mapped_onto_the_command_and_not_onto_authorization()
    {
        var domain = Map(Example());

        var change = domain.Commands.Single(c => c.Name == "ChangeLoanRequest");
        Assert.Equal("memberId", change.OwnedBy?.StateField);
        Assert.Equal("only the member who made this loan request may change it", change.OwnedBy?.Message);
        Assert.Null(change.RequiredOwnership);
        Assert.Null(domain.Commands.Single(c => c.Name == "RequestLoan").OwnedBy);
        // The approval is scoped through read models instead: no state owner.
        Assert.Null(domain.Commands.Single(c => c.Name == "ApproveLoanRequest").OwnedBy);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_every_scenario_the_generated_code_decides()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("state-ownership.json"));

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(7, results.Count);
        // The harness runs the generated command authorization before the decider, over read models built from the
        // given events, so the scope refuses a volunteer at another branch with its message (schema 4.0.0).
        Assert.Contains("refused by the command's authorization",
            results.Single(r => r.ScenarioId == "volunteer-at-another-branch-is-refused").Detail);
        // The existence refusal names the aggregate in words, as the example does ("loan request does not exist").
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_owner_the_refusal_scenario_fails()
    {
        // The scenario must be able to fail, or it proves nothing about the generated rule.
        var node = Example();
        node["commands"]!["change-loan-request"]!.AsObject().Remove("requiredOwnership");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "another-member-is-refused").Passed);
        Assert.True(results.Single(r => r.ScenarioId == "requester-changes").Passed);
    }

    [Fact]
    public void The_decider_refuses_as_forbidden_after_the_existence_check_and_authorization_reads_nothing()
    {
        var domains = MapAll(Example());
        var domain = Map(Example());

        var decider = CSharpGenerator.Generate(domain).Single(f => f.Name.EndsWith("Decider.cs")).Source;
        var authorization = CommandAuthorizationGenerator.Generate(domains).Source;

        var check = "if (!string.Equals(state.MemberId, cmd.Actor, StringComparison.Ordinal)) throw new CommandForbiddenException(\"only the member who made this loan request may change it\");";
        Assert.Contains(check, decider);
        Assert.True(decider.IndexOf("does not exist\")", StringComparison.Ordinal) < decider.IndexOf(check, StringComparison.Ordinal));
        Assert.DoesNotContain("ChangeLoanRequest", authorization);
        // The approval's scope is authorization's: the membership row is matched on the branch and its actorField
        // (schema 4.0.0) is the column compared with the actor.
        Assert.Contains("ApproveLoanRequest", authorization);
        Assert.Contains("\"branchVolunteers\", \"branch_id\", \"volunteer_id\"", authorization);
        Assert.Contains("\"only a volunteer at this loan request's branch may approve it\"", authorization);
    }

    [Fact]
    public void Without_a_message_the_refusal_names_the_owner_field()
    {
        var node = Example();
        node["commands"]!["change-loan-request"]!["requiredOwnership"]!.AsObject().Remove("message");
        var domain = Map(node);

        var decider = CSharpGenerator.Generate(domain).Single(f => f.Name.EndsWith("Decider.cs")).Source;

        Assert.Contains("throw new CommandForbiddenException(\"only this loan request's own memberId may ChangeLoanRequest\");", decider);
    }

    [Theory]
    [InlineData("ownerId", "names state field \"ownerId\", which no event of this aggregate carries")]
    [InlineData("loanDays", "requiredOwnership.stateField \"loanDays\" is number; an owner is text (a string)")]
    public void An_owner_that_is_not_a_string_state_field_is_an_error(string field, string error)
    {
        var node = Example();
        node["commands"]!["change-loan-request"]!["requiredOwnership"]!["stateField"] = field;

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())));

        Assert.Contains(ex.Report.Errors, e => e.Contains(error, StringComparison.Ordinal));
    }
}
