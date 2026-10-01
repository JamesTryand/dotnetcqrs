using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace EventStoreFixtures;

/// <summary>
/// A customer aggregate with personal data, the smallest thing that makes the stack itself write
/// <c>{"$pii":{"s":subject,"c":ciphertext}}</c> envelopes: the events carry <c>Pii&lt;string&gt;</c> fields and a
/// registered <see cref="IPiiProtector"/> encrypts them under the stream's own subject before append.
/// (The gateway would refuse a payload that already carries an envelope, so plaintext goes in.)
/// </summary>
public static class Customers
{
    public const string Aggregate = "customer";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record RegisteredPayload(Pii<string> Email, string Plan);
    private sealed record EmailChangedPayload(Pii<string> Email);

    public static Decider<bool> Decider() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) => cmd.Name switch
        {
            "Register" when exists => throw new InvalidOperationException("customer already registered"),
            "Register" => [NewEvent.Of("Registered", JsonSerializer.Deserialize<RegisteredPayload>(cmd.Payload, Json)!)],
            "ChangeEmail" when !exists => throw new InvalidOperationException("customer does not exist"),
            "ChangeEmail" => [NewEvent.Of("EmailChanged", JsonSerializer.Deserialize<EmailChangedPayload>(cmd.Payload, Json)!)],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (exists, ev) => exists || ev.Type is "Registered" or "EmailChanged",
    };

    /// <summary>Encrypts every <c>Pii&lt;string&gt;</c> field under the stream's own subject (the aggregate id).</summary>
    public sealed class Protector(IKmsClient kms) : IPiiProtector
    {
        public Task<object> RevealAsync(object state, CancellationToken ct) => Task.FromResult(state);

        public async Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct)
        {
            // The in-memory KMS, like the real facade, wants a subject's key ensured before it encrypts.
            await kms.EnsureKeyAsync(aggregateId, ct);
            var result = new List<NewEvent>(events.Count);
            foreach (var e in events)
                result.Add(e.Payload switch
                {
                    RegisteredPayload r => e with { Payload = r with { Email = await r.Email.EncryptAsync(kms, aggregateId, ct) } },
                    EmailChangedPayload c => e with { Payload = c with { Email = await c.Email.EncryptAsync(kms, aggregateId, ct) } },
                    _ => e,
                });
            return result;
        }
    }
}
