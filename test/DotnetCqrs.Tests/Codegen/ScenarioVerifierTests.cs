using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

public class ScenarioVerifierTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    /// <summary>Walks up from the test's own output directory to find the repo root
    /// (marked by dotnetcqrs.slnx) -- same approach as CSharpGeneratorTests.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private static string DotnetCqrsProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj");

    [Fact(Timeout = 60000)]
    public async Task Verifies_every_scenario_kind_in_the_order_fulfillment_document()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        // order-fulfillment.json declares exactly one scenario per slice (4 slices):
        // place-order (stateChange), order-status (stateView), auto-ship
        // (stateChange, same-aggregate automation), notify-shipping-partner
        // (stateChange, cross-aggregate automation).
        Assert.Equal(4, results.Count);

        var placeOrder = results.Single(r => r.ScenarioId == "place-order-happy-path");
        Assert.Equal("stateChange", placeOrder.Kind);
        Assert.True(placeOrder.Passed, placeOrder.Detail);

        // EXPECTED to fail, not a verifier or generator bug: "status" is a derived
        // value ("placed" once order-placed happened, "shipped" once order-shipped
        // does) that neither event's own JSON payload literally carries -- the
        // generic field-merge projection (see ProjectionGenerator's own doc comment)
        // can only copy fields present in an event's data, and computing "status"
        // from *which event type fired* is exactly the kind of real per-event rule
        // "the author's job" (Milestone 3's own framing) means. The verifier correctly
        // catching this, rather than silently passing, is the whole point of this
        // milestone -- so this scenario failing IS the passing assertion for the test.
        var orderStatus = results.Single(r => r.ScenarioId == "view-order-summary-after-placement");
        Assert.Equal("stateView", orderStatus.Kind);
        Assert.False(orderStatus.Passed);
        Assert.Contains("status", orderStatus.Detail);

        var autoShip = results.Single(r => r.ScenarioId == "auto-ship-triggers-on-order-placed");
        Assert.Equal("stateChange", autoShip.Kind);
        Assert.True(autoShip.Passed, autoShip.Detail);

        // this is the cross-aggregate one: given (order-shipped) belongs to "order",
        // the dispatched command (notify-shipping-partner) belongs to
        // "shippingNotification" -- passing here proves SplitGiven's own/foreign
        // split and the aggregate-resolution plumbing are both correct end to end.
        var notify = results.Single(r => r.ScenarioId == "notify-partner-on-shipment");
        Assert.Equal("stateChange", notify.Kind);
        Assert.True(notify.Passed, notify.Detail);
    }

    [Fact(Timeout = 60000)]
    public async Task An_error_scenario_that_should_be_rejected_is_reported_as_passed()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "error-test", "name": "Error Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [
                  {
                    "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                    "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                  },
                  {
                    "id": "duplicate-rejected", "name": "A second PlaceOrder is refused", "kind": "error",
                    "given": [{"eventId": "order-placed"}], "when": {"commandId": "place-order"},
                    "then": {"error": {"message": "order already exists"}}
                  }
                ]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));

        var errorScenario = results.Single(r => r.ScenarioId == "duplicate-rejected");
        Assert.Equal("error", errorScenario.Kind);
        Assert.Contains("refused", errorScenario.Detail);
    }

    [Fact(Timeout = 60000)]
    public async Task A_scenario_whose_read_model_was_skipped_during_mapping_is_reported_skipped_not_failed()
    {
        // A read model with no builtFromEventIds is a WARNING during mapping (the
        // generated projection would never fire, so the mapper skips it) rather than
        // an ERROR -- unlike an untagged command (always a hard mapping failure, so
        // DocumentMapper.Map never returns a MappingResult for ScenarioVerifier to see
        // in that case at all). This is the genuinely reachable "referenced but not
        // generated" path: mapping succeeds, but "empty-rm" is absent from the
        // resulting Domain, so the scenario referencing it must be Skipped, not
        // silently treated as a failure it never had a chance to pass.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "skip-test", "name": "Skip Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order", "aggregate": "Order"}},
              "readModels": {"empty-rm": {"name": "Empty RM"}},
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "place-order", "eventIds": ["order-placed"],
                  "scenarios": []
                },
                {
                  "id": "view-slice", "name": "View", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "empty-rm",
                  "scenarios": [{
                    "id": "view-scenario", "name": "Should be skipped", "kind": "stateView",
                    "given": [], "when": {"readModelId": "empty-rm"}, "then": {"result": {}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc); // succeeds -- the empty read model is a warning, not an error
        Assert.Contains(mapped.Report.Warnings, w => w.Contains("empty-rm") && w.Contains("skipped"));
        Assert.Empty(mapped.Domains.Single().ReadModels); // confirms it's genuinely absent, not just under-tested

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.False(result.Passed);
        Assert.Contains("empty-rm", result.Detail);
    }
}
