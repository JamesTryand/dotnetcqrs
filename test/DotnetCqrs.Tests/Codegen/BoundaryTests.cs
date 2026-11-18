using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// eventmodelschema 6.0.0: a document names messages, never their sources, so the boundary parts that remain are
/// read and only their model is generated: a stateChange started by an ingress, an automation that waits (delay), one
/// that calls out (effect, its answer and give-up), and an output. No reaction is generated for the delay or the effect,
/// nothing for the output. Upstream's order-fulfillment example, without its partitioning (still refused).
/// </summary>
[Trait("Category", "Compiles")]
public class BoundaryTests
{
    private static JsonNode Example()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "upstream-order-fulfillment.json")))!;
        node.AsObject().Remove("partitioning");
        foreach (var slice in node["slices"]!.AsArray())
        {
            // the example's 3.3.0 outcomes don't say which applies; the ingress slice gets a real condition
            if ((string?)slice!["id"] == "record-delivery-slice")
                slice["outcomes"] = JsonNode.Parse("""
                    [{"eventIds": ["order-delivered"], "when": [{"all": [{"command": "delivered", "equals": true}]}]},
                     {"eventIds": ["delivery-failed"]}]
                    """);
            if ((string?)slice["id"] == "auto-ship-slice")
                slice.AsObject().Remove("outcomes");
        }
        return node;
    }

    private static readonly MappingOptions Overrides = new()
    {
        AggregateOverrides = new Dictionary<string, string>
        {
            ["record-partner-notified"] = "ShipmentNotice",
            ["record-partner-notification-abandoned"] = "ShipmentNotice",
        },
    };

    [Fact(Timeout = 120000)]
    public async Task The_boundary_parts_are_read_and_only_their_model_is_generated()
    {
        var doc = DocumentLoader.Parse(Example().ToJsonString());
        var mapped = DocumentMapper.Map(doc, Overrides);

        var commands = mapped.Domains.SelectMany(d => d.Commands).Select(c => c.Name).ToHashSet();
        foreach (var c in new[] { "RecordDelivery", "FlagDeliveryOverdue", "RecordPartnerNotified", "RecordPartnerNotificationAbandoned" })
            Assert.Contains(c, commands);
        Assert.Equal(["autoShipPendingOrders"], mapped.Domains.SelectMany(d => d.Reactors).Select(r => r.Name).ToList());
        var notes = string.Join("\n", mapped.Report.Warnings);
        Assert.Contains("automation \"delivery-overdue-slice\" waits before sending", notes);
        Assert.Contains("automation \"notify-shipping-partner-slice\" calls out", notes);
        Assert.Contains("output \"order-confirmation\" leaves after order-placed: nothing is generated", notes);

        // the ingress, the delay and the effect (answer and give-up); one scenario expects a refusal the example
        // never declares (no refuseWhen), so no generator can pass it
        var boundary = new HashSet<string> { "record-delivery-slice", "delivery-overdue-slice", "notify-shipping-partner-slice" };
        var results = (await ScenarioVerifier.VerifyAsync(doc, mapped))
            .Where(r => boundary.Contains(r.SliceId) && r.ScenarioId != "delivery-before-shipment-rejected").ToList();
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.SliceId}/{r.ScenarioId}: {r.Detail}"));
    }

    [Fact]
    public void An_outputs_fields_must_be_on_its_triggers()
    {
        var node = Example();
        node["outputs"]!["order-confirmation"]!["fields"] = JsonNode.Parse("""["orderId", "phone"]""");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, Overrides));

        Assert.Contains(ex.Report.Errors, e => e.Contains("output \"order-confirmation\": field \"phone\", which it says leaves, is not a field of trigger event \"order-placed\""));
    }
}
