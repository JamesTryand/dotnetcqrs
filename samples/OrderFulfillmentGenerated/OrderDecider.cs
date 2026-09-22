using System.Text.Json;
using DotnetCqrs.Crypto;
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
public sealed record OrderState(bool Exists, string? OrderId, string? CustomerId, Pii<string>? CustomerEmail, JsonElement? Items);

public static class OrderDecider
{
    public const string Aggregate = "order";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Builds the generated decider. Register at bootstrap:
    /// <c>registry.Register(OrderDecider.Aggregate, OrderDecider.Create(), new OrderDecider.PiiProtector(kmsClient));</c>
    /// This aggregate carries field.pii values; without the protector the registry
    /// refuses to append them (fail closed) rather than storing plaintext.</summary>
    public static Decider<OrderState> Create() => new()
    {
        InitialState = () => new OrderState(false, null, null, null, null),
        Decide = (state, cmd) =>
        {
            switch (cmd.Name)
            {
                case "PlaceOrder":
                {
                    if (state.Exists) throw new InvalidOperationException("order already exists");
                    var payload = JsonSerializer.Deserialize<OrderPlacedPayload>(cmd.Payload, JsonOptions)!;
                    return [NewEvent.Of(OrderEvents.OrderPlaced, payload)];
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
                    return state with { Exists = true, OrderId = data.OrderId, CustomerId = data.CustomerId, CustomerEmail = data.CustomerEmail, Items = data.Items };
                }
                case OrderEvents.OrderShipped:
                    return state with { Exists = true };
                default:
                    return state;
            }
        },
    };

    private sealed record OrderPlacedPayload(string OrderId, string CustomerId, Pii<string>? CustomerEmail, JsonElement Items);

    /// <summary>Encrypts this aggregate's field.pii values before they are appended and
    /// reveals stored ones only when a decision reads them. Register it next to the
    /// decider; see <see cref="Create"/>.</summary>
    public sealed class PiiProtector(IKmsClient kms) : IPiiProtector
    {
        public async Task<object> RevealAsync(object state, CancellationToken ct)
        {
            var s = (OrderState)state;
            var buffer = new PiiRevealBuffer(kms);
            var revealCustomerEmail = s.CustomerEmail?.RevealAsync(buffer, ct);
            await buffer.FlushAsync(ct);
            return s with
            {
                CustomerEmail = revealCustomerEmail is null ? null : await revealCustomerEmail,
            };
        }

        public async Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct)
        {
            var result = new List<NewEvent>(events.Count);
            foreach (var e in events)
            {
                switch (e.Payload)
                {
                    case OrderPlacedPayload p:
                        result.Add(e with
                        {
                            Payload = p with
                            {
                                CustomerEmail = p.CustomerEmail is null ? null : await p.CustomerEmail.EncryptAsync(kms,
                                    p.CustomerId ?? throw new InvalidOperationException("OrderPlaced.customerEmail is pii but its piiSubject customerId is null"), ct),
                            },
                        });
                        break;
                    default:
                        result.Add(e);
                        break;
                }
            }
            return result;
        }
    }
}
