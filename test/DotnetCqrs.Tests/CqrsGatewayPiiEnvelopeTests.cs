using System.Net;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using DotnetCqrs.Tests.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests;

/// <summary>The gateway refuses a command payload carrying a <c>{"$pii":...}</c>
/// envelope. <c>Pii&lt;T&gt;</c>'s converter reads one as already-encrypted, so an outside
/// caller could otherwise append ciphertext bound to another subject's key (or garbage
/// that later fails to decrypt), skipping the protector entirely. Reactor/ext-caller
/// dispatch calls the registry directly and must keep accepting envelopes -- that's how a
/// command built from a stored event carries its PII forward.</summary>
public class CqrsGatewayPiiEnvelopeTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record RegisteredPayload(Pii<string> Email);

    private static Decider<bool> Customer() => new()
    {
        InitialState = () => false,
        Decide = (exists, cmd) =>
        {
            if (cmd.Name != "Register") throw new InvalidOperationException($"unknown command {cmd.Name}");
            if (exists) throw new InvalidOperationException("already registered");
            return [NewEvent.Of("Registered", JsonSerializer.Deserialize<RegisteredPayload>(cmd.Payload, Json)!)];
        },
        Evolve = (_, ev) => ev.Type == "Registered",
    };

    private sealed class CustomerPiiProtector(IKmsClient kms) : IPiiProtector
    {
        public Task<object> RevealAsync(object state, CancellationToken ct) => Task.FromResult(state);

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

    private readonly SqliteEventStore _store;
    private readonly FakeKmsHandler _kms = new();
    private readonly DeciderRegistry _registry;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public CqrsGatewayPiiEnvelopeTests()
    {
        _store = SqliteEventStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        _registry = new DeciderRegistry(_store);
        var kms = new KmsClient(new HttpClient(_kms) { BaseAddress = new Uri("https://kms.test/") });
        _registry.Register("customer", Customer(), new CustomerPiiProtector(kms));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_registry);
        _app = builder.Build();
        _app.MapCqrsGateway();
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _store.DisposeAsync();
    }

    private Task<HttpResponseMessage> Post(string aggregateId, string body) =>
        _client.PostAsync($"/api/cqrs/customer/{aggregateId}/Register",
            new StringContent(body, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Plaintext_PII_is_accepted_and_encrypted_under_the_streams_own_subject()
    {
        var response = await Post("cust-1", """{"email":"ada@example.com"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = Assert.Single(await _store.LoadStreamAsync("customer", "cust-1")).Data;
        Assert.DoesNotContain("ada@example.com", data);
        Assert.Contains("\"s\":\"cust-1\"", data);
    }

    [Theory]
    [InlineData("""{"email":{"$pii":{"s":"victim","c":"fake:victim:YWRh"}}}""")]
    [InlineData("""{"email":{"$pii":{"s":"victim","c":null}}}""")]
    // With siblings: the converter would throw on these, but the gateway refuses any $pii key up front.
    [InlineData("""{"email":{"$pii":{"s":"victim","c":"fake:victim:YWRh"},"x":1}}""")]
    [InlineData("""{"email":{"x":1,"$pii":{"s":"victim","c":"fake:victim:YWRh"}}}""")]
    [InlineData("""{"email":"ada@example.com","tags":[{"$pii":{"s":"victim","c":"x"}}]}""")]
    [InlineData("""{"email":"ada@example.com","extra":{"deep":{"$pii":{"s":"victim","c":"x"}}}}""")]
    [InlineData("""{"$pii":{"s":"victim","c":"x"}}""")]
    public async Task A_payload_carrying_a_pii_envelope_anywhere_is_refused_with_400_before_dispatch(string body)
    {
        var response = await Post("cust-1", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("$pii", await response.Content.ReadAsStringAsync());
        Assert.Empty(await _store.LoadStreamAsync("customer", "cust-1"));
        Assert.Equal(0, _kms.EncryptCallCount);
    }

    // A Fact rather than InlineData: xUnit's test id unescapes the argument, so it collided with the plain case.
    [Fact]
    public async Task An_envelope_key_spelled_with_a_JSON_escape_is_refused_too()
    {
        var body = "{\"email\":{\"\\u0024p\\u0069i\":{\"s\":\"victim\",\"c\":\"fake:victim:YWRh\"}}}";
        Assert.DoesNotContain("$pii", body); // the marker only appears once the key is unescaped

        var response = await Post("cust-1", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _store.LoadStreamAsync("customer", "cust-1"));
    }

    [Theory]
    [InlineData("""{"email":"$pii@example.com"}""")]
    [InlineData("""{"email":"ada@example.com","note":{"pii":"$pii"}}""")]
    public async Task The_marker_as_a_value_or_a_near_miss_key_is_not_an_envelope(string body)
    {
        var response = await Post("cust-1", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Direct_registry_dispatch_the_reactor_and_ext_caller_path_still_accepts_an_envelope()
    {
        await Post("cust-1", """{"email":"ada@example.com"}""");
        var stored = Assert.Single(await _store.LoadStreamAsync("customer", "cust-1")).Data;
        var encryptsSoFar = _kms.EncryptCallCount;

        // A reactor builds its command from the stored event, envelope and all.
        await _registry.HandleWithMetaAsync("customer", "cust-2", new Command("Register", stored), meta: null);

        var carried = Assert.Single(await _store.LoadStreamAsync("customer", "cust-2")).Data;
        Assert.Equal(stored, carried);
        Assert.Equal(encryptsSoFar, _kms.EncryptCallCount);
    }
}
