using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Reactors;

namespace OrderFulfillment;

/// <summary>
/// Opens a fulfillment task for every confirmed order, on the task aggregate
/// "fulfill-&lt;orderId&gt;" — the deterministic target id that makes replays
/// idempotent (the second dispatch hits the task decider's own "already exists").
/// </summary>
public sealed class FulfillmentReactor : IReactor
{
    public string Name => "fulfillment";

    public IReadOnlyList<Reaction> React(Event ev)
    {
        if (ev.Aggregate != Orders.Aggregate || ev.Type != "OrderConfirmed") return [];

        var payload = $$"""{"title":"fulfil order {{ev.AggregateId}}"}""";
        return [new Reaction(Tasks.Aggregate, $"fulfill-{ev.AggregateId}", new Command("CreateTask", payload))];
    }
}
