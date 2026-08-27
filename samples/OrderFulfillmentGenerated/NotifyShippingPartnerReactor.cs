using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Reactors;

namespace Generated.Order;

/// <summary>Reacts to "order"'s events by dispatching shippingNotification/NotifyShippingPartner
/// -- one reaction per triggering event, its payload the causing event's own data
/// untouched. The target id is derived from the source event, so a replay hits the
/// target's own "already exists" rejection instead of dispatching twice -- keep it
/// deterministic if you change it.</summary>
public sealed class NotifyShippingPartnerReactor : IReactor
{
    public string Name => "notifyShippingPartner";

    public IReadOnlyList<Reaction> React(Event ev)
    {
        if (ev.Type is not ("OrderShipped")) return [];

        return [new Reaction("shippingNotification", "notify-shipping-partner-" + ev.AggregateId, new Command("NotifyShippingPartner", ev.Data))];
    }
}
