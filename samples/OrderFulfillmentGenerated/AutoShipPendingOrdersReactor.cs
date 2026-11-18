using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Reactors;

namespace Generated.Order;

/// <summary>Reacts to "order"'s events by dispatching order/ShipOrder
/// -- one reaction per triggering event, its payload the causing event's own data
/// untouched. The target is the order stream the event's orderId names (the model's
/// targetIdField). Delivery is at-least-once; a redelivery reaches the same stream, and the registry skips a
/// reaction already applied.</summary>
public sealed class AutoShipPendingOrdersReactor : IReactor
{
    public string Name => "autoShipPendingOrders";

    public IReadOnlyList<Reaction> React(Event ev)
    {
        if (ev.Type is not ("OrderPlaced")) return [];

        return [new Reaction("order", ReactorTarget.Id(ev, "orderId"), new Command("ShipOrder", ev.Data))];
    }
}
