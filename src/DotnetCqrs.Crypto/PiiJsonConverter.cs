using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Crypto;

/// <summary>The wire shape of <see cref="Pii{T}"/>, and its fail-closed guarantee.
/// Reads a bare scalar as <see cref="PiiState.Fresh"/> (a command's plaintext arriving)
/// and the envelope <c>{"$pii":{"s":subject,"c":ciphertext}}</c> as
/// <see cref="PiiState.Pending"/> (a stored event coming back). Writes only the
/// envelope, and throws for a <see cref="PiiState.Fresh"/> value — so a host that
/// forgets to register an <c>IPiiProtector</c> fails at append time rather than writing
/// plaintext into an append-only log.</summary>
public sealed class PiiJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Pii<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(PiiJsonConverter<>).MakeGenericType(inner))!;
    }
}

internal sealed class PiiJsonConverter<T> : JsonConverter<Pii<T>>
{
    private const string Envelope = "$pii";
    private const string Subject = "s";
    private const string Cipher = "c";

    public override Pii<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            return Pii<T>.Fresh(JsonSerializer.Deserialize<T>(ref reader, options)!);

        // Peek without committing: a JSON object is only an envelope if its single key is
        // "$pii"; anything else (e.g. a json-typed PII field carrying an object) is plaintext.
        var probe = reader;
        if (!probe.Read() || probe.TokenType != JsonTokenType.PropertyName || probe.GetString() != Envelope)
            return Pii<T>.Fresh(JsonSerializer.Deserialize<T>(ref reader, options)!);

        using var doc = JsonDocument.ParseValue(ref reader);
        var body = doc.RootElement.GetProperty(Envelope);
        var subject = body.GetProperty(Subject).GetString()
            ?? throw new JsonException($"{Envelope}.{Subject} must be a string");
        var cipher = body.TryGetProperty(Cipher, out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return cipher is null ? Pii<T>.Redacted(subject) : Pii<T>.FromCiphertext(subject, cipher);
    }

    public override void Write(Utf8JsonWriter writer, Pii<T> value, JsonSerializerOptions options)
    {
        if (value.State == PiiState.Fresh)
            throw new InvalidOperationException(
                $"Refusing to serialise an unencrypted Pii<{typeof(T).Name}> -- the aggregate has no IPiiProtector " +
                "registered, or Decide produced a fresh value the protector did not encrypt.");

        writer.WriteStartObject();
        writer.WritePropertyName(Envelope);
        writer.WriteStartObject();
        writer.WriteString(Subject, value.SubjectId);
        if (value.Ciphertext is null) writer.WriteNull(Cipher);
        else writer.WriteString(Cipher, value.Ciphertext);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
