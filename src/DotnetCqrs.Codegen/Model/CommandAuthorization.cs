using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetCqrs.Codegen.Model;

/// <summary>
/// A command's optional <c>fieldGatedRole</c> (schema 2.5.0): <see cref="RequiredRole"/>
/// applies only when the command payload's <see cref="Field"/> equals <see cref="Value"/>.
/// <c>Value</c> stays a raw <see cref="JsonElement"/> at this layer -- same precedent as
/// <see cref="ResultThen.Result"/> for "an arbitrary JSON scalar" -- and is resolved to a
/// typed <see cref="Domain.FieldGatedRoleValue"/> once, during mapping.
/// </summary>
public sealed record CommandFieldGatedRoleDef(
    string Field,
    JsonElement Value,
    [property: JsonConverter(typeof(RoleOrRolesConverter))] IReadOnlyList<string> RequiredRole);

/// <summary>A command's optional <c>requiredOwnership</c> (schema 2.5.0): the actor's own
/// id must equal the value <see cref="Via"/> resolves for the command's own target,
/// unless the actor's role is in <see cref="BypassRoles"/>.</summary>
public sealed record CommandOwnershipDef(
    [property: JsonConverter(typeof(RoleOrRolesConverter))] IReadOnlyList<string>? BypassRoles,
    CommandOwnershipVia Via);

public sealed record CommandOwnershipVia(string ReadModelId, string KeyField, string OwnerField);

/// <summary>A command's optional <c>scope</c> (schema 2.5.0): resolves a value from the
/// command's own target via <see cref="ResolveVia"/>, then the actor's own id must be a
/// member of the set <see cref="MemberOfVia"/> resolves for that value -- unless the
/// actor's role is in <see cref="BypassRoles"/>. Kept as its own declaration rather than
/// merged with <see cref="CommandOwnershipDef"/>: they share the first resolution step but
/// diverge on the second (equality-against-actor vs. set-membership) -- see
/// platform/command-authorization's design proposal for the full reasoning.</summary>
public sealed record CommandScopeDef(
    [property: JsonConverter(typeof(RoleOrRolesConverter))] IReadOnlyList<string>? BypassRoles,
    CommandScopeResolveVia ResolveVia,
    CommandScopeMemberOfVia MemberOfVia);

public sealed record CommandScopeResolveVia(string ReadModelId, string KeyField, string SelectField);

public sealed record CommandScopeMemberOfVia(string ReadModelId, string MatchField);

/// <summary>Deserializes <c>requiredRole</c>/<c>bypassRoles</c>/<c>fieldGatedRole.requiredRole</c>:
/// the schema's own <c>roleRequirement</c> <c>$def</c> (named <c>commandRole</c> before
/// <c>eventmodelschema</c> 2.7.0 -- renamed once <c>readModel.requiredRole</c> started
/// reusing it too) is an <c>anyOf</c> of a single role id or a non-empty array of them
/// (<c>eventmodelschema</c> 2.5.0) -- normalized to a list here so nothing downstream
/// re-checks which JSON shape a document happened to use.</summary>
public sealed class RoleOrRolesConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException($"expected a role id string in a role array, found {reader.TokenType}");
                list.Add(reader.GetString()!);
            }
            return list;
        }

        throw new JsonException($"expected a role id string or an array of role ids, found {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }
        writer.WriteStartArray();
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}
