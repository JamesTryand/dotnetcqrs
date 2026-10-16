using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A generated reactor that creates a new target per trigger derives the target's id from the triggering EVENT (its
/// stream id and sequence), not from its stream alone. Keyed by the stream, a second
/// trigger on the same stream was lost as "already exists". The same event redelivered still gives the same id, so the
/// target's "already exists" absorbs it. A same-aggregate reactor still targets the trigger's own stream. Order
/// fulfilment: the shipping notification is created per OrderShipped; auto-ship dispatches to the order itself.
/// </summary>
[Trait("Category", "Compiles")]
public class ReactorTargetIdTests
{
    private const string ProgramCs = """
        using DotnetCqrs.EventStore;
        using DotnetCqrs.Reactors;

        Event Ev(string type, string id, long sequence) =>
            new(Position: sequence, Id: $"ev-{id}-{sequence}", Aggregate: "order", AggregateId: id, Sequence: sequence,
                Type: type, Data: "{}", Metadata: "{}", Created: "2026-10-14T00:00:00Z");

        var reactors = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IReactor).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (IReactor)Activator.CreateInstance(t)!)
            .OrderBy(r => r.Name);
        foreach (var reactor in reactors)
            foreach (var ev in new[] { Ev("OrderPlaced", "o1", 1), Ev("OrderShipped", "o1", 3), Ev("OrderShipped", "o1", 5), Ev("OrderShipped", "o1", 3) })
                foreach (var reaction in reactor.React(ev))
                    Console.WriteLine($"{ev.Type}#{ev.Sequence} -> {reaction.Aggregate}/{reaction.Id}");
        return 0;
        """;

    private static string TestDataPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static async Task<List<string>> ReactionsAsync(Action<JsonNode>? change = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(TestDataPath("order-fulfillment.json")))!;
        change?.Invoke(node);
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString()),
            new MappingOptions { AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" } });
        var order = mapped.Domains.Single(d => d.Aggregate == "order");
        var files = order.Reactors.Select(r => ReactorGenerator.Generate(order, r)).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, ProgramCs);

        Assert.True(success, output);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Contains(" -> ")).ToList();
    }

    [Fact(Timeout = 120000)]
    public async Task A_create_per_trigger_is_keyed_by_the_event_and_a_same_aggregate_reaction_by_its_stream()
    {
        var lines = await ReactionsAsync();

        Assert.Contains("OrderPlaced#1 -> order/o1", lines);
        Assert.Equal(
            ["OrderShipped#3 -> shippingNotification/notify-shipping-partner-o1-3",
             "OrderShipped#5 -> shippingNotification/notify-shipping-partner-o1-5",
             "OrderShipped#3 -> shippingNotification/notify-shipping-partner-o1-3"],
            lines.Where(l => l.StartsWith("OrderShipped")).ToList());
    }

    [Fact(Timeout = 120000)]
    public async Task A_reaction_into_the_target_its_stream_names_keeps_the_streams_id()
    {
        // A second scenario where the notification already exists makes the command an upsert: every shipment of o1
        // goes to o1's one notification, so its id stays the stream's.
        var lines = await ReactionsAsync(node =>
        {
            var slice = node["slices"]!.AsArray().First(s => (string?)s!["id"] == "notify-shipping-partner-slice")!;
            slice["scenarios"]!.AsArray().Add(JsonNode.Parse("""
                {"id": "notify-again", "name": "A second shipment notifies again", "kind": "stateChange",
                 "given": [{"eventId": "shipment-notified", "data": {"orderId": "order-1"}}],
                 "when": {"commandId": "notify-shipping-partner", "data": {"orderId": "order-1"}},
                 "then": {"events": [{"eventId": "shipment-notified", "data": {"orderId": "order-1"}}]}}
                """));
        });

        Assert.Equal(
            ["OrderShipped#3 -> shippingNotification/notify-shipping-partner-o1",
             "OrderShipped#5 -> shippingNotification/notify-shipping-partner-o1",
             "OrderShipped#3 -> shippingNotification/notify-shipping-partner-o1"],
            lines.Where(l => l.StartsWith("OrderShipped")).ToList());
    }
}
