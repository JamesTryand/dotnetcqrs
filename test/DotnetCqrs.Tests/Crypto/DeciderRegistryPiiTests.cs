using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>Proves the write-path seam decided 2026-09-18 with a hand-written aggregate —
/// the same shape the generator will later emit. Decide reads plaintext both from a
/// fresh command and (on demand, via one reveal + re-run) from stored state; the
/// registry encrypts before append; the store never sees plaintext; and an aggregate
/// registered without a protector fails closed.</summary>
public class DeciderRegistryPiiTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- a hand-written "customer" aggregate ----

    private sealed record CustomerState(bool Exists, Pii<string>? Email, int Greetings);
    private sealed record RegisteredPayload(Pii<string> Email);
    private sealed record GreetedPayload(int EmailLength);
    private sealed record PingedPayload(int Count);

    private static Decider<CustomerState> Customer(Action? onDecide = null) => new()
    {
        InitialState = () => new CustomerState(false, null, 0),
        Decide = (state, cmd) =>
        {
            onDecide?.Invoke();
            switch (cmd.Name)
            {
                case "Register":
                    if (state.Exists) throw new InvalidOperationException("already registered");
                    var reg = JsonSerializer.Deserialize<RegisteredPayload>(cmd.Payload, Json)!;
                    // Decide sees the incoming plaintext and may validate it.
                    if (!reg.Email.Value.Contains('@')) throw new InvalidOperationException("not an email");
                    return [NewEvent.Of("Registered", reg)];
                case "Greet":
                    // A decision that genuinely needs stored PII: derives a non-PII fact from it.
                    return [NewEvent.Of("Greeted", new GreetedPayload(state.Email!.Value.Length))];
                case "Ping":
                    return [NewEvent.Of("Pinged", new PingedPayload(state.Greetings + 1))];
                default:
                    throw new InvalidOperationException($"unknown command {cmd.Name}");
            }
        },
        Evolve = (state, ev) => ev.Type switch
        {
            "Registered" => state with { Exists = true, Email = JsonSerializer.Deserialize<RegisteredPayload>(ev.Data, Json)!.Email },
            "Greeted" => state with { Greetings = state.Greetings + 1 },
            _ => state,
        },
    };

    /// <summary>What a generated protector looks like for this aggregate: it knows which
    /// state fields and which payload fields are PII, and that the subject is the stream's
    /// own id.</summary>
    private sealed class CustomerPiiProtector(IKmsClient kms) : IPiiProtector
    {
        public async Task<object> RevealAsync(object state, CancellationToken ct)
        {
            var s = (CustomerState)state;
            if (s.Email is null) return s;
            var buffer = new PiiRevealBuffer(kms);
            var revealed = s.Email.RevealAsync(buffer, ct);
            await buffer.FlushAsync(ct);
            return s with { Email = await revealed };
        }

        public async Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct)
        {
            var result = new List<NewEvent>(events.Count);
            foreach (var e in events)
                result.Add(e.Payload is RegisteredPayload r
                    ? e with { Payload = r with { Email = await r.Email.EncryptAsync(kms, aggregateId, ct) } }
                    : e);
            return result;
        }
    }

    private static (IKmsClient Kms, FakeKmsHandler Handler) MakeKms()
    {
        var handler = new FakeKmsHandler();
        return (new KmsClient(new HttpClient(handler) { BaseAddress = new Uri("https://kms.test/") }), handler);
    }

    private static Command Register(string email) => new("Register", $$"""{"email":"{{email}}"}""");

    [Fact]
    public async Task Register_encrypts_the_fresh_email_before_append_and_the_store_holds_only_the_envelope()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, _) = MakeKms();
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer(), new CustomerPiiProtector(kms));

        var appended = await registry.HandleAsync("customer", "cust-1", Register("ada@example.com"));

        var stored = await store.LoadStreamAsync("customer", "cust-1");
        var data = Assert.Single(stored).Data;
        Assert.Equal(appended.Single().Data, data);
        Assert.DoesNotContain("ada@example.com", data);
        Assert.Contains("\"$pii\"", data);
        Assert.Contains("\"s\":\"cust-1\"", data);
    }

    [Fact]
    public async Task Decide_can_validate_the_incoming_plaintext_before_anything_is_encrypted_or_appended()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, handler) = MakeKms();
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer(), new CustomerPiiProtector(kms));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.HandleAsync("customer", "cust-1", Register("not-an-email")));

        Assert.Empty(await store.LoadStreamAsync("customer", "cust-1"));
        Assert.Equal(0, handler.EncryptCallCount);
    }

    [Fact]
    public async Task A_decision_that_reads_stored_PII_costs_one_reveal_and_a_second_Decide()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, handler) = MakeKms();
        var decides = 0;
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer(() => decides++), new CustomerPiiProtector(kms));
        await registry.HandleAsync("customer", "cust-1", Register("ada@example.com"));
        decides = 0;

        var events = await registry.HandleAsync("customer", "cust-1", new Command("Greet", "{}"));

        Assert.Equal(2, decides);
        Assert.Equal(1, handler.DecryptBatchCallCount);
        var greeted = JsonSerializer.Deserialize<GreetedPayload>(events.Single().Data, Json)!;
        Assert.Equal("ada@example.com".Length, greeted.EmailLength);
    }

    [Fact]
    public async Task A_decision_that_does_not_read_PII_never_reveals_and_decides_once()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, handler) = MakeKms();
        var decides = 0;
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer(() => decides++), new CustomerPiiProtector(kms));
        await registry.HandleAsync("customer", "cust-1", Register("ada@example.com"));
        decides = 0;

        var events = await registry.HandleAsync("customer", "cust-1", new Command("Ping", "{}"));

        Assert.Equal(1, decides);
        Assert.Equal(0, handler.DecryptBatchCallCount);
        Assert.Equal("""{"count":1}""", events.Single().Data);
    }

    [Fact]
    public async Task Without_a_protector_a_fresh_PII_value_fails_closed_and_nothing_is_appended()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer()); // no protector

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.HandleAsync("customer", "cust-1", Register("ada@example.com")));

        Assert.Contains("unencrypted", ex.Message);
        Assert.Empty(await store.LoadStreamAsync("customer", "cust-1"));
    }

    [Fact]
    public async Task Without_a_protector_reading_stored_PII_surfaces_RevealRequired_to_the_caller()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, _) = MakeKms();
        var protectedRegistry = new DeciderRegistry(store);
        protectedRegistry.Register("customer", Customer(), new CustomerPiiProtector(kms));
        await protectedRegistry.HandleAsync("customer", "cust-1", Register("ada@example.com"));

        var bare = new DeciderRegistry(store);
        bare.Register("customer", Customer());

        await Assert.ThrowsAsync<RevealRequiredException>(() => bare.HandleAsync("customer", "cust-1", new Command("Greet", "{}")));
    }

    [Fact]
    public async Task A_typed_payload_with_no_PII_needs_no_protector_and_is_serialised_camelCase()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer());

        var events = await registry.HandleAsync("customer", "cust-1", new Command("Ping", "{}"));

        Assert.Equal("""{"count":1}""", events.Single().Data);
        Assert.Equal("""{"count":1}""", (await store.LoadStreamAsync("customer", "cust-1")).Single().Data);
    }

    [Fact]
    public async Task Reading_PII_of_an_erased_subject_throws_PiiRedacted_and_is_not_retried()
    {
        await using var store = await SqliteEventStore.OpenAsync(":memory:");
        var (kms, handler) = MakeKms();
        var decides = 0;
        var registry = new DeciderRegistry(store);
        registry.Register("customer", Customer(() => decides++), new CustomerPiiProtector(kms));
        await registry.HandleAsync("customer", "cust-1", Register("ada@example.com"));
        await kms.DestroyKeyAsync("cust-1");
        decides = 0;

        await Assert.ThrowsAsync<PiiRedactedException>(() => registry.HandleAsync("customer", "cust-1", new Command("Greet", "{}")));

        Assert.Equal(2, decides); // pending -> reveal -> redacted on the re-run, then stop
        Assert.Equal(1, handler.DecryptBatchCallCount);
    }
}
