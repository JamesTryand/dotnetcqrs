using System.Text;
using DotnetCqrs.Codegen.Domain;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Emits ONE generated file, <c>CommandAuthorization.cs</c>, covering every command in
/// the WHOLE document -- unlike every other generator here (one file per aggregate or
/// per read model), this is cross-cutting, the same posture <see cref="HostProjectGenerator"/>'s
/// own <c>Program.cs</c> already takes. That's a real architectural consequence of how
/// <c>DotnetCqrs.Host.CqrsGatewayEndpoints.MapCqrsGateway</c> actually dispatches commands:
/// there is no per-command generated ROUTE to attach a check to: most commands fall through
/// the one generic <c>POST {aggregate}/{aggregateId}/{command}</c> route, with at most a
/// handful getting a hand-written literal-route override for reasons unrelated to
/// authorization. So the generated authorization check has to live in something the
/// generic gateway can consult by <c>(aggregate, command)</c> -- a lookup table, not
/// per-route code -- and the same table needs to be callable explicitly from any
/// hand-written literal route too, so it can replace a host's hand-written guards rather
/// than merely supplement them.
///
/// <para>Runtime types are declared INSIDE the generated file, not borrowed from
/// <see cref="Domain"/> -- <c>Domain.*</c> is this generator's own input model, not
/// something generated runtime code should depend on (same posture every other generator
/// here already takes: <see cref="DeciderGenerator"/>/<see cref="ProjectionGenerator"/>
/// never reference <c>Domain.*</c> in the code they EMIT, only while building it).</para>
///
/// <para><c>resolveOwnRole</c>/<c>resolveOwnSubjectId</c> are pluggable delegates the
/// evaluator takes as parameters, the same shape <c>MapCqrsGateway</c>'s existing
/// <c>resolveActor</c> already uses -- deliberately NOT fixed claim-type constants baked
/// into this generator or into <c>DotnetCqrs.Host</c>. Unlike the raw actor id (where
/// <c>oid</c>/<c>azp</c>/<see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
/// really are provider-standard claim types worth a generic default), the claim a role or
/// an own id comes from is named by each host, and would mean nothing to another. The
/// consuming project supplies both resolvers at its own <c>MapCqrsGateway(authorize: ...)</c>
/// call site -- the same place it hand-wires <c>resolveActor</c> -- and
/// <see cref="HostProjectGenerator"/>'s generated <c>Program.cs</c> leaves both as the
/// operator's own addition.</para>
///
/// <para>The membership check a declared <c>scope</c> runs
/// (<c>command.scope.memberOfVia</c>) compares the member through the field the document
/// names (<c>actorField</c>, schema 4.0.0), never an assumed column. A scope naming its own
/// <c>message</c> (schema 4.0.0) refuses with it as a <c>CommandForbiddenException</c>, which the
/// gateway answers 403 with, as it does the decider's own refusals.</para>
///
/// <para>The evaluation is split into what the caller's role and the payload decide
/// (<c>RolesAllow</c>) and what read models say about who the caller is
/// (<c>StateRefusalAsync</c>), so verify can run them for a scenario's actor and role, over read
/// models built from its given events, exactly as the gateway runs them. <c>ActorRefusalAsync</c>
/// is that by (aggregate, command): what model-only verify runs, and what a host hands its own
/// verify (<c>HostDeciders.Authorization</c>). A scenario naming no role (schema 4.1.0
/// <c>when.role</c>) holds the command's required roles and none of its bypass roles, so only
/// the second part runs.</para>
///
/// <para>The runtime's built-in <c>dataSubject</c> aggregate is the one exception to "no policy
/// allows it". Erasure cannot be undone, so every <c>dataSubject</c> command is refused here
/// unless the document's <c>dataSubjects.erasure</c> (schema 3.2.0) admits it, and that declaration
/// only ever admits <c>RequestErasure</c> (schema 3.7.1): <c>self</c> for the caller's own subject
/// id (or, with <c>via</c>, the subject ids a read model says the caller owns), <c>roles</c> for
/// any subject. Erasing directly, holding, approving and legal holds are each host's own policy,
/// never a document's, so a host that allows them wraps its <c>authorize</c> delegate around this
/// one. Nothing here names a role or an application: the rule is whatever the document says.</para>
/// </summary>
public static class CommandAuthorizationGenerator
{
    public static GeneratedFile Generate(IReadOnlyList<Domain.Domain> domains, ErasureRequestPolicy? erasureRequest = null)
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
        b.AppendLine("/// file rather than one per aggregate, and for the resolveOwnRole/resolveOwnSubjectId");
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

