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
/// <c>SelectRowsAsync</c>'s own SQL-building (a plain column equality per unrecognized
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
/// </summary>
public static class ReadModelQueryGenerator
{
    public static GeneratedFile Generate(Domain.Domain domain, Domain.ReadModel readModel)
    {
        var typeName = GenerationSupport.ExportName(readModel.Collection);

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.Codegen.Generation;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine("using Microsoft.AspNetCore.Builder;");
        b.AppendLine("using Microsoft.AspNetCore.Http;");
        b.AppendLine("using Microsoft.AspNetCore.Routing;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Read-side query route for the \"{readModel.Collection}\" table -- see");
        b.AppendLine("/// ReadModelQueryGenerator's own doc comment for the query-string convention.</summary>");
        b.AppendLine($"public static class {typeName}QueryRoute");
        b.AppendLine("{");
        b.AppendLine($"    public static RouteHandlerBuilder Map{typeName}Route(this IEndpointRouteBuilder endpoints, string prefix = \"/api/query\")");
        b.AppendLine("    {");
        b.AppendLine($"        return endpoints.MapGet($\"{{prefix}}/{readModel.Collection}\", async (HttpRequest request, IReadModelStore store, CancellationToken ct) =>");
        b.AppendLine("        {");
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
