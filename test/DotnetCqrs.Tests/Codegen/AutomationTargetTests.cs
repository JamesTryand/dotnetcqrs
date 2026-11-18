using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// eventmodelschema 5.0.0: an automation names where its command goes (<c>targetIdField</c>). The mapper refuses one
/// that doesn't, or whose field a trigger event or the command lacks; verify runs an automation scenario on the stream
/// its trigger names and fails one whose command goes anywhere else, or that shows no trigger. Before, a model passed
/// every scenario with its commands going to the wrong stream. The example records an order on the customer's
/// existing stream, by the order's <c>customerId</c>: the case no derived id could reach.
/// </summary>
[Trait("Category", "Compiles")]
public class AutomationTargetTests
{
    private const string Doc = """
        {"eventModelingSchemaVersion": "5.0.0", "id": "o", "name": "O",
         "swimlanes": [{"id": "s", "name": "S", "kind": "team"}],
         "events": {
           "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "order",
             "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "customerId", "type": "string"}]},
           "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "customer",
             "fields": [{"name": "customerId", "type": "string", "idAttribute": true}]},
           "customer-ordered": {"name": "Customer Ordered", "swimlaneId": "s", "aggregate": "customer",
             "fields": [{"name": "customerId", "type": "string", "idAttribute": true}, {"name": "orderId", "type": "string"}]}},
         "commands": {
           "place-order": {"name": "Place Order", "aggregate": "order",
             "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "customerId", "type": "string"}]},
           "register-customer": {"name": "Register Customer", "aggregate": "customer",
             "fields": [{"name": "customerId", "type": "string", "idAttribute": true}]},
           "record-customer-order": {"name": "Record Customer Order", "aggregate": "customer",
             "fields": [{"name": "customerId", "type": "string", "idAttribute": true}, {"name": "orderId", "type": "string"}]}},
         "screens": {"sc": {"name": "Sc"}},
         "automations": {"credit-customer": {"name": "Credit Customer"}},
         "slices": [
           {"id": "place", "name": "Place", "pattern": "stateChange", "swimlaneId": "s", "status": "planned",
            "screenId": "sc", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
           {"id": "register", "name": "Register", "pattern": "stateChange", "swimlaneId": "s", "status": "planned",
            "screenId": "sc", "commandId": "register-customer", "eventIds": ["customer-registered"], "scenarios": []},
           {"id": "credit-customer", "name": "Credit Customer", "pattern": "automation", "swimlaneId": "s", "status": "planned",
            "automationId": "credit-customer", "triggerEventIds": ["order-placed"], "targetIdField": "customerId",
            "commandId": "record-customer-order", "resultEventIds": ["customer-ordered"],
            "scenarios": [
              {"id": "credits", "name": "the order's customer", "kind": "stateChange",
               "given": [{"eventId": "customer-registered", "data": {"customerId": "c1"}},
                         {"eventId": "order-placed", "data": {"orderId": "o1", "customerId": "c1"}}],
               "when": {"commandId": "record-customer-order", "data": {"customerId": "c1", "orderId": "o1"}},
               "then": {"events": [{"eventId": "customer-ordered", "data": {"customerId": "c1", "orderId": "o1"}}]}},
              {"id": "wrong-customer", "name": "another customer", "kind": "stateChange",
               "given": [{"eventId": "customer-registered", "data": {"customerId": "c2"}},
                         {"eventId": "order-placed", "data": {"orderId": "o1", "customerId": "c1"}}],
               "when": {"commandId": "record-customer-order", "data": {"customerId": "c2", "orderId": "o1"}},
               "then": {"events": [{"eventId": "customer-ordered", "data": {"customerId": "c2", "orderId": "o1"}}]}},
              {"id": "no-trigger", "name": "no trigger", "kind": "stateChange",
               "given": [{"eventId": "customer-registered", "data": {"customerId": "c1"}}],
               "when": {"commandId": "record-customer-order", "data": {"customerId": "c1", "orderId": "o1"}},
               "then": {"events": [{"eventId": "customer-ordered", "data": {"customerId": "c1", "orderId": "o1"}}]}}]}]}
        """;

    [Fact(Timeout = 120000)]
    public async Task Verify_fails_a_command_sent_to_another_stream_than_its_trigger_names()
    {
        var doc = DocumentLoader.Parse(Doc);

        var results = (await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc))).ToDictionary(r => r.ScenarioId);

        Assert.True(results["credits"].Passed, results["credits"].Detail);
        Assert.False(results["wrong-customer"].Passed);
        Assert.Contains("customerId \"c1\"", results["wrong-customer"].Detail);
        Assert.Contains("customerId \"c2\"", results["wrong-customer"].Detail);
        Assert.False(results["no-trigger"].Passed);
        Assert.Contains("gives none of its trigger events", results["no-trigger"].Detail);
    }

    [Fact]
    public void The_schema_requires_targetIdField()
    {
        var ex = Assert.Throws<DocumentValidationException>(() =>
            DocumentLoader.Parse(Doc.Replace("\"targetIdField\": \"customerId\",", "")));
        Assert.Contains(ex.Errors, e => e.Message.Contains("targetIdField"));
    }

    [Theory]
    [InlineData("trigger", "its targetIdField \"buyerId\" is not a field of trigger event \"order-placed\"")]
    [InlineData("command", "command \"record-customer-order\" must declare \"customerId\" with idAttribute: true")]
    public void The_mapper_refuses_a_field_the_trigger_or_the_command_lacks(string broken, string want)
    {
        var node = JsonNode.Parse(Doc)!;
        if (broken == "trigger")
            node["slices"]![2]!["targetIdField"] = "buyerId";
        else
            node["commands"]!["record-customer-order"]!["fields"]![0]!.AsObject().Remove("idAttribute");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(ex.Report.Errors, e => e.Contains(want));
    }
}
