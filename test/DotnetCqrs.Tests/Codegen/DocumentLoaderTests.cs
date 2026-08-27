using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Model;

namespace DotnetCqrs.Tests.Codegen;

public class DocumentLoaderTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    [Fact]
    public void Loads_the_minimal_valid_document()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("minimal.json"));

        Assert.Equal("minimal-example", doc.Id);
        Assert.Equal("2.0.0", doc.EventModelingSchemaVersion);
        Assert.Single(doc.Swimlanes);
        Assert.Equal("sales", doc.Swimlanes[0].Id);
        Assert.Single(doc.Slices);

        var slice = Assert.IsType<StateChangeSlice>(doc.Slices[0]);
        Assert.Equal("place-order", slice.CommandId);
        Assert.Equal(["order-placed"], slice.EventIds);
        Assert.Empty(slice.Scenarios);
    }

    [Fact]
    public void Loads_the_order_fulfillment_document_with_all_three_slice_patterns()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        Assert.Equal("order-fulfillment", doc.Id);
        Assert.Equal(3, doc.Swimlanes.Count);
        Assert.Equal(4, doc.Slices.Count);

        var stateChange = Assert.IsType<StateChangeSlice>(doc.Slices.Single(s => s.Id == "place-order-slice"));
        Assert.Equal("place-order", stateChange.CommandId);
        Assert.Equal("place-order-screen", stateChange.ScreenId);

        var stateView = Assert.IsType<StateViewSlice>(doc.Slices.Single(s => s.Id == "order-status-slice"));
        Assert.Equal("order-summary", stateView.ReadModelId);

        var automation = Assert.IsType<AutomationSlice>(doc.Slices.Single(s => s.Id == "auto-ship-slice"));
        Assert.Equal(["order-placed"], automation.TriggerEventIds);
        Assert.Equal("pending-shipments", automation.ReadModelId);

        // an automation slice with no read model (the "Bridge" pattern the document's
        // own description calls out)
        var bridge = Assert.IsType<AutomationSlice>(doc.Slices.Single(s => s.Id == "notify-shipping-partner-slice"));
        Assert.Null(bridge.ReadModelId);
    }

    [Fact]
    public void Parses_all_three_scenario_kinds()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        var stateChangeScenario = Assert.IsType<StateChangeScenario>(doc.Slices.Single(s => s.Id == "place-order-slice").Scenarios.Single());
        Assert.Equal("place-order", stateChangeScenario.When.CommandId);
        Assert.Equal(["order-placed"], stateChangeScenario.Then.Events.Select(e => e.EventId));

        var stateViewScenario = Assert.IsType<StateViewScenario>(doc.Slices.Single(s => s.Id == "order-status-slice").Scenarios.Single());
        Assert.Equal("order-summary", stateViewScenario.When.ReadModelId);
    }

    [Fact]
    public void Parses_field_definitions_including_nested_subfields()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        var orderPlaced = doc.Events!["order-placed"];
        var items = orderPlaced.Fields!.Single(f => f.Name == "items");
        Assert.Equal("custom", items.Type);
        Assert.Equal("list", items.Cardinality);
        Assert.NotNull(items.Subfields);
        Assert.Contains(items.Subfields!, f => f.Name == "sku" && f.Type == "string");
    }

    [Fact]
    public void Parses_a_hotspot_with_an_ordering_target()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        var hotspot = doc.Hotspots!["unclear-status-timing"];
        var target = Assert.IsType<OrderingHotspotTarget>(hotspot.Target);
        Assert.Equal(["order-status-slice", "auto-ship-slice"], target.SliceIds);
        Assert.False(hotspot.Resolved);
    }

    [Fact]
    public void Rejects_a_document_missing_a_required_top_level_field()
    {
        const string json = """{"eventModelingSchemaVersion":"2.0.0","id":"x","name":"X"}"""; // missing swimlanes, slices

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Rejects_a_slice_missing_the_field_its_own_pattern_requires()
    {
        // pattern: stateChange requires screenId/commandId/eventIds -- none present
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "x", "name": "X",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "slices": [{"id":"sl","name":"SL","pattern":"stateChange","swimlaneId":"s","status":"created","scenarios":[]}]
            }
            """;

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Rejects_an_invalid_enum_value()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "x", "name": "X",
              "swimlanes": [{"id":"s","name":"S","kind":"not-a-real-kind"}],
              "slices": []
            }
            """;

        Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
    }

    [Fact]
    public void Rejects_an_id_that_is_not_kebab_case()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "Not Kebab Case!", "name": "X",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "slices": []
            }
            """;

        Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
    }
}
