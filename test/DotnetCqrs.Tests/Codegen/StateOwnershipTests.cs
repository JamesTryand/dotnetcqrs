using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.15.0 <c>requiredOwnership.stateField</c>: ownership decided by the aggregate, comparing the field of its
/// own state that names the owner with the command's actor, never from a read model. Raised by timesheets W5: a
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

    private static DotnetCqrs.Codegen.Domain.Domain Map(JsonNode node) =>
        Assert.Single(DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains);

    [Fact]
    public void A_state_owner_is_mapped_onto_the_command_and_not_onto_authorization()
    {
        var domain = Map(Example());

        var update = domain.Commands.Single(c => c.Name == "UpdateExpense");
        Assert.Equal("staffId", update.OwnedBy?.StateField);
        Assert.Equal("only this expense's own staff member may change it", update.OwnedBy?.Message);
        Assert.Null(update.RequiredOwnership);
        Assert.Null(domain.Commands.Single(c => c.Name == "LogExpense").OwnedBy);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_every_scenario()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("state-ownership.json"));

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_owner_the_refusal_scenario_fails()
    {
        // The scenario must be able to fail, or it proves nothing about the generated rule.
        var node = Example();
        node["commands"]!["update-expense"]!.AsObject().Remove("requiredOwnership");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "someone-else-is-refused").Passed);
        Assert.True(results.Single(r => r.ScenarioId == "owner-updates").Passed);
    }

    [Fact]
    public void The_decider_refuses_as_forbidden_after_the_existence_check_and_authorization_reads_nothing()
    {
        var domain = Map(Example());

        var decider = CSharpGenerator.Generate(domain).Single(f => f.Name.EndsWith("Decider.cs")).Source;
        var authorization = CommandAuthorizationGenerator.Generate([domain]).Source;

        var check = "if (!string.Equals(state.StaffId, cmd.Actor, StringComparison.Ordinal)) throw new CommandForbiddenException(\"only this expense's own staff member may change it\");";
        Assert.Contains(check, decider);
        Assert.True(decider.IndexOf("does not exist\")", StringComparison.Ordinal) < decider.IndexOf(check, StringComparison.Ordinal));
        Assert.DoesNotContain("UpdateExpense", authorization);
    }

    [Fact]
    public void Without_a_message_the_refusal_names_the_owner_field()
    {
        var node = Example();
        node["commands"]!["update-expense"]!["requiredOwnership"]!.AsObject().Remove("message");
        var domain = Map(node);

        var decider = CSharpGenerator.Generate(domain).Single(f => f.Name.EndsWith("Decider.cs")).Source;

        Assert.Contains("throw new CommandForbiddenException(\"only this expense's own staffId may UpdateExpense\");", decider);
    }

    [Theory]
    [InlineData("ownerId", "names state field \"ownerId\", which no event of this aggregate carries")]
    [InlineData("value", "requiredOwnership.stateField \"value\" is decimal; an owner is text (a string)")]
    public void An_owner_that_is_not_a_string_state_field_is_an_error(string field, string error)
    {
        var node = Example();
        node["commands"]!["update-expense"]!["requiredOwnership"]!["stateField"] = field;

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())));

        Assert.Contains(ex.Report.Errors, e => e.Contains(error, StringComparison.Ordinal));
    }
}
