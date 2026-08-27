using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>
/// Deserializes a discriminated-union JSON object (buffered into a <see cref="JsonDocument"/>
/// first) by finding <paramref name="discriminatorProperty"/> and dispatching to the
/// matching concrete type from <paramref name="typesByDiscriminator"/>.
///
/// .NET's native polymorphic deserialization (<c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c>)
/// requires the discriminator property to appear before any other property in the JSON
/// object when the target type uses a parameterized (record) constructor — confirmed
/// with a standalone repro, not assumed: a real EventModeling document's slices have
/// <c>id</c>/<c>name</c> before <c>pattern</c>, which the native mechanism rejects with
/// "must specify a type discriminator" even though the discriminator IS present, just
/// not first. JSON objects are semantically unordered; a document author has no reason
/// to know or care about this ordering requirement. This converter finds the
/// discriminator regardless of position.
/// </summary>
public abstract class DiscriminatedUnionConverter<TBase>(string discriminatorProperty, IReadOnlyDictionary<string, Type> typesByDiscriminator)
    : JsonConverter<TBase>
{
    public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        string? discriminatorValue = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, discriminatorProperty, StringComparison.OrdinalIgnoreCase))
            {
                discriminatorValue = property.Value.GetString();
                break;
            }
        }

        if (discriminatorValue is null || !typesByDiscriminator.TryGetValue(discriminatorValue, out var concreteType))
            throw new JsonException(
                $"missing or unrecognized \"{discriminatorProperty}\" while deserializing {typeToConvert.Name}: {discriminatorValue ?? "(absent)"}");

        return (TBase?)JsonSerializer.Deserialize(root.GetRawText(), concreteType, options);
    }

    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value!.GetType(), options);
}
