using System.Text;
using DotnetCqrs.Codegen.Domain;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Emits ONE generated file, <c>CommandAuthorization.cs</c>, covering every command in
/// the WHOLE document -- unlike every other generator here (one file per aggregate or
/// per read model), this is cross-cutting, the same posture <see cref="HostProjectGenerator"/>'s
/// own <c>Program.cs</c> already takes. That's a real architectural consequence of how
/// <c>DotnetCqrs.Host.CqrsGatewayEndpoints.MapCqrsGateway</c> actually dispatches commands:
/// there is no per-command generated ROUTE to attach a check to (confirmed directly against
/// <c>project/timesheets</c>'s real <c>Program.cs</c> before designing this -- most commands
/// fall through the one generic <c>POST {aggregate}/{aggregateId}/{command}</c> route, with
/// only a handful getting a hand-written literal-route override for reasons unrelated to
/// authorization). So the generated authorization check has to live in something the
/// generic gateway can consult by <c>(aggregate, command)</c> -- a lookup table, not
/// per-route code -- and the same table needs to be callable explicitly from any
/// hand-written literal route too (see this project's own <c>StaffOnboardingHost</c>/
/// <c>TimeEntryFlaggingHost</c>/<c>ImpersonationHost</c>-shaped guards, which this
/// capability is meant to replace, not merely supplement).
///
/// <para>Runtime types are declared INSIDE the generated file, not borrowed from
/// <see cref="Domain"/> -- <c>Domain.*</c> is this generator's own input model, not
/// something generated runtime code should depend on (same posture every other generator
/// here already takes: <see cref="DeciderGenerator"/>/<see cref="ProjectionGenerator"/>
/// never reference <c>Domain.*</c> in the code they EMIT, only while building it).</para>
///
/// <para><c>resolveOwnRole</c>/<c>resolveOwnStaffId</c> are pluggable delegates the
/// evaluator takes as parameters, the same shape <c>MapCqrsGateway</c>'s existing
/// <c>resolveActor</c> already uses -- deliberately NOT fixed claim-type constants baked
/// into this generator or into <c>DotnetCqrs.Host</c>. Unlike the raw actor id (where
/// <c>oid</c>/<c>azp</c>/<see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// really are provider-standard claim types worth a generic default), a role or
/// "own staff id" claim's NAME is inherently project-specific -- <c>project/timesheets</c>'s
/// own <c>EffectiveActor.OwnRoleClaimType</c> is literally <c>"timesheets:ownRole"</c>,
/// which would mean nothing to a document with no "staff" concept at all (e.g.
/// <c>samples/OrderFulfillment</c>). The consuming project supplies both resolvers at its
/// own <c>MapCqrsGateway(authorize: ...)</c> call site -- the same place it already
/// hand-wires <c>resolveActor: EffectiveActor.Effective</c> today (see
/// <c>project/timesheets</c>'s own <c>Program.cs</c>, a <c>[HAND, Phase 04g]</c>
/// addition this generator does not attempt to auto-wire, for the same reason it can't
/// auto-wire <c>resolveActor</c> either -- see <see cref="HostProjectGenerator"/>'s
/// generated <c>Program.cs</c>, which leaves both as the operator's own addition).</para>
///
/// <para>The membership check a declared <c>scope</c> runs
/// (<c>command.scope.memberOfVia</c>) hard-codes a <c>staff_id</c> column on the target
/// read model -- not a declared field name -- matching the design proposal's own
/// literal semantics ("the actor's own staffId must appear as a staffId in
/// memberOfVia.readModelId") and the real hand-written precedent this replaces
/// (<c>TimeEntryFlaggingHost.AuthorizeAsync</c>'s <c>SELECT 1 FROM projectManagers
/// WHERE project_id = @projectId AND staff_id = @staffId</c>).</para>
/// </summary>
public static class CommandAuthorizationGenerator
{
    public static GeneratedFile Generate(IReadOnlyList<Domain.Domain> domains)
    {
        var entries = new List<(string Aggregate, Command Command)>();
        foreach (var domain in domains)
            foreach (var command in domain.Commands)
                if (command.RequiredRole is not null || command.FieldGatedRole is not null
                    || command.RequiredOwnership is not null || command.Scope is not null)
                    entries.Add((domain.Aggregate, command));

        var b = new StringBuilder();
        b.AppendLine("using System.Security.Claims;");
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using System.Linq;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine();
        b.AppendLine("namespace Generated;");
        b.AppendLine();
        b.AppendLine("/// <summary>Generated command-authorization policy table and evaluator -- see");
        b.AppendLine("/// CommandAuthorizationGenerator's own doc comment for why this is one cross-cutting");
        b.AppendLine("/// file rather than one per aggregate, and for the resolveOwnRole/resolveOwnStaffId");
        b.AppendLine("/// pluggable-resolver shape below.</summary>");
        b.AppendLine("public static class CommandAuthorization");
        b.AppendLine("{");

        b.AppendLine("    public static readonly IReadOnlyDictionary<(string Aggregate, string Command), CommandAuthorizationPolicy> Policies =");
        b.AppendLine("        new Dictionary<(string, string), CommandAuthorizationPolicy>");
        b.AppendLine("        {");
        foreach (var (aggregate, command) in entries)
            b.AppendLine($"            [(\"{aggregate}\", \"{command.Name}\")] = {PolicyLiteral(command)},");
        b.AppendLine("        };");
        b.AppendLine();

        b.AppendLine("    /// <summary>Evaluates the declared policy for (aggregate, command), if any. No");
        b.AppendLine("    /// declared policy (the common case for most commands) returns true -- unchanged");
        b.AppendLine("    /// behavior for a document that declares none of requiredRole/fieldGatedRole/");
        b.AppendLine("    /// requiredOwnership/scope on this command.</summary>");
        b.AppendLine("    public static async Task<bool> AuthorizeAsync(");
        b.AppendLine("        ClaimsPrincipal user, string aggregate, string command, string aggregateId, JsonElement payload,");
        b.AppendLine("        IReadModelStore store, Func<ClaimsPrincipal, string> resolveOwnRole, Func<ClaimsPrincipal, string> resolveOwnStaffId,");
        b.AppendLine("        CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        if (!Policies.TryGetValue((aggregate, command), out var policy)) return true;");
        b.AppendLine();
        b.AppendLine("        var ownRole = resolveOwnRole(user);");
        b.AppendLine();
        b.AppendLine("        if (policy.RequiredRole is { Length: > 0 } requiredRole)");
        b.AppendLine("            return requiredRole.Contains(ownRole, StringComparer.OrdinalIgnoreCase);");
        b.AppendLine();
        b.AppendLine("        if (policy.FieldGatedRole is { } fieldGated)");
        b.AppendLine("        {");
        b.AppendLine("            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(fieldGated.Field, out var actual) || !fieldGated.Matches(actual))");
        b.AppendLine("                return true; // condition doesn't apply -- no additional requirement from this declaration");
        b.AppendLine("            return fieldGated.RequiredRole.Contains(ownRole, StringComparer.OrdinalIgnoreCase);");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        if (policy.Ownership is { } ownership)");
        b.AppendLine("        {");
        b.AppendLine("            if (ownership.BypassRoles.Contains(ownRole, StringComparer.OrdinalIgnoreCase)) return true;");
        b.AppendLine("            var ownStaffId = resolveOwnStaffId(user);");
        b.AppendLine("            var owner = await ScalarStringAsync(store,");
        b.AppendLine("                $\"SELECT {ownership.OwnerColumn} FROM {ownership.ViaCollection} WHERE {ownership.KeyColumn} = @id\",");
        b.AppendLine("                [(\"@id\", aggregateId)], ct);");
        b.AppendLine("            return owner is not null && string.Equals(owner, ownStaffId, StringComparison.Ordinal);");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        if (policy.Scope is { } scope)");
        b.AppendLine("        {");
        b.AppendLine("            if (scope.BypassRoles.Contains(ownRole, StringComparer.OrdinalIgnoreCase)) return true;");
        b.AppendLine("            var ownStaffId = resolveOwnStaffId(user);");
        b.AppendLine("            var resolved = await ScalarStringAsync(store,");
        b.AppendLine("                $\"SELECT {scope.ResolveSelectColumn} FROM {scope.ResolveViaCollection} WHERE {scope.ResolveKeyColumn} = @id\",");
        b.AppendLine("                [(\"@id\", aggregateId)], ct);");
        b.AppendLine("            if (resolved is null) return false;");
        b.AppendLine("            var member = await ScalarStringAsync(store,");
        b.AppendLine("                $\"SELECT 1 FROM {scope.MemberOfViaCollection} WHERE {scope.MemberOfMatchColumn} = @val AND staff_id = @staffId LIMIT 1\",");
        b.AppendLine("                [(\"@val\", resolved), (\"@staffId\", ownStaffId)], ct);");
        b.AppendLine("            return member is not null;");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        return true;");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    private static async Task<string?> ScalarStringAsync(IReadModelStore store, string sql, (string, object?)[] parameters, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        await using var cmd = store.Connection.CreateCommand();");
        b.AppendLine("        cmd.CommandText = sql;");
        b.AppendLine("        foreach (var (name, value) in parameters) cmd.AddParam(name, value);");
        b.AppendLine("        var result = await cmd.ExecuteScalarAsync(ct);");
        b.AppendLine("        return result is null or DBNull ? null : result.ToString();");
        b.AppendLine("    }");
        b.AppendLine("}");
        b.AppendLine();

