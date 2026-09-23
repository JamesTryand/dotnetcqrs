using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates one read-side HTTP query route per read model — the first generated
/// counterpart to <see cref="ProjectionGenerator"/>'s write side. Closes the gap this
/// project's own Group C item 4 execution plan found: <c>DotnetCqrs.Host</c>'s
/// <c>CqrsGatewayEndpoints</c> maps command dispatch only, and <c>readModel.scopes</c>
/// had zero production consumers before this — only the scenario-verify harness's own
/// SIMULATION of what a real query would do (<c>Verification/HarnessProgram.txt</c>'s
/// <c>SelectRowsAsync</c>). This generator is that real query, mirroring
/// <c>SelectRowsAsync</c>'s own SQL-building (a plain column equality per other
/// query key, a semi-join per declared <see cref="Domain.ReadModelScope"/>, a
/// <c>col &gt;= @from AND col &lt;= @to</c> range per declared
/// <see cref="Domain.ReadModelFilter"/>) rather than inventing a second shape, and
/// sharing the one genuinely drift-prone part outright: both this generator's emitted
/// code and the harness call <see cref="DateRangeResolver.ResolveBounds"/> for the
/// actual dateRange preset math, so "last7Days" can't quietly mean two different things.
///
/// <para><b>Query-string convention:</b> a plain field or a declared scope's param is an
/// ordinary string value (<c>?pmStaffId=s1</c>) — exactly how a scoped/unscoped
/// <c>queryParams</c> entry already works in a stateView scenario's own JSON. A declared
/// filter's param takes a JSON-encoded object value using the SAME <c>{ kind, from?,
/// to? }</c> shape a scenario's own <c>queryParams</c> already uses (schema 2.4.0's
/// documented runtime convention) — e.g. <c>?dateRange=%7B%22kind%22%3A%22last7Days%22%7D</c>.
/// One canonical representation for both the real route and the verify harness's
/// simulation of it, rather than inventing a second, HTTP-only shape.</para>
///
/// <para>Returns the raw route builder so the caller can chain
/// <c>.RequireAuthorization()</c> — same posture <c>MapCqrsGateway</c> already takes;
/// auth is out of scope here (deferred, per this project's own README, to whichever
/// stage wires a route into a real host).</para>
///
/// <para><b>Role gating (schema 2.7.0 <c>readModel.requiredRole</c>):</b> a declared
/// <see cref="Domain.ReadModel.RequiredRole"/> is checked directly inside this route's
/// own handler, not through a shared runtime policy table the way
/// <c>CommandAuthorizationGenerator</c>'s <c>CommandAuthorization.Policies</c> is —
/// that shape exists specifically because ONE shared gateway route dispatches every
/// command, so it needs a runtime <c>(aggregate, command)</c> lookup
/// (<c>CommandAuthorizationGenerator</c>'s own doc comment). Every read model already
/// gets its OWN generated route here, so its required role is a plain literal baked in
/// at generation time — no lookup needed, same posture this generator already takes for
/// a read model's own <see cref="Domain.ReadModelScope"/>/<see cref="Domain.ReadModelFilter"/>
/// SQL. Takes an optional <c>resolveOwnRole</c> delegate, same "pluggable hook, default
/// null means no check" precedent as <c>MapCqrsGateway</c>'s own <c>authorize</c>
/// parameter — wiring a real one (and <c>.RequireAuthorization()</c> for authentication
/// itself) is the operator's job, not this generator's; see
/// <c>HostProjectGenerator</c>'s generated <c>Program.cs</c> for where that wiring goes.
/// Deliberately does NOT force a <c>scopes</c> param (e.g. <c>pmStaffId</c>) to the
/// caller's own identity — this generator never has, for any read model — so there is
/// no interaction to reason about between the two: <c>requiredRole</c> gates the whole
/// route regardless of which params a request supplies, and a project needing
/// per-caller scope-forcing on top of that still hand-writes it, exactly as
/// <c>project/timesheets</c>'s own Phase 04g already did before this capability
/// existed.</para>
///
/// <para><b>Plain params are whitelisted.</b> Any other query key names a column, and that
/// name goes into the SQL text, so it must be one of the table's own columns (the key plus
/// each field); anything else gets a 400. Before this, an arbitrary key was interpolated
/// into the SQL as-is, which was injectable. With pii columns revealed by name, a
/// <c>UNION ... AS email</c> would also have had another table's ciphertext decrypted.</para>
///
/// <para><b>PII (Milestone D3):</b> a read model with <c>field.pii</c> columns gets a route
/// that takes <c>IKmsClient</c> (required) and <c>PiiRevealCache</c> (optional) from DI.
/// After the SQL runs, every pii cell on the page is revealed through
/// <c>PiiColumnRevealer</c>: one buffer, one flush, so at most one <c>decrypt-batch</c> per
/// subject. An erased subject's cell comes back as <c>{"$redacted":true}</c>. A plain
/// query param naming a pii column is a 400: the column holds non-deterministic
/// ciphertext, so equality could never match. Searching PII is the job of
/// <c>match</c> filters (D5/D6), not plain params.</para>
/// </summary>
public static class ReadModelQueryGenerator
{
    public static GeneratedFile Generate(Domain.Domain domain, Domain.ReadModel readModel)
    {
        var typeName = GenerationSupport.ExportName(readModel.Collection);
        // A pii column holds the {"$pii":...} envelope at rest (P4). The route reveals it
        // just before responding and refuses to filter on it, since ciphertext is
        // non-deterministic and can never match a query value.
        var piiColumns = readModel.Fields.Where(f => f.Pii).Select(f => ToSnakeCase(f.Name)).ToList();
        var hasPii = piiColumns.Count > 0;
        // Every column the projection creates (see ProjectionGenerator): the key plus each
        // field. A plain query param must name one of these; it is interpolated into the
        // SQL as a column name, so anything else would be an injection point.
        var columns = new[] { readModel.Key }.Concat(readModel.Fields.Select(f => f.Name))
            .Select(ToSnakeCase).Distinct().ToList();

        var b = new StringBuilder();
        b.AppendLine("using System.Security.Claims;");
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.Codegen.Generation;");
        if (hasPii) b.AppendLine("using DotnetCqrs.Crypto;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine("using Microsoft.AspNetCore.Builder;");
        b.AppendLine("using Microsoft.AspNetCore.Http;");
        if (hasPii) b.AppendLine("using Microsoft.AspNetCore.Mvc;");
        b.AppendLine("using Microsoft.AspNetCore.Routing;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Read-side query route for the \"{readModel.Collection}\" table -- see");
        b.AppendLine("/// ReadModelQueryGenerator's own doc comment for the query-string convention and role gating.</summary>");
        b.AppendLine($"public static class {typeName}QueryRoute");
        b.AppendLine("{");
        b.AppendLine("    /// <summary>This table's own columns: the only names a plain query param may use.</summary>");
        b.AppendLine($"    private static readonly string[] Columns = {GenerationSupport.QuotedArray(columns)};");
        b.AppendLine();
        if (hasPii)
        {
            b.AppendLine("    /// <summary>Columns holding the ciphertext envelope: revealed on the way out, never filtered on.</summary>");
            b.AppendLine($"    private static readonly string[] PiiColumns = {GenerationSupport.QuotedArray(piiColumns)};");
            b.AppendLine();
        }
        b.AppendLine($"    public static RouteHandlerBuilder Map{typeName}Route(this IEndpointRouteBuilder endpoints, string prefix = \"/api/query\", Func<ClaimsPrincipal, string>? resolveOwnRole = null)");
        b.AppendLine("    {");
        // IKmsClient is required, so a host that has PII but no key service fails the
        // request instead of serving envelopes as if they were values. The cache is
        // optional ([FromServices] on a nullable parameter), so a host without one
        // still works; it just makes a round trip for every read.
        var piiParams = hasPii ? ", [FromServices] IKmsClient kms, [FromServices] PiiRevealCache? cache" : "";
        b.AppendLine($"        return endpoints.MapGet($\"{{prefix}}/{readModel.Collection}\", async (HttpRequest request, IReadModelStore store{piiParams}, CancellationToken ct) =>");
        b.AppendLine("        {");
        if (readModel.RequiredRole is { Count: > 0 } requiredRole)
        {
            // A bare collection expression has no target type when `.Contains(...)` is
            // called on it directly (CS9176) -- an explicitly-typed local gives it one,
            // same fix CommandAuthorizationGenerator's own literal gets for free by
            // assigning into a declared `string[]?` record property instead.
            b.AppendLine("            if (resolveOwnRole is not null)");
            b.AppendLine("            {");
            b.AppendLine($"                string[] requiredRole = {GenerationSupport.QuotedArray(requiredRole)};");
            b.AppendLine("                var ownRole = resolveOwnRole(request.HttpContext.User);");
            b.AppendLine("                if (!requiredRole.Contains(ownRole, StringComparer.OrdinalIgnoreCase))");
            b.AppendLine("                    return Results.Problem(\"not authorized\", statusCode: StatusCodes.Status403Forbidden);");
            b.AppendLine("            }");
            b.AppendLine();
        }
        b.AppendLine("            var clauses = new List<string>();");
        b.AppendLine("            var parameters = new Dictionary<string, object?>();");
        b.AppendLine("            var i = 0;");
        b.AppendLine("            foreach (var (key, values) in request.Query)");
        b.AppendLine("            {");
        b.AppendLine("                var raw = values.ToString();");
        b.AppendLine("                switch (key)");
        b.AppendLine("                {");
        foreach (var filter in readModel.Filters)
        {
            b.AppendLine($"                    case \"{filter.Param}\":");
            b.AppendLine("                    {");
            b.AppendLine("                        var filterValue = JsonDocument.Parse(raw).RootElement;");
            b.AppendLine("                        var kind = filterValue.GetProperty(\"kind\").GetString()!;");
            b.AppendLine("                        var from = filterValue.TryGetProperty(\"from\", out var fromEl) ? fromEl.GetString() : null;");
            b.AppendLine("                        var to = filterValue.TryGetProperty(\"to\", out var toEl) ? toEl.GetString() : null;");
            b.AppendLine("                        var (rangeFrom, rangeTo) = DateRangeResolver.ResolveBounds(kind, from, to, DateOnly.FromDateTime(DateTime.UtcNow));");
            b.AppendLine("                        var fromParam = $\"@p{i++}\";");
            b.AppendLine("                        var toParam = $\"@p{i++}\";");
            b.AppendLine($"                        clauses.Add($\"{ToSnakeCase(filter.Field)} >= {{fromParam}} AND {ToSnakeCase(filter.Field)} <= {{toParam}}\");");
            b.AppendLine("                        parameters[fromParam] = rangeFrom;");
            b.AppendLine("                        parameters[toParam] = rangeTo;");
            b.AppendLine("                        break;");
            b.AppendLine("                    }");
        }
        foreach (var scope in readModel.Scopes)
        {
            b.AppendLine($"                    case \"{scope.Param}\":");
            b.AppendLine("                    {");
            b.AppendLine("                        var scopeParam = $\"@p{i++}\";");
            b.AppendLine($"                        clauses.Add($\"{ToSnakeCase(scope.FilterLocalField)} IN (SELECT {ToSnakeCase(scope.SelectField)} FROM {scope.ViaCollection} WHERE {ToSnakeCase(scope.MatchParamToField)} = {{scopeParam}})\");");
            b.AppendLine("                        parameters[scopeParam] = raw;");
            b.AppendLine("                        break;");
            b.AppendLine("                    }");
        }
        b.AppendLine("                    default:");
        b.AppendLine("                    {");
        b.AppendLine("                        // The key becomes a column name in the SQL text, so it must be one of this");
        b.AppendLine("                        // table's own columns and nothing else.");
        b.AppendLine("                        if (!Columns.Contains(ToSnakeCase(key)))");
        b.AppendLine("                            return Results.Problem($\"unknown query parameter '{key}'\", statusCode: StatusCodes.Status400BadRequest);");
        if (hasPii)
        {
            b.AppendLine("                        if (PiiColumns.Contains(ToSnakeCase(key)))");
            b.AppendLine("                            return Results.Problem($\"'{key}' is personal data and is stored encrypted, so it cannot be used as a query filter.\", statusCode: StatusCodes.Status400BadRequest);");
        }
        b.AppendLine("                        var plainParam = $\"@p{i++}\";");
        b.AppendLine("                        clauses.Add($\"{ToSnakeCase(key)} = {plainParam}\");");
        b.AppendLine("                        parameters[plainParam] = raw;");
        b.AppendLine("                        break;");
        b.AppendLine("                    }");
        b.AppendLine("                }");
        b.AppendLine("            }");
        b.AppendLine();
        b.AppendLine($"            var sql = clauses.Count == 0 ? \"SELECT * FROM {readModel.Collection}\" : \"SELECT * FROM {readModel.Collection} WHERE \" + string.Join(\" AND \", clauses);");
        b.AppendLine("            await using var command = store.Connection.CreateCommand();");
        b.AppendLine("            command.CommandText = sql;");
        b.AppendLine("            foreach (var (name, value) in parameters) command.AddParam(name, value);");
        b.AppendLine();
        b.AppendLine("            var rows = new List<Dictionary<string, object?>>();");
        b.AppendLine("            await using (var reader = await command.ExecuteReaderAsync(ct))");
        b.AppendLine("            {");
        b.AppendLine("                while (await reader.ReadAsync(ct))");
        b.AppendLine("                {");
        b.AppendLine("                    var row = new Dictionary<string, object?>();");
        b.AppendLine("                    for (var c = 0; c < reader.FieldCount; c++)");
        b.AppendLine("                        row[reader.GetName(c)] = reader.IsDBNull(c) ? null : reader.GetValue(c);");
        b.AppendLine("                    rows.Add(row);");
        b.AppendLine("                }");
        b.AppendLine("            }");
        if (hasPii)
            b.AppendLine("            await PiiColumnRevealer.RevealAsync(rows, PiiColumns, kms, cache, ct);");
        b.AppendLine("            return Results.Ok(rows);");
        b.AppendLine("        });");
        b.AppendLine("    }");
        b.AppendLine();
        // Runtime helper, unlike the private ToSnakeCase below (a GENERATOR-time
        // helper baking known column names into the switch cases above): an
        // unrecognized query key names an arbitrary column this generator has no way
        // to know ahead of time, so its snake-casing has to happen in the generated
        // code itself -- same duplication precedent as ProjectionGenerator/
        // HarnessProgram.txt's own copies of this exact transform.
        b.AppendLine("    private static string ToSnakeCase(string name)");
        b.AppendLine("    {");
        b.AppendLine("        var b = new System.Text.StringBuilder();");
        b.AppendLine("        for (var i = 0; i < name.Length; i++)");
        b.AppendLine("        {");
        b.AppendLine("            var c = name[i];");
        b.AppendLine("            if (char.IsUpper(c))");
        b.AppendLine("            {");
        b.AppendLine("                if (i > 0) b.Append('_');");
        b.AppendLine("                b.Append(char.ToLowerInvariant(c));");
        b.AppendLine("            }");
        b.AppendLine("            else");
        b.AppendLine("            {");
        b.AppendLine("                b.Append(c);");
        b.AppendLine("            }");
        b.AppendLine("        }");
        b.AppendLine("        return b.ToString();");
        b.AppendLine("    }");
        b.AppendLine("}");

        return new GeneratedFile($"{typeName}QueryRoute.cs", b.ToString());
    }

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
