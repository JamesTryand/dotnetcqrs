using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A generated reactor sends its command to the stream the triggering event's targetIdField names (eventmodelschema
/// 5.0.0), and nothing else: not the trigger's own stream id, not a prefix plus its sequence. Order fulfilment: auto-ship
/// and the shipping notification both go where the event's <c>orderId</c> says, whatever stream the event is on. An
/// event without the field gets no id, which the consumer drops and logs.
/// </summary>
[Trait("Category", "Compiles")]
public class ReactorTargetIdTests
{
    private const string ProgramCs = """
        using DotnetCqrs.EventStore;
        using DotnetCqrs.Reactors;

        Event Ev(string type, string stream, long sequence, string data) =>
            new(Position: sequence, Id: $"ev-{stream}-{sequence}", Aggregate: "order", AggregateId: stream, Sequence: sequence,
                Type: type, Data: data, Metadata: "{}", Created: "2026-10-14T00:00:00Z");

        var reactors = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IReactor).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (IReactor)Activator.CreateInstance(t)!)
            .OrderBy(r => r.Name);
        foreach (var reactor in reactors)
            foreach (var ev in new[] {
                Ev("OrderPlaced", "s1", 1, "{\"orderId\":\"o1\"}"),
                Ev("OrderShipped", "s1", 3, "{\"orderId\":\"o1\"}"),
                Ev("OrderShipped", "s1", 5, "{\"orderId\":\"o2\"}"),
                Ev("OrderShipped", "s1", 7, "{}") })
                foreach (var reaction in reactor.React(ev))
                    Console.WriteLine($"{ev.Type}#{ev.Sequence} -> {reaction.Aggregate}/{reaction.Id}");
        return 0;
        """;

    private static string TestDataPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    [Fact(Timeout = 120000)]
    public async Task A_reaction_goes_to_the_stream_its_events_target_field_names()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(File.ReadAllText(TestDataPath("order-fulfillment.json"))),
            new MappingOptions { AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" } });
        var order = mapped.Domains.Single(d => d.Aggregate == "order");
        var files = order.Reactors.Select(r => ReactorGenerator.Generate(order, r)).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, ProgramCs);

        Assert.True(success, output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Contains(" -> ")).ToList();
        Assert.Contains("OrderPlaced#1 -> order/o1", lines);
        Assert.Equal(
            ["OrderShipped#3 -> shippingNotification/o1",
             "OrderShipped#5 -> shippingNotification/o2",
             "OrderShipped#7 -> shippingNotification/"],
            lines.Where(l => l.StartsWith("OrderShipped")).ToList());
        Assert.DoesNotContain(lines, l => l.Contains("/s1"));
    }
}