        b.AppendLine("public sealed record CommandAuthorizationPolicy(");
        b.AppendLine("    string[]? RequiredRole = null, FieldGatedRolePolicy? FieldGatedRole = null,");
        b.AppendLine("    OwnershipPolicy? Ownership = null, ScopePolicy? Scope = null);");
        b.AppendLine();
        b.AppendLine("public sealed record FieldGatedRolePolicy(string Field, Func<JsonElement, bool> Matches, string[] RequiredRole);");
        b.AppendLine();
        b.AppendLine("public sealed record OwnershipPolicy(string ViaCollection, string KeyColumn, string OwnerColumn, string[] BypassRoles);");
        b.AppendLine();
        b.AppendLine("public sealed record ScopePolicy(");
        b.AppendLine("    string ResolveViaCollection, string ResolveKeyColumn, string ResolveSelectColumn,");
        b.AppendLine("    string MemberOfViaCollection, string MemberOfMatchColumn, string[] BypassRoles);");

        return new GeneratedFile("CommandAuthorization.cs", b.ToString());
    }

    private static string PolicyLiteral(Command command)
    {
        var parts = new List<string>();
        if (command.RequiredRole is not null)
            parts.Add($"RequiredRole: {GenerationSupport.QuotedArray(command.RequiredRole)}");
        if (command.FieldGatedRole is { } fieldGated)
            parts.Add($"FieldGatedRole: new(\"{fieldGated.Field}\", {ValuePredicate(fieldGated.Value)}, {GenerationSupport.QuotedArray(fieldGated.RequiredRole)})");
        if (command.RequiredOwnership is { } ownership)
            parts.Add($"Ownership: new(\"{ownership.ViaCollection}\", \"{ToSnakeCase(ownership.KeyField)}\", \"{ToSnakeCase(ownership.OwnerField)}\", {GenerationSupport.QuotedArray(ownership.BypassRoles)})");
        if (command.Scope is { } scope)
            parts.Add($"Scope: new(\"{scope.ResolveViaCollection}\", \"{ToSnakeCase(scope.ResolveKeyField)}\", \"{ToSnakeCase(scope.ResolveSelectField)}\", " +
                $"\"{scope.MemberOfViaCollection}\", \"{ToSnakeCase(scope.MemberOfMatchField)}\", {GenerationSupport.QuotedArray(scope.BypassRoles)})");
        return "new(" + string.Join(", ", parts) + ")";
    }

    /// <summary>Renders a <see cref="FieldGatedRoleValue"/> as a C# lambda comparing a
    /// parsed payload's <see cref="JsonElement"/> against the declared value -- chosen
    /// over a runtime-typed value record so the JsonValueKind check this needs is baked
    /// in once, here, at generation time, rather than re-branched on every call.</summary>
    private static string ValuePredicate(FieldGatedRoleValue value) => value switch
    {
        StringFieldValue s => $"actual => actual.ValueKind == JsonValueKind.String && actual.GetString() == \"{s.Value}\"",
        BoolFieldValue b => $"actual => actual.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.GetBoolean() == {(b.Value ? "true" : "false")}",
        NumberFieldValue n => $"actual => actual.ValueKind == JsonValueKind.Number && actual.GetDouble() == {n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        _ => throw new NotSupportedException($"unrecognized field-gated-role value kind: {value.GetType().Name}"),
    };

    // Generator-time helper baking known column names into the policy table above --
    // same duplication precedent ReadModelQueryGenerator's own copy of this transform
    // already documents (ProjectionGenerator/HarnessProgram.txt each carry one too).
    private static string ToSnakeCase(string name)
    {
        var b = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) b.Append('_');
                b.Append(char.ToLowerInvariant(c));
            }
            else
            {
                b.Append(c);
            }
        }
        return b.ToString();
    }
}
