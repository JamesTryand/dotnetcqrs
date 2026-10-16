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
/// For example: closing a grant round awards each recipient in its own event,
/// encrypted under their key, appended with the closing.
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
        foreach (var fields in new[] { node["events"]!["application-approved"]!, node["events"]!["grant-awarded"]!, node["readModels"]!["awards"]! })
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

        var close = result.Domains.SelectMany(d => d.Commands).Single(c => c.Name == "CloseGrantRound");
        var outcome = Assert.Single(close.Outcomes);
        Assert.Equal(["GrantRoundClosed", "GrantAwarded"], outcome.EventNames);
        Assert.Equal(["GrantAwarded"], outcome.Repeated);
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("different events"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Any_number_of_repeats_in_place_is_the_declared_outcome_chosen_by_hand()
    {
        var results = await VerifyAsync(Example);

        foreach (var (id, events) in new[] { ("two-recipients-awarded", "[GrantRoundClosed, GrantAwarded, GrantAwarded]"), ("no-applications-approved", "[GrantRoundClosed]") })
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
        // An award before the closing, or a second closing: neither fits [closed, awarded*].
        var node = JsonNode.Parse(Example)!;
        var scenarios = Slice(node)["scenarios"]!.AsArray();
        var events = scenarios[0]!["then"]!["events"]!.AsArray();
        var released = events[0]!.DeepClone();
        events.RemoveAt(0);
        events.Add(released);
        var twice = scenarios[1]!["then"]!["events"]!.AsArray();
        twice.Add(twice[0]!.DeepClone());

        var results = await VerifyAsync(node.ToJsonString());

        foreach (var id in new[] { "two-recipients-awarded", "no-applications-approved" })
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
            {"id": "no-close-twice", "name": "A refusal no declared rule makes", "kind": "error",
             "given": [],
             "when": {"commandId": "close-grant-round", "data": {"closingDate": "2026-09-30"}},
             "then": {"error": {"message": "nothing to close"}}}
            """));

        var results = await VerifyAsync(node.ToJsonString());

        var r = results.Single(r => r.ScenarioId == "no-close-twice");
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
        Assert.Contains("CloseGrantRoundOutcome0GrantAwarded", errors);

        // The hand-written hook: here, two awards for September and none otherwise.
        var hook = new GeneratedFile("Grant_roundDecider.Hand.cs", """
            using DotnetCqrs.Deciders;

            namespace Generated.Grant_round;

            public static partial class Grant_roundDecider
            {
                private static partial IEnumerable<GrantAwardedPayload> CloseGrantRoundOutcome0GrantAwarded(Grant_roundState state, Command cmd) =>
                    cmd.Payload.Contains("2026-09-30")
                        ? [new GrantAwardedPayload("g1", "r1", "g1:r1", null, 1200), new GrantAwardedPayload("g1", "r2", "g1:r2", null, 800)]
                        : [];
            }
            """);
        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Grant_round;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(Grant_roundDecider.Aggregate, Grant_roundDecider.Create());

            await registry.HandleAsync("grant_round", "g1", new Command("CloseGrantRound", "{\"closingDate\":\"2026-09-30\"}"));
            await registry.HandleAsync("grant_round", "g2", new Command("CloseGrantRound", "{\"closingDate\":\"2026-10-31\"}"));
            foreach (var id in new[] { "g1", "g2" })
                foreach (var e in await store.LoadStreamAsync("grant_round", id))
                    Console.WriteLine($"{id} {e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync([.. files, hook], programCs);

        Assert.True(success, output);
        Assert.Contains("g1 GrantRoundClosed", output);
        Assert.Contains("g1 GrantAwarded {\"grantRoundId\":\"g1\",\"recipientId\":\"r1\",\"awardId\":\"g1:r1\",\"closingDate\":null,\"amount\":1200}", output);
        Assert.Contains("g1 GrantAwarded {\"grantRoundId\":\"g1\",\"recipientId\":\"r2\",\"awardId\":\"g1:r2\",\"closingDate\":null,\"amount\":800}", output);
        Assert.Contains("g2 GrantRoundClosed", output);
        Assert.DoesNotContain("g2 GrantAwarded", output);
    }

    [Fact]
    public async Task A_repeated_pii_event_is_encrypted_under_each_items_own_subject()
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(Example)).Domains.SelectMany(CSharpGenerator.Generate).ToList();
        var hook = new GeneratedFile("Grant_roundDecider.Hand.cs", """
            using DotnetCqrs.Crypto;
            using DotnetCqrs.Deciders;

            namespace Generated.Grant_round;

            public static partial class Grant_roundDecider
            {
                private static partial IEnumerable<GrantAwardedPayload> CloseGrantRoundOutcome0GrantAwarded(Grant_roundState state, Command cmd) =>
                    [new GrantAwardedPayload("g1", "r1", "g1:r1", null, Pii<decimal>.Fresh(1200m)), new GrantAwardedPayload("g1", "r2", "g1:r2", null, Pii<decimal>.Fresh(800m))];
            }
            """);
        const string programCs = """
            using DotnetCqrs.Crypto;
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Grant_round;

            var kms = new InMemoryKmsClient();
            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.RegisterDataSubjects();
            registry.Register(Grant_roundDecider.Aggregate, Grant_roundDecider.Create(), new Grant_roundDecider.PiiProtector(kms, new SubjectStatus(store)));

            await registry.HandleAsync("grant_round", "g1", new Command("CloseGrantRound", "{\"closingDate\":\"2026-09-30\"}"));
            foreach (var e in await store.LoadStreamAsync("grant_round", "g1"))
                Console.WriteLine($"{e.Type} {e.Data}");
            return 0;
            """;

        var (success, output) = await InMemoryProgram.RunAsync([.. files, hook], programCs);

        Assert.True(success, output);
        Assert.DoesNotContain("\"amount\":1200", output);
        Assert.Contains("\"s\":\"r1\"", output);
        Assert.Contains("\"s\":\"r2\"", output);
    }

    [Fact]
    public async Task A_hosts_own_decider_is_checked_in_full_including_the_repeats()
    {
        var doc = DocumentLoader.Parse(WithoutPii());
        static void Register(DeciderRegistry registry, DotnetCqrs.Crypto.IKmsClient _) =>
            registry.Register("grant_round", new Decider<List<(string Recipient, double Amount)>>
            {
                InitialState = () => [],
                Decide = (state, cmd) =>
                [
                    new NewEvent("GrantRoundClosed", cmd.Payload),
                    .. state.Select(w => new NewEvent("GrantAwarded", JsonSerializer.Serialize(new { recipientId = w.Recipient, awardId = "g1:" + w.Recipient, amount = w.Amount }))),
                ],
                Evolve = (state, ev) =>
                {
                    if (ev.Type != "ApplicationApproved") return state;
                    var data = JsonDocument.Parse(ev.Data).RootElement;
                    return [.. state, (data.GetProperty("recipientId").GetString()!, data.GetProperty("amount").GetDouble())];
                },
            });

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), new HostDeciders(Register));

        foreach (var id in new[] { "two-recipients-awarded", "no-applications-approved" })
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
        var errors = MappingErrors(n => Slice(n)["outcomes"]![0]!["eventIds"] = JsonNode.Parse("""["grant-round-closed"]"""));
        Assert.Contains(errors, e => e.Contains("close-grant-round-slice") && e.Contains("repeated") && e.Contains("\"grant-awarded\"") && e.Contains("not one of this outcome's events"));
    }

    [Fact]
    public void Set_cannot_fix_values_on_a_repeated_event()
    {
        var errors = MappingErrors(n => Slice(n)["outcomes"]![0]!["set"] = JsonNode.Parse("""{"grant-awarded": {"recipientId": "r9"}}"""));
        Assert.Contains(errors, e => e.Contains("close-grant-round-slice") && e.Contains("set") && e.Contains("\"grant-awarded\"") && e.Contains("repeated"));
    }

    [Fact]
    public void A_repeated_event_must_carry_fields()
    {
        var errors = MappingErrors(n => n["events"]!["grant-awarded"]!["fields"] = new JsonArray());
        Assert.Contains(errors, e => e.Contains("close-grant-round-slice") && e.Contains("\"grant-awarded\"") && e.Contains("carries no fields"));
    }
}
