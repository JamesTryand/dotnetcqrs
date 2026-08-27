using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace Generated.Order;

/// <summary>Event types "order" produces.</summary>
public static class OrderEvents
{
    public const string OrderPlaced = "OrderPlaced";
    public const string OrderShipped = "OrderShipped";
}

/// <summary>
/// State is "order"'s generated read side for Decide/Evolve. THE SHAPE IS
/// RIGHT, THE RULES ARE YOURS: fields come straight from the declared event payloads,
/// unioned across every event this aggregate produces. Dry-run and test this against
/// real history before relying on it.
/// </summary>
public sealed record OrderState(bool Exists, string? OrderId, string? CustomerEmail, JsonElement? Items);

public static class OrderDecider
{
    public const string Aggregate = "order";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Builds the generated decider. Register at bootstrap:
    /// <c>registry.Register(OrderDecider.Aggregate, OrderDecider.Create());</c></summary>
    public static Decider<OrderState> Create() => new()
    {
        InitialState = () => new OrderState(false, null, null, null),
        Decide = (state, cmd) =>
        {
            switch (cmd.Name)
            {
                case "PlaceOrder":
                {
                    if (state.Exists) throw new InvalidOperationException("order already exists");
                    var payload = JsonSerializer.Deserialize<OrderPlacedPayload>(cmd.Payload, JsonOptions)!;
                    return [new NewEvent(OrderEvents.OrderPlaced, JsonSerializer.Serialize(payload, JsonOptions))];
                }
                case "ShipOrder":
                {
                    if (!state.Exists) throw new InvalidOperationException("order does not exist");
                    return [new NewEvent(OrderEvents.OrderShipped, "{}")];
                }
                default:
                    throw new InvalidOperationException($"unknown command: {cmd.Name}");
            }
        },
        Evolve = (state, ev) =>
        {
            switch (ev.Type)
            {
                case OrderEvents.OrderPlaced:
                {
                    var data = JsonSerializer.Deserialize<OrderPlacedPayload>(ev.Data, JsonOptions)!;
                    return state with { Exists = true, OrderId = data.OrderId, CustomerEmail = data.CustomerEmail, Items = data.Items };
                }
                case OrderEvents.OrderShipped:
                    return state with { Exists = true };
                default:
                    return state;
            }
        },
    };

    private sealed record OrderPlacedPayload(string OrderId, string CustomerEmail, JsonElement Items);
}
