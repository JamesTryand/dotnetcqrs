using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace OrderFulfillment;

/// <summary>The order decider: place an order, then confirm it.</summary>
public static class Orders
{
    public const string Aggregate = "order";

    public sealed record State(bool Exists, bool Confirmed);

    public static Decider<State> Decider() => new()
    {
        InitialState = () => new State(false, false),
        Decide = (state, cmd) => cmd.Name switch
        {
            "PlaceOrder" when state.Exists => throw new InvalidOperationException("order already exists"),
            "PlaceOrder" => [new NewEvent("OrderPlaced", cmd.Payload)],
            "ConfirmOrder" when !state.Exists => throw new InvalidOperationException("order does not exist"),
            "ConfirmOrder" when state.Confirmed => throw new InvalidOperationException("order already confirmed"),
            "ConfirmOrder" => [new NewEvent("OrderConfirmed", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (state, ev) => ev.Type switch
        {
            "OrderPlaced" => state with { Exists = true },
            "OrderConfirmed" => state with { Confirmed = true },
            _ => state,
        },
    };
}
