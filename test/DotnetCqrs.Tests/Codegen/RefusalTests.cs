using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.10.0 <c>command.refuseWhen</c>: refusals declared on a command, over the
/// aggregate's own state and the command's fields, generated into Decide. Raised by
/// timesheets D29: every such guard was hand-written, so the scenario harness (which runs
/// the generated decider) reported each as a failure.
/// </summary>
[Trait("Category", "Compiles")]
public class RefusalTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    [Fact]
    public void Refusals_are_mapped_onto_the_command_in_order()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("refusals.json"));

        var domain = Assert.Single(DocumentMapper.Map(doc).Domains);

        var claim = domain.Commands.Single(c => c.Name == "ClaimEntryForInvoice");
        var refusal = Assert.Single(claim.Refusals);
        Assert.Equal("entry is on another invoice", refusal.Message);
        Assert.Equal(2, refusal.All.Count);

        var update = domain.Commands.Single(c => c.Name == "UpdateEntry");
        Assert.Equal(["entry is on an invoice", "hours must be positive"], update.Refusals.Select(r => r.Message));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_every_scenario()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("refusals.json"));

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    // Every operator, on both sides, each with a scenario that the rule refuses and one it
    // lets through. "touch" is refused by the FIRST rule that holds, so each error scenario
    // makes only its own rule hold.
    private const string OperatorsJson = """
        {
          "eventModelingSchemaVersion": "3.10.0", "id": "operators-test", "name": "Operators Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "gate-opened": {"name": "Gate Opened", "swimlaneId": "s", "aggregate": "Gate", "fields": [
              {"name": "gateId", "type": "string"}, {"name": "owner", "type": "string"},
              {"name": "locked", "type": "boolean"}]},
            "gate-touched": {"name": "Gate Touched", "swimlaneId": "s", "aggregate": "Gate", "fields": [
              {"name": "gateId", "type": "string"}, {"name": "level", "type": "decimal"}]}
          },
          "commands": {
            "open-gate": {"name": "Open Gate", "aggregate": "Gate", "fields": [
              {"name": "owner", "type": "string"}, {"name": "locked", "type": "boolean"}]},
            "touch-gate": {"name": "Touch Gate", "aggregate": "Gate", "fields": [
                {"name": "owner", "type": "string"}, {"name": "level", "type": "decimal"},
                {"name": "tags", "type": "string", "cardinality": "list"}, {"name": "note", "type": "string"}],
              "refuseWhen": [
                {"message": "gate is locked", "all": [{"state": "locked", "equals": true}]},
                {"message": "not your gate", "all": [{"state": "owner", "notEquals": {"command": "owner"}}]},
                {"message": "level must not be zero", "all": [{"command": "level", "equals": 0}]},
                {"message": "forbidden tag", "all": [{"command": "tags", "contains": "forbidden"}]},
                {"message": "a note is required", "all": [{"command": "note", "is": "notSet"}]},
                {"message": "already touched", "all": [{"state": "level", "is": "set"}, {"command": "level", "notEquals": {"state": "level"}}]}
              ]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "open-slice", "name": "Open", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "open-gate", "eventIds": ["gate-opened"],
             "scenarios": [{"id": "open", "name": "Open", "kind": "stateChange", "given": [],
               "when": {"commandId": "open-gate", "data": {"owner": "ann", "locked": false}},
               "then": {"events": [{"eventId": "gate-opened"}]}}]},
            {"id": "touch-slice", "name": "Touch", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "touch-gate", "eventIds": ["gate-touched"],
             "scenarios": [
               {"id": "touch-ok", "name": "No rule holds", "kind": "stateChange",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": ["fine"], "note": "n"}},
                "then": {"events": [{"eventId": "gate-touched"}]}},
               {"id": "touch-same-level-again", "name": "The same level again is not a change", "kind": "stateChange",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}},
                          {"eventId": "gate-touched", "data": {"gateId": "g1", "level": 2}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": [], "note": "n"}},
                "then": {"events": [{"eventId": "gate-touched"}]}},
               {"id": "locked", "name": "state equals literal", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": true}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": [], "note": "n"}},
                "then": {"error": {"message": "gate is locked"}}},
               {"id": "not-owner", "name": "state notEquals command", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "bob", "level": 2, "tags": [], "note": "n"}},
                "then": {"error": {"message": "not your gate"}}},
               {"id": "zero", "name": "command equals literal", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 0, "tags": [], "note": "n"}},
                "then": {"error": {"message": "level must not be zero"}}},
               {"id": "forbidden", "name": "command contains literal", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": ["ok", "forbidden"], "note": "n"}},
                "then": {"error": {"message": "forbidden tag"}}},
               {"id": "no-note", "name": "command is notSet", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": []}},
                "then": {"error": {"message": "a note is required"}}},
               {"id": "different-level", "name": "state is set, and command notEquals state", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}},
                          {"eventId": "gate-touched", "data": {"gateId": "g1", "level": 2}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 3, "tags": [], "note": "n"}},
                "then": {"error": {"message": "already touched"}}}
             ]}
          ]
        }
        """;

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Every_operator_refuses_with_its_own_message_and_lets_the_rest_through()
    {
        var doc = DocumentLoader.Parse(OperatorsJson);

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(9, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_declared_refusals_the_error_scenarios_fail()
    {
        // The same document with refuseWhen taken away: the scenarios above must be able to
        // fail, or they prove nothing about the generated rules.
        var node = JsonNode.Parse(OperatorsJson)!;
        node["commands"]!["touch-gate"]!.AsObject().Remove("refuseWhen");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        var errors = results.Where(r => r.Kind == "error").ToList();
        Assert.Equal(6, errors.Count);
        Assert.All(errors, r => Assert.False(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact]
    public void A_message_is_generated_as_written()
    {
        // The generated decider is merged by hand into hosts, so its messages must read as
        // the model wrote them, not as '-escaped JSON.
        var node = JsonNode.Parse(OperatorsJson)!;
        node["commands"]!["touch-gate"]!["refuseWhen"]![0]!["message"] = "the gate's locked";
        var domain = Assert.Single(DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains);

        var decider = CSharpGenerator.Generate(domain).Single(f => f.Name.EndsWith("Decider.cs"));

        Assert.Contains("throw new InvalidOperationException(\"the gate's locked\");", decider.Source);
    }

    [Theory]
    [InlineData("""{"state": "nosuch", "is": "set"}""", "state field \"nosuch\"")]
    [InlineData("""{"command": "nosuch", "is": "set"}""", "command field \"nosuch\"")]
    [InlineData("""{"command": "note", "contains": "x"}""", "contains")]
    [InlineData("""{"state": "owner", "notEquals": {"command": "nosuch"}}""", "command field \"nosuch\"")]
    public void A_rule_naming_a_field_that_does_not_exist_or_cannot_be_compared_is_refused(string condition, string expected)
    {
        var node = JsonNode.Parse(OperatorsJson)!;
        node["commands"]!["touch-gate"]!["refuseWhen"] = JsonNode.Parse($$"""[{"message": "m", "all": [{{condition}}]}]""");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(expected, ex.Message);
    }
}
