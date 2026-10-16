using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.12.0 conditional outcomes: a command emits one set of events or another, and the
/// model says when. A declared <c>when</c> is generated into Decide, after the refusals; a
/// <c>"hand"</c> one is a hook the code must fill, and the scenario harness running the generated
/// decider reports a scenario that depends on it as a choice made by hand, never as a pass.
/// For example: a hire of more than 14 days is booked and
/// flagged in one decision.
/// </summary>
[Trait("Category", "Compiles")]
public class OutcomeTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static string Example => File.ReadAllText(TestDataPath("conditional-outcomes.json"));

    private static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(string json)
    {
        var doc = DocumentLoader.Parse(json);
        return await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));
    }

    private static JsonNode Slice(JsonNode doc, string id) =>
        doc["slices"]!.AsArray().Single(s => (string?)s!["id"] == id)!;

    [Fact]
    public void Outcomes_are_mapped_onto_the_command_in_order()
    {
        var doc = DocumentLoader.Parse(Example);

        var result = DocumentMapper.Map(doc);
        var domains = result.Domains;

        // Nothing is left to guess, so no "emits the first event" warning.
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("different events"));

        var log = domains.SelectMany(d => d.Commands).Single(c => c.Name == "MakeBooking");
        Assert.Equal(2, log.Outcomes.Count);
        Assert.Equal(["BookingMade", "BookingFlagged"], log.Outcomes[0].EventNames);
        var group = Assert.Single(log.Outcomes[0].When!);
        Assert.Equal("greaterThan", Assert.Single(group).Operator);
        Assert.False(log.Outcomes[0].ByHand);
        Assert.Equal("long-hire", log.Outcomes[0].Set["BookingFlagged"]["reason"]);
        Assert.Equal(["BookingMade"], log.Outcomes[1].EventNames);
        Assert.Null(log.Outcomes[1].When);

        var submit = domains.SelectMany(d => d.Commands).Single(c => c.Name == "RequestRefund");
        Assert.True(submit.Outcomes[0].ByHand);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_declared_outcomes_and_reports_the_choice_made_by_hand()
    {
        var results = await VerifyAsync(Example);

        Assert.Equal(4, results.Count);
        foreach (var id in new[] { "make-a-booking", "long-hire-is-flagged", "refused-before-outcomes" })
        {
            var r = results.Single(r => r.ScenarioId == id);
            Assert.True(r.Passed, $"{id}: {r.Detail}");
            Assert.False(r.ByHand, id);
        }
        var hand = results.Single(r => r.ScenarioId == "refund-over-threshold");
        Assert.False(hand.Passed);
        Assert.True(hand.ByHand, hand.Detail);
        Assert.Contains("[RefundRequested, RefundFlagged] is a declared outcome", hand.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_declared_condition_the_two_event_scenario_fails()
    {
        // The scenarios must be able to fail: with the condition moved out of reach, a 15-day
        // hire is booked alone and the scenario expecting the flag fails.
        var node = JsonNode.Parse(Example)!;
        Slice(node, "make-booking-slice")["outcomes"]![0]!["when"]![0]!["all"]![0]!["greaterThan"] = 100;

        var results = await VerifyAsync(node.ToJsonString());

        var flagged = results.Single(r => r.ScenarioId == "long-hire-is-flagged");
        Assert.False(flagged.Passed);
        Assert.False(flagged.ByHand);
        Assert.Contains("expected [BookingMade, BookingFlagged], got [BookingMade]", flagged.Detail);
        Assert.True(results.Single(r => r.ScenarioId == "make-a-booking").Passed);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_condition_holds_when_any_group_holds_and_a_group_when_all_its_conditions_do()
    {
        // More than 14 days, or from 1 up to (not including) 2: 15 and 1.5 are flagged; 8 meets
        // only half of the second group, 0.5 the other half, so neither is.
        var node = JsonNode.Parse(Example)!;
        var slice = Slice(node, "make-booking-slice");
        slice["outcomes"]![0]!["when"] = JsonNode.Parse("""
            [{"all": [{"command": "days", "greaterThan": 14}]},
             {"all": [{"command": "days", "atLeast": 1}, {"command": "days", "lessThan": 2}]}]
            """);
        var scenarios = slice["scenarios"]!.AsArray();
        foreach (var (id, days, flagged) in new[] { ("d15", 15.0, true), ("d1-5", 1.5, true), ("d8", 8.0, false), ("d0-5", 0.5, false) })
        {
            var events = new JsonArray(new JsonObject { ["eventId"] = "booking-made" });
            if (flagged) events.Add(new JsonObject { ["eventId"] = "booking-flagged" });
            scenarios.Add(new JsonObject
            {
                ["id"] = id, ["name"] = id, ["kind"] = "stateChange", ["given"] = new JsonArray(),
                ["when"] = new JsonObject { ["commandId"] = "make-booking", ["data"] = new JsonObject { ["days"] = days } },
                ["then"] = new JsonObject { ["events"] = events },
            });
        }

        var results = await VerifyAsync(node.ToJsonString());

        foreach (var id in new[] { "d15", "d1-5", "d8", "d0-5" })
        {
            var r = results.Single(r => r.ScenarioId == id);
            Assert.True(r.Passed, $"{id}: {r.Detail}");
        }
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_choice_made_by_hand_whose_events_are_not_a_declared_outcome_fails()
    {
        var node = JsonNode.Parse(Example)!;
        var scenario = Slice(node, "request-refund-slice")["scenarios"]![0]!;
        scenario["then"]!["events"]!.AsArray().RemoveAt(0); // the flag alone: no alternative says that

        var results = await VerifyAsync(node.ToJsonString());

        var r = results.Single(r => r.ScenarioId == "refund-over-threshold");
        Assert.False(r.Passed);
        Assert.False(r.ByHand, r.Detail);
        Assert.Contains("not a declared outcome", r.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_error_scenario_that_reaches_a_choice_made_by_hand_fails()
    {
        // Refusals come before outcomes, so a scenario expecting a refusal that no declared rule
        // makes reaches the choice: the command was accepted, and that is a failure.
        var node = JsonNode.Parse(Example)!;
        Slice(node, "request-refund-slice")["scenarios"]!.AsArray().Add(JsonNode.Parse("""
            {"id": "no-negative-refund", "name": "A negative refund is refused", "kind": "error",
             "given": [], "when": {"commandId": "request-refund", "data": {"amount": -5, "currency": "GBP"}},
             "then": {"error": {"message": "a refund is more than 0"}}}
            """));

        var results = await VerifyAsync(node.ToJsonString());

        var r = results.Single(r => r.ScenarioId == "no-negative-refund");
        Assert.False(r.Passed);
        Assert.False(r.ByHand, r.Detail);
        Assert.Contains("expected a refusal", r.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_report_prints_a_choice_made_by_hand_as_HAND_and_it_does_not_fail_the_run()
    {
        var results = await VerifyAsync(Example);
        var writer = new StringWriter();

        var exit = ScenarioReport.Print(results, writer);

        Assert.Equal(0, exit);
        Assert.Contains("HAND [stateChange] request-refund-slice/refund-over-threshold", writer.ToString());
        Assert.Contains("PASS [stateChange] make-booking-slice/long-hire-is-flagged", writer.ToString());
    }

    [Fact]
    public async Task Set_fixes_a_value_on_the_alternatives_event()
    {
        var doc = DocumentLoader.Parse(Example);
        var files = DocumentMapper.Map(doc).Domains.Where(d => d.Aggregate == "booking").SelectMany(CSharpGenerator.Generate).ToList();

        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Booking;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(BookingDecider.Aggregate, BookingDecider.Create());

            await registry.HandleAsync("booking", "b1", new Command("MakeBooking", "{\"days\":15}"));
            await registry.HandleAsync("booking", "b2", new Command("MakeBooking", "{\"days\":7}"));
            foreach (var id in new[] { "b1", "b2" })
                foreach (var e in await store.LoadStreamAsync("booking", id))
                    Console.WriteLine($"{id} {e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync(files, programCs);

        Assert.True(success, output);
        Assert.Contains("b1 BookingMade", output);
        Assert.Contains("b1 BookingFlagged {\"bookingId\":null,\"reason\":\"long-hire\"}", output);
        Assert.Contains("b2 BookingMade", output);
        Assert.DoesNotContain("b2 BookingFlagged", output);
    }

    [Fact]
    public async Task A_choice_made_by_hand_is_a_hook_the_code_must_fill()
    {
        var doc = DocumentLoader.Parse(Example);
        var files = DocumentMapper.Map(doc).Domains.Where(d => d.Aggregate == "refund").SelectMany(CSharpGenerator.Generate).ToList();

        // Generated alone, it does not compile: the hook has no body.
        var (compiles, errors) = InMemoryProgram.Compile(files);
        Assert.False(compiles);
        Assert.Contains("RequestRefundOutcome0", errors);

        // With the hand-written condition, the decider chooses by it.
        var hook = new GeneratedFile("RefundDecider.Hand.cs", """
            using System.Text.Json;
            using DotnetCqrs.Deciders;

            namespace Generated.Refund;

            public static partial class RefundDecider
            {
                private static partial bool RequestRefundOutcome0(RefundState state, Command cmd) =>
                    JsonDocument.Parse(cmd.Payload).RootElement.GetProperty("amount").GetDouble() > 500;
            }
            """);
        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Refund;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(RefundDecider.Aggregate, RefundDecider.Create());

            await registry.HandleAsync("refund", "r1", new Command("RequestRefund", "{\"amount\":900,\"currency\":\"USD\"}"));
            await registry.HandleAsync("refund", "r2", new Command("RequestRefund", "{\"amount\":90,\"currency\":\"USD\"}"));
            foreach (var id in new[] { "r1", "r2" })
                foreach (var e in await store.LoadStreamAsync("refund", id))
                    Console.WriteLine($"{id} {e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync([.. files, hook], programCs);

        Assert.True(success, output);
        Assert.Contains("r1 RefundFlagged {\"refundId\":null,\"reason\":\"over-threshold\"}", output);
        Assert.Contains("r2 RefundRequested", output);
        Assert.DoesNotContain("r2 RefundFlagged", output);
    }

    [Fact]
    public async Task A_hosts_own_decider_is_checked_in_full_including_the_choice()
    {
        // Against a host's deciders nothing is left to hand: the choice and the values are checked.
        var doc = DocumentLoader.Parse(Example);
        static void Register(DeciderRegistry registry, DotnetCqrs.Crypto.IKmsClient _) =>
            registry.Register("refund", new Decider<bool>
            {
                InitialState = () => false,
                Decide = (_, cmd) =>
                {
                    var amount = JsonDocument.Parse(cmd.Payload).RootElement.GetProperty("amount").GetDouble();
                    var submitted = new NewEvent("RefundRequested", JsonSerializer.Serialize(new { amount }));
                    return amount > 500
                        ? [submitted, new NewEvent("RefundFlagged", """{"reason":"over-threshold"}""")]
                        : [submitted];
                },
                Evolve = (_, _) => true,
            });

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), new HostDeciders(Register));

        var r = results.Single(r => r.ScenarioId == "refund-over-threshold");
        Assert.True(r.Passed, r.Detail);
        Assert.False(r.ByHand);
    }

    // ---- what the mapper refuses ----

    private static IReadOnlyList<string> MappingErrors(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Example)!;
        change(Slice(node, "make-booking-slice"));
        var doc = DocumentLoader.Parse(node.ToJsonString());
        try
        {
            DocumentMapper.Map(doc);
            return [];
        }
        catch (DocumentMappingException ex)
        {
            return ex.Report.Errors;
        }
    }

    [Fact]
    public void The_last_alternative_must_have_no_condition()
    {
        var errors = MappingErrors(s => s["outcomes"]![1]!["when"] = "hand");
        Assert.Contains(errors, e => e.Contains("make-booking-slice") && e.Contains("the last outcome") && e.Contains("no when"));
    }

    [Fact]
    public void Every_alternative_but_the_last_must_have_a_condition()
    {
        var errors = MappingErrors(s => s["outcomes"]!.AsArray().Insert(1, JsonNode.Parse("""{"eventIds": ["booking-made"]}""")));
        Assert.Contains(errors, e => e.Contains("make-booking-slice") && e.Contains("outcome 1") && e.Contains("needs a when"));
    }

    [Fact]
    public void Alternatives_without_conditions_are_still_refused()
    {
        // 3.3.0's form says which alternatives exist, not which applies: nothing to generate.
        var errors = MappingErrors(s => s["outcomes"] = JsonNode.Parse("""[["booking-made", "booking-flagged"], ["booking-made"]]"""));
        Assert.Contains(errors, e => e.Contains("make-booking-slice") && e.Contains("say when each applies"));
    }

    [Fact]
    public void A_single_alternative_without_a_condition_emits_all_its_events()
    {
        var node = JsonNode.Parse(Example)!;
        Slice(node, "make-booking-slice")["outcomes"] = JsonNode.Parse("""[["booking-made", "booking-flagged"]]""");
        var result = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString()));

        Assert.Empty(result.Report.Errors);
        var log = result.Domains.SelectMany(d => d.Commands).Single(c => c.Name == "MakeBooking");
        Assert.Equal(["BookingMade", "BookingFlagged"], Assert.Single(log.Outcomes).EventNames);
    }

    [Fact]
    public void An_alternatives_events_must_be_the_slices_own()
    {
        var errors = MappingErrors(s => s["outcomes"]![1]!["eventIds"] = JsonNode.Parse("""["refund-requested"]"""));
        Assert.Contains(errors, e => e.Contains("make-booking-slice") && e.Contains("\"refund-requested\"") && e.Contains("eventIds"));
    }

    [Fact]
    public void Set_must_name_an_event_of_its_alternative_and_a_field_that_event_carries()
    {
        var notInAlternative = MappingErrors(s => s["outcomes"]![1]!["set"] = JsonNode.Parse("""{"booking-flagged": {"reason": "x"}}"""));
        Assert.Contains(notInAlternative, e => e.Contains("set") && e.Contains("\"booking-flagged\"") && e.Contains("not one of this outcome's events"));

        var noSuchField = MappingErrors(s => s["outcomes"]![0]!["set"] = JsonNode.Parse("""{"booking-flagged": {"severity": "x"}}"""));
        Assert.Contains(noSuchField, e => e.Contains("set") && e.Contains("\"severity\"") && e.Contains("does not carry"));
    }

    [Fact]
    public void A_condition_names_fields_that_exist()
    {
        var errors = MappingErrors(s => s["outcomes"]![0]!["when"]![0]!["all"]![0]!["command"] = "minutes");
        Assert.Contains(errors, e => e.Contains("MakeBooking") && e.Contains("\"minutes\"") && e.Contains("does not declare"));
    }
}