        b.AppendLine("    /// <summary>The runtime's built-in data-subject aggregate, and the one command of it a document may");
        b.AppendLine("    /// authorise (dataSubjects.erasure, schema 3.7.1).</summary>");
        b.AppendLine("    public const string DataSubjectAggregate = \"dataSubject\";");
        b.AppendLine("    public const string RequestErasureCommand = \"RequestErasure\";");
        b.AppendLine();
        b.AppendLine("    /// <summary>Who may request a subject's erasure, from the document's dataSubjects.erasure; null when it");
        b.AppendLine("    /// declares none, and then no one may.</summary>");
        b.AppendLine($"    public static readonly ErasureRequestPolicy? ErasureRequest = {ErasureRequestLiteral(erasureRequest)};");
        b.AppendLine();

        b.AppendLine("    /// <summary>Evaluates the declared policy for (aggregate, command), if any. No");
        b.AppendLine("    /// declared policy (the common case for most commands) returns true -- unchanged");
        b.AppendLine("    /// behavior for a document that declares none of requiredRole/fieldGatedRole/");
        b.AppendLine("    /// requiredOwnership/scope on this command. Declarations combine: every one the");
        b.AppendLine("    /// command declares must hold, so each refuses or passes on to the next. A refusing");
        b.AppendLine("    /// declaration that names its own message (a scope's, schema 4.0.0) throws");
        b.AppendLine("    /// CommandForbiddenException with it instead of returning false. The dataSubject aggregate");
        b.AppendLine("    /// is the exception: see DataSubjectAllowsAsync.</summary>");
        b.AppendLine("    public static async Task<bool> AuthorizeAsync(");
        b.AppendLine("        ClaimsPrincipal user, string aggregate, string command, string aggregateId, JsonElement payload,");
        b.AppendLine("        IReadModelStore store, Func<ClaimsPrincipal, string> resolveOwnRole, Func<ClaimsPrincipal, string> resolveOwnSubjectId,");
        b.AppendLine("        CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        if (string.Equals(aggregate, DataSubjectAggregate, StringComparison.OrdinalIgnoreCase))");
        b.AppendLine("            return await DataSubjectAllowsAsync(command, aggregateId, resolveOwnRole(user), () => resolveOwnSubjectId(user), store, ct);");
        b.AppendLine("        if (!Policies.TryGetValue((aggregate, command), out var policy)) return true;");
        b.AppendLine();
        b.AppendLine("        var ownRole = resolveOwnRole(user);");
        b.AppendLine("        if (!RolesAllow(policy, ownRole, payload)) return false;");
        b.AppendLine();
        b.AppendLine("        var refusal = await StateRefusalAsync(policy, ownRole, () => resolveOwnSubjectId(user), aggregateId, store, ct);");
        b.AppendLine("        if (refusal is null) return true;");
        b.AppendLine("        if (refusal.Length > 0) throw new DotnetCqrs.Deciders.CommandForbiddenException(refusal);");
        b.AppendLine("        return false;");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    /// <summary>The role declarations (requiredRole, fieldGatedRole): what the caller's role");
        b.AppendLine("    /// and the payload decide.</summary>");
        b.AppendLine("    public static bool RolesAllow(CommandAuthorizationPolicy policy, string ownRole, JsonElement payload)");
        b.AppendLine("    {");
        b.AppendLine("        if (policy.RequiredRole is { Length: > 0 } requiredRole && !requiredRole.Contains(ownRole, StringComparer.OrdinalIgnoreCase))");
        b.AppendLine("            return false;");
        b.AppendLine();
        b.AppendLine("        if (policy.FieldGatedRole is { } fieldGated)");
        b.AppendLine("        {");
        b.AppendLine("            // A condition that doesn't apply adds no requirement from this declaration.");
        b.AppendLine("            var applies = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(fieldGated.Field, out var actual) && fieldGated.Matches(actual);");
        b.AppendLine("            if (applies && !fieldGated.RequiredRole.Contains(ownRole, StringComparer.OrdinalIgnoreCase)) return false;");
        b.AppendLine("        }");
        b.AppendLine("        return true;");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    /// <summary>What verify runs before a command scenario's decider: (aggregate, command)'s");
        b.AppendLine("    /// declarations for the scenario's actor holding its role (schema 4.1.0 when.role). With a");
        b.AppendLine("    /// role, the role declarations must admit it and its bypass roles apply; without one, the");
        b.AppendLine("    /// actor holds the required roles and none of the bypass roles, so only the read-model");
        b.AppendLine("    /// declarations run. Null when they allow it, as StateRefusalAsync; \"\" for a refused role.");
        b.AppendLine("    /// A host hands it to its --verify as HostDeciders.Authorization.</summary>");
        b.AppendLine("    public static async Task<string?> ActorRefusalAsync(");
        b.AppendLine("        string aggregate, string command, string actor, string? role, string aggregateId, string payload,");
        b.AppendLine("        IReadModelStore store, CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        if (!Policies.TryGetValue((aggregate, command), out var policy)) return null;");
        b.AppendLine("        if (role is not null)");
        b.AppendLine("        {");
        b.AppendLine("            using var body = JsonDocument.Parse(payload);");
        b.AppendLine("            if (!RolesAllow(policy, role, body.RootElement)) return \"\";");
        b.AppendLine("        }");
        b.AppendLine("        return await StateRefusalAsync(policy, role ?? \"\", () => actor, aggregateId, store, ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    /// <summary>The read-model declarations (requiredOwnership via, scope): what the target's");
        b.AppendLine("    /// rows say about who the caller is. Null when they allow the command; otherwise the");
        b.AppendLine("    /// refusing declaration's own message, or \"\" when it names none. ownRole matters only");
        b.AppendLine("    /// as a bypass role.</summary>");
        b.AppendLine("    public static async Task<string?> StateRefusalAsync(");
        b.AppendLine("        CommandAuthorizationPolicy policy, string ownRole, Func<string> ownSubjectId, string aggregateId,");
        b.AppendLine("        IReadModelStore store, CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        if (policy.Ownership is { } ownership)");
        b.AppendLine("        {");
        b.AppendLine("            if (!ownership.BypassRoles.Contains(ownRole, StringComparer.OrdinalIgnoreCase))");
        b.AppendLine("            {");
        b.AppendLine("                var owner = await ScalarStringAsync(store,");
        b.AppendLine("                    $\"SELECT {ownership.OwnerColumn} FROM {ownership.ViaCollection} WHERE {ownership.KeyColumn} = @id\",");
        b.AppendLine("                    [(\"@id\", aggregateId)], ct);");
        b.AppendLine("                if (owner is null || !string.Equals(owner, ownSubjectId(), StringComparison.Ordinal)) return \"\";");
        b.AppendLine("            }");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        if (policy.Scope is { } scope)");
        b.AppendLine("        {");
        b.AppendLine("            if (!scope.BypassRoles.Contains(ownRole, StringComparer.OrdinalIgnoreCase))");
        b.AppendLine("            {");
        b.AppendLine("                var resolved = await ScalarStringAsync(store,");
        b.AppendLine("                    $\"SELECT {scope.ResolveSelectColumn} FROM {scope.ResolveViaCollection} WHERE {scope.ResolveKeyColumn} = @id\",");
        b.AppendLine("                    [(\"@id\", aggregateId)], ct);");
        b.AppendLine("                if (resolved is null) return scope.Message ?? \"\";");
        b.AppendLine("                var member = await ScalarStringAsync(store,");
        b.AppendLine("                    $\"SELECT 1 FROM {scope.MemberOfViaCollection} WHERE {scope.MemberOfMatchColumn} = @val AND {scope.MemberOfActorColumn} = @actor LIMIT 1\",");
        b.AppendLine("                    [(\"@val\", resolved), (\"@actor\", ownSubjectId())], ct);");
        b.AppendLine("                if (member is null) return scope.Message ?? \"\";");
        b.AppendLine("            }");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        return null;");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    /// <summary>A dataSubject command (the aggregate id is the subject id). Erasure cannot be undone, so");
        b.AppendLine("    /// with no rule a command is refused, not allowed: only RequestErasure, and only as ErasureRequest");
        b.AppendLine("    /// says. A host that lets someone erase directly, hold, approve or place a legal hold decides that");
        b.AppendLine("    /// in its own authorize delegate before calling AuthorizeAsync.</summary>");
        b.AppendLine("    public static async Task<bool> DataSubjectAllowsAsync(");
        b.AppendLine("        string command, string subjectId, string ownRole, Func<string> ownSubjectId,");
        b.AppendLine("        IReadModelStore store, CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        if (ErasureRequest is not { } rule || !string.Equals(command, RequestErasureCommand, StringComparison.Ordinal)) return false;");
        b.AppendLine("        if (rule.Roles.Contains(ownRole, StringComparer.OrdinalIgnoreCase)) return true;");
        b.AppendLine("        if (!rule.Self) return false;");
        b.AppendLine();
        b.AppendLine("        var own = ownSubjectId();");
        b.AppendLine("        if (string.IsNullOrEmpty(own)) return false;");
        b.AppendLine("        if (rule.Via is not { } via) return string.Equals(own, subjectId, StringComparison.Ordinal);");
        b.AppendLine("        // Several rows may name the caller (a returning person has a new subject id): any of them will do.");
        b.AppendLine("        var owned = await ScalarStringAsync(store,");
        b.AppendLine("            $\"SELECT 1 FROM {via.ViaCollection} WHERE {via.KeyColumn} = @id AND {via.OwnerColumn} = @own LIMIT 1\",");
        b.AppendLine("            [(\"@id\", subjectId), (\"@own\", own)], ct);");
        b.AppendLine("        return owned is not null;");
        b.AppendLine("    }");
        b.AppendLine();
        // ReadAsync: an authorization check runs per request, alongside other requests and
        // the projections' writes (see IReadModelStore.ReadAsync).
        b.AppendLine("    private static Task<string?> ScalarStringAsync(IReadModelStore store, string sql, (string, object?)[] parameters, CancellationToken ct) =>");
        b.AppendLine("        store.ReadAsync(async (connection, readCt) =>");
        b.AppendLine("        {");
        b.AppendLine("            await using var cmd = connection.CreateCommand();");
        b.AppendLine("            cmd.CommandText = sql;");
        b.AppendLine("            foreach (var (name, value) in parameters) cmd.AddParam(name, value);");
        b.AppendLine("            var result = await cmd.ExecuteScalarAsync(readCt);");
        b.AppendLine("            return result is null or DBNull ? null : result.ToString();");
        b.AppendLine("        }, ct);");
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
        b.AppendLine("    string MemberOfViaCollection, string MemberOfMatchColumn, string MemberOfActorColumn, string[] BypassRoles,");
        b.AppendLine("    string? Message = null);");
        b.AppendLine();
        b.AppendLine("/// <summary>dataSubjects.erasure: Self lets a caller request their own subject's erasure (through Via when");
        b.AppendLine("/// set: the subject ids whose rows name the caller); Roles may request any subject's.</summary>");
        b.AppendLine("public sealed record ErasureRequestPolicy(bool Self, string[] Roles, OwnershipPolicy? Via = null);");

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
                $"\"{scope.MemberOfViaCollection}\", \"{ToSnakeCase(scope.MemberOfMatchField)}\", \"{ToSnakeCase(scope.MemberOfActorField)}\", {GenerationSupport.QuotedArray(scope.BypassRoles)}" +
                (scope.Message is null ? ")" : $", {Literal(scope.Message)})"));
        return "new(" + string.Join(", ", parts) + ")";
    }

    private static string ErasureRequestLiteral(ErasureRequestPolicy? policy)
    {
        if (policy is null) return "null";
        var via = policy.Via is { } v
            ? $", new(\"{v.ViaCollection}\", \"{ToSnakeCase(v.KeyField)}\", \"{ToSnakeCase(v.OwnerField)}\", [])"
            : "";
        return $"new({(policy.Self ? "true" : "false")}, {GenerationSupport.QuotedArray(policy.Roles)}{via})";
    }

    // Relaxed, as DeciderGenerator's own literals: an apostrophe stays readable in the generated code; quotes,
    // backslashes and control characters are still escaped.
    private static string Literal(string value) => System.Text.Json.JsonSerializer.Serialize(value, LiteralOptions);

    private static readonly System.Text.Json.JsonSerializerOptions LiteralOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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
