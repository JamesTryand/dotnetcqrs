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
    /// <summary>Encrypts fresh pii values after Decide and reveals stored ones on demand.
    /// <paramref name="subjects"/> is optional: supply one (SubjectStatus over the event
    /// store) and this refuses to store new PII for an erased data subject, since a
    /// returning person is a new subject with a new id, never a reactivation of the old one.</summary>
    public sealed class PiiProtector(IKmsClient kms, ISubjectStatus? subjects = null) : IPiiProtector
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
                                    await SubjectAsync(p.CustomerId, "OrderPlaced.customerEmail", "customerId", ct), ct),
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

        /// <summary>Resolves the subject id a value is encrypted under, and refuses an
        /// erased one. Without a guard (subjects is null) only the null check applies.</summary>
        private async Task<string> SubjectAsync(string? subjectId, string field, string subjectField, CancellationToken ct)
        {
            var id = subjectId ?? throw new InvalidOperationException($"{field} is pii but its piiSubject {subjectField} is null");
            if (subjects is not null && await subjects.IsErasedAsync(id, ct)) throw new SubjectErasedException(id);
            return id;
        }
    }
}
