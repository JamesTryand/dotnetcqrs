using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.10.0 <c>command.refuseWhen</c>: refusals declared on a command, over the
/// aggregate's own state and the command's fields, generated into Decide. Every such
/// guard used to be hand-written, so the scenario harness (which runs
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

        var hold = domain.Commands.Single(c => c.Name == "HoldBookingForEventPlan");
        var refusal = Assert.Single(hold.Refusals);
        Assert.Equal("booking is held by another event plan", refusal.Message);
        Assert.Equal(2, refusal.All.Count);

        var change = domain.Commands.Single(c => c.Name == "ChangeBooking");
        Assert.Equal(["booking is held by an event plan", "duration must be positive", "a booking lasts at most one day"], change.Refusals.Select(r => r.Message));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_every_scenario()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("refusals.json"));

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(7, results.Count);
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
              {"name": "gateId", "type": "string"}, {"name": "level", "type": "decimal"},
              {"name": "usedTags", "type": "string", "cardinality": "list"}]}
          },
          "commands": {
            "open-gate": {"name": "Open Gate", "aggregate": "Gate", "fields": [
              {"name": "owner", "type": "string"}, {"name": "locked", "type": "boolean"}]},
            "touch-gate": {"name": "Touch Gate", "aggregate": "Gate", "fields": [
                {"name": "owner", "type": "string"}, {"name": "level", "type": "decimal"},
                {"name": "tags", "type": "string", "cardinality": "list"}, {"name": "note", "type": "string"},
                {"name": "tag", "type": "string"}],
              "refuseWhen": [
                {"message": "gate is locked", "all": [{"state": "locked", "equals": true}]},
                {"message": "not your gate", "all": [{"state": "owner", "notEquals": {"command": "owner"}}]},
                {"message": "level must not be zero", "all": [{"command": "level", "equals": 0}]},
                {"message": "forbidden tag", "all": [{"command": "tags", "contains": "forbidden"}]},
                {"message": "a note is required", "all": [{"command": "note", "is": "notSet"}]},
                {"message": "already touched", "all": [{"state": "level", "is": "set"}, {"command": "level", "notEquals": {"state": "level"}}]},
                {"message": "tag already used", "all": [{"state": "usedTags", "contains": {"command": "tag"}}]}
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
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": ["fine"], "note": "n", "usedTags": []}},
                "then": {"events": [{"eventId": "gate-touched"}]}},
               {"id": "touch-same-level-again", "name": "The same level again is not a change", "kind": "stateChange",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}},
                          {"eventId": "gate-touched", "data": {"gateId": "g1", "level": 2, "usedTags": ["a"]}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": [], "note": "n", "tag": "b", "usedTags": ["a"]}},
                "then": {"events": [{"eventId": "gate-touched"}]}},
               {"id": "tag-reused", "name": "state list contains a command field", "kind": "error",
                "given": [{"eventId": "gate-opened", "data": {"gateId": "g1", "owner": "ann", "locked": false}},
                          {"eventId": "gate-touched", "data": {"gateId": "g1", "level": 2, "usedTags": ["a"]}}],
                "when": {"commandId": "touch-gate", "data": {"owner": "ann", "level": 2, "tags": [], "note": "n", "tag": "a"}},
                "then": {"error": {"message": "tag already used"}}},
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

        Assert.Equal(10, results.Count);
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
        Assert.Equal(7, errors.Count);
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

    // Schema 3.11.0: the four numeric comparisons, against a literal and against a state field,
    // each with its boundary. For example, a conversion rate must be more
    // than 0, a VAT percentage from 0 to 100.
    private const string ComparisonsJson = """
        {
          "eventModelingSchemaVersion": "3.11.0", "id": "comparisons-test", "name": "Comparisons Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "band-set": {"name": "Band Set", "swimlaneId": "s", "aggregate": "Band", "fields": [
              {"name": "bandId", "type": "string"}, {"name": "floor", "type": "decimal"}, {"name": "cap", "type": "decimal"}]},
            "rate-quoted": {"name": "Rate Quoted", "swimlaneId": "s", "aggregate": "Band", "fields": [
              {"name": "bandId", "type": "string"}, {"name": "rate", "type": "decimal"}]}
          },
          "commands": {
            "set-band": {"name": "Set Band", "aggregate": "Band", "fields": [
              {"name": "floor", "type": "decimal"}, {"name": "cap", "type": "decimal"}]},
            "quote-rate": {"name": "Quote Rate", "aggregate": "Band", "fields": [{"name": "rate", "type": "decimal"}, {"name": "note", "type": "string"}],
              "refuseWhen": [
                {"message": "the rate must be more than 0", "all": [{"command": "rate", "atMost": 0}]},
                {"message": "the rate is at most 100", "all": [{"command": "rate", "greaterThan": 100}]},
                {"message": "below the band's floor", "all": [{"command": "rate", "lessThan": {"state": "floor"}}]},
                {"message": "at or over the band's cap", "all": [{"command": "rate", "atLeast": {"state": "cap"}}]}
              ]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "set-slice", "name": "Set", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "set-band", "eventIds": ["band-set"],
             "scenarios": [{"id": "set", "name": "Set", "kind": "stateChange", "given": [],
               "when": {"commandId": "set-band", "data": {"floor": 1, "cap": 50}}, "then": {"events": [{"eventId": "band-set"}]}}]},
            {"id": "quote-slice", "name": "Quote", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "quote-rate", "eventIds": ["rate-quoted"],
             "scenarios": [
               {"id": "in-band", "name": "No rule holds", "kind": "stateChange",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 49.99}}, "then": {"events": [{"eventId": "rate-quoted"}]}},
               {"id": "at-the-floor", "name": "lessThan: the floor itself is allowed", "kind": "stateChange",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 1}}, "then": {"events": [{"eventId": "rate-quoted"}]}},
               {"id": "no-rate", "name": "A comparison with an unset value never holds", "kind": "stateChange",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"note": "n"}}, "then": {"events": [{"eventId": "rate-quoted"}]}},
               {"id": "zero", "name": "atMost: the boundary refuses", "kind": "error",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 0}}, "then": {"error": {"message": "the rate must be more than 0"}}},
               {"id": "negative", "name": "atMost: below it refuses", "kind": "error",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": -2}}, "then": {"error": {"message": "the rate must be more than 0"}}},
               {"id": "over-100", "name": "greaterThan a literal", "kind": "error",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 500}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 100.5}}, "then": {"error": {"message": "the rate is at most 100"}}},
               {"id": "below-floor", "name": "lessThan a state field", "kind": "error",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 0.5}}, "then": {"error": {"message": "below the band's floor"}}},
               {"id": "at-cap", "name": "atLeast a state field: the boundary refuses", "kind": "error",
                "given": [{"eventId": "band-set", "data": {"bandId": "b1", "floor": 1, "cap": 50}}],
                "when": {"commandId": "quote-rate", "data": {"rate": 50}}, "then": {"error": {"message": "at or over the band's cap"}}}
             ]}
          ]
        }
        """;

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Each_comparison_refuses_at_and_past_its_boundary_and_never_on_an_unset_value()
    {
        var doc = DocumentLoader.Parse(ComparisonsJson);

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(9, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_comparisons_their_error_scenarios_fail()
    {
        var node = JsonNode.Parse(ComparisonsJson)!;
        node["commands"]!["quote-rate"]!.AsObject().Remove("refuseWhen");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        var errors = results.Where(r => r.Kind == "error").ToList();
        Assert.Equal(5, errors.Count);
        Assert.All(errors, r => Assert.False(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_date_or_number_an_event_does_not_carry_is_unset_not_a_default()
    {
        // An item logged without closedOn must not read as "closed". The
        // generated payload record held a date (and a number, and a bool) as a non-nullable value, so a field
        // the event did not carry folded into state as 0001-01-01 (or 0, or false) and "is set" held for it.
        const string json = """
            {
              "eventModelingSchemaVersion": "3.11.0", "id": "unset-test", "name": "Unset Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "item-logged": {"name": "Item Logged", "swimlaneId": "s", "aggregate": "Item", "fields": [
                  {"name": "itemId", "type": "string"}, {"name": "closedOn", "type": "date"},
                  {"name": "weight", "type": "decimal"}, {"name": "sealed", "type": "boolean"}]},
                "item-touched": {"name": "Item Touched", "swimlaneId": "s", "aggregate": "Item", "fields": [
                  {"name": "itemId", "type": "string"}]}
              },
              "commands": {
                "log-item": {"name": "Log Item", "aggregate": "Item", "fields": [
                  {"name": "closedOn", "type": "date"}, {"name": "weight", "type": "decimal"}, {"name": "sealed", "type": "boolean"}]},
                "touch-item": {"name": "Touch Item", "aggregate": "Item", "fields": [],
                  "refuseWhen": [
                    {"message": "item is closed", "all": [{"state": "closedOn", "is": "set"}]},
                    {"message": "item is weighed", "all": [{"state": "weight", "is": "set"}]},
                    {"message": "item is sealed", "all": [{"state": "sealed", "is": "set"}]}
                  ]}
              },
              "screens": {"scr": {"name": "Screen"}},
              "slices": [
                {"id": "log-slice", "name": "Log", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
                 "screenId": "scr", "commandId": "log-item", "eventIds": ["item-logged"],
                 "scenarios": [{"id": "log", "name": "Log", "kind": "stateChange", "given": [],
                   "when": {"commandId": "log-item", "data": {}}, "then": {"events": [{"eventId": "item-logged"}]}}]},
                {"id": "touch-slice", "name": "Touch", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
                 "screenId": "scr", "commandId": "touch-item", "eventIds": ["item-touched"],
                 "scenarios": [
                   {"id": "open-item", "name": "Logged without a date, a weight or a seal: none is set", "kind": "stateChange",
                    "given": [{"eventId": "item-logged", "data": {"itemId": "i1"}}],
                    "when": {"commandId": "touch-item", "data": {}}, "then": {"events": [{"eventId": "item-touched"}]}},
                   {"id": "closed-item", "name": "Logged with a date: set", "kind": "error",
                    "given": [{"eventId": "item-logged", "data": {"itemId": "i1", "closedOn": "2026-09-30"}}],
                    "when": {"commandId": "touch-item", "data": {}}, "then": {"error": {"message": "item is closed"}}}
                 ]}
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    // Dates compare for equality by calendar day: a state date is held as a DateTime, a command's
    // arrives as text, and either may carry a time of day. For example, a
    // period starts the day after the last one ended.
    private const string DatesJson = """
        {
          "eventModelingSchemaVersion": "3.14.0", "id": "dates-test", "name": "Dates Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "period-closed": {"name": "Period Closed", "swimlaneId": "s", "aggregate": "Ledger", "fields": [
              {"name": "ledgerId", "type": "string"}, {"name": "start", "type": "date"}, {"name": "nextStart", "type": "date"}, {"name": "count", "type": "decimal"}]}
          },
          "commands": {
            "close-period": {"name": "Close Period", "aggregate": "Ledger", "fields": [{"name": "start", "type": "date"}, {"name": "count", "type": "decimal"}],
              "refuseWhen": [
                {"message": "a period starts the day after the last", "all": [{"state": "nextStart", "is": "set"}, {"command": "start", "notEquals": {"state": "nextStart"}}]}
              ]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "close-slice", "name": "Close", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "close-period", "eventIds": ["period-closed"],
             "scenarios": [
               {"id": "first", "name": "The first period starts any day", "kind": "stateChange", "given": [],
                "when": {"commandId": "close-period", "data": {"start": "2026-09-01"}}, "then": {"events": [{"eventId": "period-closed"}]}},
               {"id": "next-day", "name": "The day after the last ended", "kind": "stateChange",
                "given": [{"eventId": "period-closed", "data": {"ledgerId": "l1", "start": "2026-09-01", "nextStart": "2026-10-01"}}],
                "when": {"commandId": "close-period", "data": {"start": "2026-10-01"}}, "then": {"events": [{"eventId": "period-closed"}]}},
               {"id": "same-day-other-form", "name": "The same day, held with a time of day", "kind": "stateChange",
                "given": [{"eventId": "period-closed", "data": {"ledgerId": "l1", "start": "2026-09-01", "nextStart": "2026-10-01T00:00:00Z"}}],
                "when": {"commandId": "close-period", "data": {"start": "2026-10-01"}}, "then": {"events": [{"eventId": "period-closed"}]}},
               {"id": "a-day-late", "name": "A day late is refused", "kind": "error",
                "given": [{"eventId": "period-closed", "data": {"ledgerId": "l1", "start": "2026-09-01", "nextStart": "2026-10-01"}}],
                "when": {"commandId": "close-period", "data": {"start": "2026-10-02"}}, "then": {"error": {"message": "a period starts the day after the last"}}},
               {"id": "again", "name": "The last period again is refused", "kind": "error",
                "given": [{"eventId": "period-closed", "data": {"ledgerId": "l1", "start": "2026-09-01", "nextStart": "2026-10-01"}}],
                "when": {"commandId": "close-period", "data": {"start": "2026-09-01"}}, "then": {"error": {"message": "a period starts the day after the last"}}}
             ]}
          ]
        }
        """;

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Dates_are_equal_when_they_are_the_same_day()
    {
        var doc = DocumentLoader.Parse(DatesJson);

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Theory]
    [InlineData("""{"command": "start", "equals": "2026-10-01"}""", "\"start\" (date) with a literal")]
    [InlineData("""{"command": "start", "equals": {"state": "count"}}""", "\"start\" (date) with \"count\"")]
    public void A_date_compares_only_with_another_date(string condition, string expected)
    {
        var node = JsonNode.Parse(DatesJson)!;
        node["commands"]!["close-period"]!["refuseWhen"] = JsonNode.Parse($$"""[{"message": "m", "all": [{{condition}}]}]""");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("""{"command": "note", "lessThan": 1}""", "compares numbers")]
    [InlineData("""{"command": "rate", "atLeast": {"command": "note"}}""", "compares numbers")]
    [InlineData("""{"command": "rate", "atMost": {"state": "nosuch"}}""", "state field \"nosuch\"")]
    public void A_comparison_of_something_that_is_not_a_number_is_refused(string condition, string expected)
    {
        var node = JsonNode.Parse(ComparisonsJson)!;
        node["commands"]!["quote-rate"]!["refuseWhen"] = JsonNode.Parse($$"""[{"message": "m", "all": [{{condition}}]}]""");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("""{"state": "nosuch", "is": "set"}""", "state field \"nosuch\"")]
    [InlineData("""{"command": "nosuch", "is": "set"}""", "command field \"nosuch\"")]
    [InlineData("""{"command": "note", "contains": "x"}""", "contains")]
    [InlineData("""{"state": "owner", "notEquals": {"command": "nosuch"}}""", "command field \"nosuch\"")]
    [InlineData("""{"state": "usedTags", "contains": {"command": "nosuch"}}""", "command field \"nosuch\"")]
    [InlineData("""{"state": "usedTags", "contains": {"command": "tags"}}""", "contains looks for")]
    public void A_rule_naming_a_field_that_does_not_exist_or_cannot_be_compared_is_refused(string condition, string expected)
    {
        var node = JsonNode.Parse(OperatorsJson)!;
        node["commands"]!["touch-gate"]!["refuseWhen"] = JsonNode.Parse($$"""[{"message": "m", "all": [{{condition}}]}]""");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(expected, ex.Message);
    }
}
