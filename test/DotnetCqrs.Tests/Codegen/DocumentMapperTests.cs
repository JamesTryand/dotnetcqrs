using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

public class DocumentMapperTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    [Fact]
    public void Maps_the_minimal_document_to_one_aggregate_with_one_create_command()
    {
        // minimal.json's "place-order" command, like order-fulfillment.json's
        // "notify-shipping-partner", carries no `aggregate` tag -- an override is
        // needed either way, exercising the same resolution path as the richer test
        // below but on the smallest possible document.
        var doc = DocumentLoader.LoadFromFile(TestDataPath("minimal.json"));
        var options = new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        };

        var result = DocumentMapper.Map(doc, options);

        var order = Assert.Single(result.Domains);
        Assert.Equal("order", order.Aggregate);
        var command = Assert.Single(order.Commands);
        Assert.Equal("PlaceOrder", command.Name);
        // minimal.json declares an empty `scenarios: []` -- IsCreate's only real
        // signal is an explicit scenario with an empty `given`, so with none present
        // this command is correctly NOT treated as the create (see the richer
        // order-fulfillment.json test below, which does supply that scenario).
        Assert.False(command.Once);
        Assert.True(command.RequiresExisting);
    }

    [Fact]
    public void An_untagged_command_with_no_override_fails_mapping_with_a_clear_reason()
    {
        // order-fulfillment.json's "notify-shipping-partner" command deliberately
        // carries no `aggregate` tag (the worked example's own point: a
        // boundary-crossing integration with nothing to own it by default).
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("notify-shipping-partner") && e.Contains("no `aggregate` tag"));
    }

    [Fact]
    public void An_aggregate_override_resolves_the_untagged_command()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var options = new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        };

        var result = DocumentMapper.Map(doc, options);

        Assert.Contains(result.Domains, d => d.Aggregate == "order");
        Assert.Contains(result.Domains, d => d.Aggregate == "shippingNotification");
        Assert.Contains(result.Report.Warnings, w => w.Contains("notify-shipping-partner") && w.Contains("override"));
    }

    private static MappingResult MapOrderFulfillment() =>
        DocumentMapper.Map(
            DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            new MappingOptions { AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" } });

    [Fact]
    public void The_order_aggregate_gets_both_its_own_commands()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        Assert.Equal(["PlaceOrder", "ShipOrder"], order.Commands.Select(c => c.Name));
    }

    [Fact]
    public void PlaceOrder_is_the_create_and_ShipOrder_requires_an_existing_stream()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var placeOrder = order.Commands.Single(c => c.Name == "PlaceOrder");
        Assert.True(placeOrder.Once);
        Assert.False(placeOrder.RequiresExisting);

        var shipOrder = order.Commands.Single(c => c.Name == "ShipOrder");
        Assert.False(shipOrder.Once);
        Assert.True(shipOrder.RequiresExisting);
    }

    [Fact]
    public void The_same_aggregate_automation_reactor_lands_on_the_order_domain()
    {
        // auto-ship-pending-orders: triggered by order-placed (owned by "order"),
        // dispatches ship-order (also "order") -- source == target, same-aggregate.
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var reactor = Assert.Single(order.Reactors, r => r.Command == "ShipOrder");
        Assert.Equal(["OrderPlaced"], reactor.On);
        Assert.Equal("order", reactor.Aggregate);
        // No IdPrefix: the target is the triggering order's own existing stream, not a
        // new one -- a prefix here would dispatch to an id nothing ever created.
        Assert.Null(reactor.IdPrefix);
    }

    [Fact]
    public void The_cross_aggregate_reactor_lands_on_the_source_while_its_command_lands_on_the_target()
    {
        // notify-shipping-partner-automation: triggered by order-shipped (owned by
        // "order"), dispatches notify-shipping-partner (overridden to
        // "shippingNotification") -- cross-aggregate, so that command is a "create".
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");
        var shipping = result.Domains.Single(d => d.Aggregate == "shippingNotification");

        var reactor = Assert.Single(order.Reactors, r => r.Command == "NotifyShippingPartner");
        Assert.Equal(["OrderShipped"], reactor.On);
        Assert.Equal("shippingNotification", reactor.Aggregate);
        Assert.Equal("notify-shipping-partner-", reactor.IdPrefix);

        var command = Assert.Single(shipping.Commands);
        Assert.Equal("NotifyShippingPartner", command.Name);
        Assert.True(command.Once); // cross-aggregate: a fresh stream per trigger
    }

    [Fact]
    public void Read_models_are_attached_to_their_owning_aggregate_with_a_derived_key()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var summary = order.ReadModels.Single(rm => rm.Collection == "orderSummary");
        Assert.Equal("orderId", summary.Key); // orderId is marked idAttribute in the document
        Assert.Equal(["OrderPlaced", "OrderShipped"], summary.On);

        var pending = order.ReadModels.Single(rm => rm.Collection == "pendingShipments");
        Assert.Equal(["OrderPlaced"], pending.On);
    }

    [Fact]
    public void A_list_field_folds_to_json_and_is_reported_in_warnings()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var orderPlaced = order.Commands.Single(c => c.Name == "PlaceOrder").Events.Single(e => e.Name == "OrderPlaced");
        var items = orderPlaced.Fields.Single(f => f.Name == "items");
        Assert.Equal("json", items.Type);
        Assert.Contains(result.Report.Warnings, w => w.Contains("items") && w.Contains("json column"));
    }

    [Fact]
    public void PII_fields_are_named_in_the_lossy_report()
    {
        var result = MapOrderFulfillment();
        Assert.Contains(result.Report.Lossy, l => l.Contains("pii") && l.Contains("customerEmail"));
    }

    [Fact]
    public void Chapters_and_slice_status_are_named_as_lossy()
    {
        var result = MapOrderFulfillment();
        Assert.Contains(result.Report.Lossy, l => l.Contains("chapter"));
        Assert.Contains(result.Report.Lossy, l => l.Contains("slice status"));
    }

    [Fact]
    public void A_dangling_reference_is_caught_before_mapping_ever_runs()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "x", "name": "X",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "slices": [{
                "id":"sl","name":"SL","pattern":"stateChange","swimlaneId":"s","status":"created",
                "screenId":"scr","commandId":"nope-does-not-exist","eventIds":["also-missing"],
                "scenarios":[]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("nope-does-not-exist"));
        Assert.Contains(ex.Report.Errors, e => e.Contains("also-missing"));
    }
}
