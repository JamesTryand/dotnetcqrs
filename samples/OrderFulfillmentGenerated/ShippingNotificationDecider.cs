using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace Generated.ShippingNotification;

/// <summary>Event types "shippingNotification" produces.</summary>
public static class ShippingNotificationEvents
{
    public const string ShipmentNotified = "ShipmentNotified";
}

/// <summary>
/// State is "shippingNotification"'s generated read side for Decide/Evolve. THE SHAPE IS
/// RIGHT, THE RULES ARE YOURS: fields come straight from the declared event payloads,
/// unioned across every event this aggregate produces. Dry-run and test this against
/// real history before relying on it.
/// </summary>
public sealed record ShippingNotificationState(bool Exists);

public static class ShippingNotificationDecider
{
    public const string Aggregate = "shippingNotification";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Builds the generated decider. Register at bootstrap:
    /// <c>registry.Register(ShippingNotificationDecider.Aggregate, ShippingNotificationDecider.Create());</c></summary>
    public static Decider<ShippingNotificationState> Create() => new()
    {
        InitialState = () => new ShippingNotificationState(false),
        Decide = (state, cmd) =>
        {
            switch (cmd.Name)
            {
                case "NotifyShippingPartner":
                {
                    if (state.Exists) throw new InvalidOperationException("shippingNotification already exists");
                    return [new NewEvent(ShippingNotificationEvents.ShipmentNotified, "{}")];
                }
                default:
                    throw new InvalidOperationException($"unknown command: {cmd.Name}");
            }
        },
        Evolve = (state, ev) =>
        {
            switch (ev.Type)
            {
                case ShippingNotificationEvents.ShipmentNotified:
                    return state with { Exists = true };
                default:
                    return state;
            }
        },
    };
}
