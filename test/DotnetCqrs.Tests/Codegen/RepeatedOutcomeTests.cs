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
/// Schema 3.13.0 repeated events: an alternative can emit an event zero or more times, one per
/// item, in its place. How many, and what each carries, is hand-written: the generator emits a hook
/// that returns the items, and the scenario harness running the generated decider reports such a
/// scenario as a choice made by hand, accepting the expected events when they fit the alternative.
/// For example: a payroll release pays each staff member in its own event,
/// encrypted under their key, appended with the release.
/// </summary>
[Trait("Category", "Compiles")]
public class RepeatedOutcomeTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "repeated-outcomes.json"));

    private static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(string json)
    {
        var doc = DocumentLoader.Parse(json);
        return await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));
    }

    private static JsonNode Slice(JsonNode doc) => doc["slices"]![0]!;

    /// <summary>The example without PII, so a generated decider runs without a key service.</summary>
    private static string WithoutPii()
    {
        var node = JsonNode.Parse(Example)!;
        foreach (var fields in new[] { node["events"]!["work-recorded"]!, node["events"]!["person-paid"]!, node["readModels"]!["payments"]! })
            foreach (var f in fields["fields"]!.AsArray())
            {
                f!.AsObject().Remove("pii");
                f.AsObject().Remove("piiSubject");
            }
        return node.ToJsonString();
    }

    [Fact]
    public void Repeated_events_are_mapped_onto_the_alternative()
    {
        var result = DocumentMapper.Map(DocumentLoader.Parse(Example));

        var release = result.Domains.SelectMany(d => d.Commands).Single(c => c.Name == "ReleasePayRun");
        var outcome = Assert.Single(release.Outcomes);
        Assert.Equal(["PayRunReleased", "PersonPaid"], outcome.EventNames);
        Assert.Equal(["PersonPaid"], outcome.Repeated);
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("different events"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Any_number_of_repeats_in_place_is_the_declared_outcome_chosen_by_hand()
    {
        var results = await VerifyAsync(Example);

        foreach (var (id, events) in new[] { ("two-people-paid", "[PayRunReleased, PersonPaid, PersonPaid]"), ("nobody-owed", "[PayRunReleased]") })
        {
            var r = results.Single(r => r.ScenarioId == id);
            Assert.False(r.Passed, id);
            Assert.True(r.ByHand, $"{id}: {r.Detail}");
            Assert.Contains($"{events} is a declared outcome", r.Detail);
        }
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Repeats_out_of_place_are_not_the_declared_outcome()
    {
        // A payment before the release, or a second release: neither fits [released, paid*].
        var node = JsonNode.Parse(Example)!;
        var scenarios = Slice(node)["scenarios"]!.AsArray();
        var events = scenarios[0]!["then"]!["events"]!.AsArray();
        var released = events[0]!.DeepClone();
        events.RemoveAt(0);
        events.Add(released);
        var twice = scenarios[1]!["then"]!["events"]!.AsArray();
        twice.Add(twice[0]!.DeepClone());

        var results = await VerifyAsync(node.ToJsonString());

        foreach (var id in new[] { "two-people-paid", "nobody-owed" })
        {
            var r = results.Single(r => r.ScenarioId == id);
            Assert.False(r.Passed, id);
            Assert.False(r.ByHand, $"{id}: {r.Detail}");
            Assert.Contains("not a declared outcome", r.Detail);
        }
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_error_scenario_that_reaches_repeated_events_fails()
    {
        var node = JsonNode.Parse(Example)!;
        Slice(node)["scenarios"]!.AsArray().Add(JsonNode.Parse("""
            {"id": "no-release-twice", "name": "A refusal no declared rule makes", "kind": "error",
             "given": [],
             "when": {"commandId": "release-pay-run", "data": {"periodEnd": "2026-09-30"}},
             "then": {"error": {"message": "nothing to release"}}}
            """));

        var results = await VerifyAsync(node.ToJsonString());

        var r = results.Single(r => r.ScenarioId == "no-release-twice");
        Assert.False(r.Passed);
        Assert.False(r.ByHand, r.Detail);
        Assert.Contains("expected a refusal", r.Detail);
    }

    [Fact]
    public async Task The_items_are_a_hook_the_code_must_fill()
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(WithoutPii())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var (compiles, errors) = InMemoryProgram.Compile(files);
        Assert.False(compiles);
        Assert.Contains("ReleasePayRunOutcome0PersonPaid", errors);

        // The hand-written hook: here, two payments for September and none otherwise.
        var hook = new GeneratedFile("Pay_runDecider.Hand.cs", """
            using DotnetCqrs.Deciders;

            namespace Generated.Pay_run;

            public static partial class Pay_runDecider
            {
                private static partial IEnumerable<PersonPaidPayload> ReleasePayRunOutcome0PersonPaid(Pay_runState state, Command cmd) =>
                    cmd.Payload.Contains("2026-09-30")
                        ? [new PersonPaidPayload("r1", "p1", "r1:p1", null, 120), new PersonPaidPayload("r1", "p2", "r1:p2", null, 80)]
                        : [];
            }
            """);
        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Pay_run;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(Pay_runDecider.Aggregate, Pay_runDecider.Create());

            await registry.HandleAsync("pay_run", "r1", new Command("ReleasePayRun", "{\"periodEnd\":\"2026-09-30\"}"));
            await registry.HandleAsync("pay_run", "r2", new Command("ReleasePayRun", "{\"periodEnd\":\"2026-10-31\"}"));
            foreach (var id in new[] { "r1", "r2" })
                foreach (var e in await store.LoadStreamAsync("pay_run", id))
                    Console.WriteLine($"{id} {e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync([.. files, hook], programCs);

        Assert.True(success, output);
        Assert.Contains("r1 PayRunReleased", output);
        Assert.Contains("r1 PersonPaid {\"payRunId\":\"r1\",\"personId\":\"p1\",\"paymentId\":\"r1:p1\",\"periodEnd\":null,\"amount\":120}", output);
        Assert.Contains("r1 PersonPaid {\"payRunId\":\"r1\",\"personId\":\"p2\",\"paymentId\":\"r1:p2\",\"periodEnd\":null,\"amount\":80}", output);
        Assert.Contains("r2 PayRunReleased", output);
        Assert.DoesNotContain("r2 PersonPaid", output);
    }

    [Fact]
    public async Task A_repeated_pii_event_is_encrypted_under_each_items_own_subject()
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(Example)).Domains.SelectMany(CSharpGenerator.Generate).ToList();
        var hook = new GeneratedFile("Pay_runDecider.Hand.cs", """
            using DotnetCqrs.Crypto;
            using DotnetCqrs.Deciders;

            namespace Generated.Pay_run;

            public static partial class Pay_runDecider
            {
                private static partial IEnumerable<PersonPaidPayload> ReleasePayRunOutcome0PersonPaid(Pay_runState state, Command cmd) =>
                    [new PersonPaidPayload("r1", "p1", "r1:p1", null, Pii<decimal>.Fresh(120m)), new PersonPaidPayload("r1", "p2", "r1:p2", null, Pii<decimal>.Fresh(80m))];
            }
            """);
        const string programCs = """
            using DotnetCqrs.Crypto;
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Pay_run;

            var kms = new InMemoryKmsClient();
            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.RegisterDataSubjects();
            registry.Register(Pay_runDecider.Aggregate, Pay_runDecider.Create(), new Pay_runDecider.PiiProtector(kms, new SubjectStatus(store)));

            await registry.HandleAsync("pay_run", "r1", new Command("ReleasePayRun", "{\"periodEnd\":\"2026-09-30\"}"));
            foreach (var e in await store.LoadStreamAsync("pay_run", "r1"))
                Console.WriteLine($"{e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync([.. files, hook], programCs);

        Assert.True(success, output);
        Assert.DoesNotContain("\"amount\":120", output);
        Assert.Contains("\"s\":\"p1\"", output);
        Assert.Contains("\"s\":\"p2\"", output);
    }

    [Fact]
    public async Task A_hosts_own_decider_is_checked_in_full_including_the_repeats()
    {
        var doc = DocumentLoader.Parse(WithoutPii());
        static void Register(DeciderRegistry registry, DotnetCqrs.Crypto.IKmsClient _) =>
            registry.Register("pay_run", new Decider<List<(string Person, double Amount)>>
            {
                InitialState = () => [],
                Decide = (state, cmd) =>
                [
                    new NewEvent("PayRunReleased", cmd.Payload),
                    .. state.Select(w => new NewEvent("PersonPaid", JsonSerializer.Serialize(new { personId = w.Person, paymentId = "r1:" + w.Person, amount = w.Amount }))),
                ],
                Evolve = (state, ev) =>
                {
                    if (ev.Type != "WorkRecorded") return state;
                    var data = JsonDocument.Parse(ev.Data).RootElement;
                    return [.. state, (data.GetProperty("personId").GetString()!, data.GetProperty("amount").GetDouble())];
                },
            });

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), new HostDeciders(Register));

        foreach (var id in new[] { "two-people-paid", "nobody-owed" })
        {
            var r = results.Single(r => r.ScenarioId == id);
            Assert.True(r.Passed, $"{id}: {r.Detail}");
            Assert.False(r.ByHand);
        }
    }

    // ---- what the mapper refuses ----

    private static IReadOnlyList<string> MappingErrors(Action<JsonNode> change)
    {
        var node = JsonNode.Parse(Example)!;
        change(node);
        try
        {
            DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString()));
            return [];
        }
        catch (DocumentMappingException ex)
        {
            return ex.Report.Errors;
        }
    }

    [Fact]
    public void A_repeated_event_must_be_one_of_its_alternatives_events()
    {
        var errors = MappingErrors(n => Slice(n)["outcomes"]![0]!["eventIds"] = JsonNode.Parse("""["pay-run-released"]"""));
        Assert.Contains(errors, e => e.Contains("release-pay-run-slice") && e.Contains("repeated") && e.Contains("\"person-paid\"") && e.Contains("not one of this outcome's events"));
    }

    [Fact]
    public void Set_cannot_fix_values_on_a_repeated_event()
    {
        var errors = MappingErrors(n => Slice(n)["outcomes"]![0]!["set"] = JsonNode.Parse("""{"person-paid": {"personId": "p9"}}"""));
        Assert.Contains(errors, e => e.Contains("release-pay-run-slice") && e.Contains("set") && e.Contains("\"person-paid\"") && e.Contains("repeated"));
    }

    [Fact]
    public void A_repeated_event_must_carry_fields()
    {
        var errors = MappingErrors(n => n["events"]!["person-paid"]!["fields"] = new JsonArray());
        Assert.Contains(errors, e => e.Contains("release-pay-run-slice") && e.Contains("\"person-paid\"") && e.Contains("carries no fields"));
    }
}
