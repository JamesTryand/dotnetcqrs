using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Reactors;

namespace Generated.Order;

/// <summary>Reacts to "order"'s events by dispatching order/ShipOrder
/// -- one reaction per triggering event, its payload the causing event's own data
/// untouched. The target id is derived from the source event, so a replay hits the
/// target's own "already exists" rejection instead of dispatching twice -- keep it
/// deterministic if you change it.</summary>
public sealed class AutoShipPendingOrdersReactor : IReactor
{
    public string Name => "autoShipPendingOrders";

    public IReadOnlyList<Reaction> React(Event ev)
    {
        if (ev.Type is not ("OrderPlaced")) return [];

        return [new Reaction("order", "auto-ship-pending-orders-" + ev.AggregateId, new Command("ShipOrder", ev.Data))];
    }
}
