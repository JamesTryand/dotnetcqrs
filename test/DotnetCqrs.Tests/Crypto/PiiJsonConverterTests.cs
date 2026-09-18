using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;

namespace DotnetCqrs.Tests.Crypto;

public class PiiJsonConverterTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Payload(string OrderId, Pii<string>? CustomerEmail, Pii<double>? Salary = null);

    [Fact]
    public void A_bare_scalar_reads_as_Fresh_plaintext()
    {
        var p = JsonSerializer.Deserialize<Payload>("""{"orderId":"o1","customerEmail":"a@b.c"}""", CamelCase)!;

        Assert.Equal(PiiState.Fresh, p.CustomerEmail!.State);
        Assert.Equal("a@b.c", p.CustomerEmail.Value);
    }

    [Fact]
    public void An_envelope_reads_as_Pending_and_reading_its_Value_throws_RevealRequired()
    {
        var p = JsonSerializer.Deserialize<Payload>(
            """{"orderId":"o1","customerEmail":{"$pii":{"s":"cust-1","c":"vault:v1:abc"}}}""", CamelCase)!;

        Assert.Equal(PiiState.Pending, p.CustomerEmail!.State);
        Assert.Equal("cust-1", p.CustomerEmail.SubjectId);
        Assert.Equal("vault:v1:abc", p.CustomerEmail.Ciphertext);
        Assert.Throws<RevealRequiredException>(() => p.CustomerEmail.Value);
    }

    [Fact]
    public void An_envelope_with_a_null_ciphertext_reads_as_Redacted()
    {
        var p = JsonSerializer.Deserialize<Payload>(
            """{"orderId":"o1","customerEmail":{"$pii":{"s":"cust-1","c":null}}}""", CamelCase)!;

        Assert.Equal(PiiState.Redacted, p.CustomerEmail!.State);
        Assert.Throws<PiiRedactedException>(() => p.CustomerEmail.Value);
    }

    [Fact]
    public void Serialising_a_Fresh_value_throws_rather_than_writing_plaintext()
    {
        var p = new Payload("o1", Pii<string>.Fresh("a@b.c"));

        var ex = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(p, CamelCase));
        Assert.Contains("unencrypted", ex.Message);
    }

    [Fact]
    public void Pending_and_Redacted_values_round_trip_through_the_envelope_unchanged()
    {
        var p = new Payload("o1", Pii<string>.FromCiphertext("cust-1", "vault:v1:abc"), Pii<double>.Redacted("cust-1"));

        var json = JsonSerializer.Serialize(p, CamelCase);
        var back = JsonSerializer.Deserialize<Payload>(json, CamelCase)!;

        Assert.Equal("""{"orderId":"o1","customerEmail":{"$pii":{"s":"cust-1","c":"vault:v1:abc"}},"salary":{"$pii":{"s":"cust-1","c":null}}}""", json);
        Assert.Equal(PiiState.Pending, back.CustomerEmail!.State);
        Assert.Equal("vault:v1:abc", back.CustomerEmail.Ciphertext);
        Assert.Equal(PiiState.Redacted, back.Salary!.State);
    }

    [Fact]
    public async Task A_Known_value_serialises_to_the_envelope_and_never_leaks_its_plaintext()
    {
        var http = new HttpClient(new FakeKmsHandler()) { BaseAddress = new Uri("https://kms.test/") };
        var known = await Pii<string>.EncryptAsync(new KmsClient(http), "cust-1", "secret@example.com");
        Assert.Equal(PiiState.Known, known.State);
        Assert.Equal("secret@example.com", known.Value);

        var json = JsonSerializer.Serialize(new Payload("o1", known), CamelCase);

        Assert.DoesNotContain("secret@example.com", json);
        Assert.Contains("\"$pii\"", json);
        Assert.Contains("\"s\":\"cust-1\"", json);
    }

    [Fact]
    public void A_json_typed_PII_field_carrying_a_plain_object_is_Fresh_not_mistaken_for_an_envelope()
    {
        var p = JsonSerializer.Deserialize<Record>("""{"address":{"line1":"1 High St","city":"Leeds"}}""", CamelCase)!;

        Assert.Equal(PiiState.Fresh, p.Address!.State);
        Assert.Equal("Leeds", p.Address.Value.GetProperty("city").GetString());
    }

    private sealed record Record(Pii<JsonElement>? Address);

    [Fact]
    public void A_null_property_stays_null()
    {
        var p = JsonSerializer.Deserialize<Payload>("""{"orderId":"o1","customerEmail":null}""", CamelCase)!;
        Assert.Null(p.CustomerEmail);
    }
}
