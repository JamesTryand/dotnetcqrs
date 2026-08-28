using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates one <see cref="DotnetCqrs.Projections.IProjection"/> per read model —
/// ports pocketcqrs's <c>projectionGo</c>'s generic field-merge approach (not a
/// per-event-type-specific mapping, since the domain model doesn't record which event
/// sets which field): an incoming event's JSON payload is walked property by property,
/// and whichever of the read model's own columns happen to be present are written.
/// Adapted to dotnetcqrs's actual read-model shape (an <see cref="DotnetCqrs.ReadModels.IReadModelStore"/>:
/// a provider-neutral <see cref="System.Data.Common.DbConnection"/> plus a write-guard
/// bypass scope) rather than pocketcqrs's PocketBase records — the same shape
/// <c>samples/OrderFulfillment</c>'s hand-written projections already use.
/// </summary>
internal static class ProjectionGenerator
{
    public static GeneratedFile Generate(Domain.Domain domain, Domain.ReadModel readModel)
    {
        var on = readModel.On.Count > 0 ? readModel.On : domain.Events();
        var keyColumn = ToSnakeCase(readModel.Key);
        var columns = readModel.Fields.Where(f => f.Name != readModel.Key).ToList();
        var typeName = GenerationSupport.ExportName(readModel.Collection) + "Projection";

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.Projections;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Projects \"{domain.Aggregate}\" events into the \"{readModel.Collection}\" table, one row");
        b.AppendLine($"/// per {domain.Aggregate} stream keyed by the aggregate id -- a generic field-merge. Port");
        b.AppendLine("/// your own per-event rules once they've settled.</summary>");
        b.AppendLine($"public sealed class {typeName}(IReadModelStore store) : IProjection");
        b.AppendLine("{");
        b.AppendLine($"    public string Name => \"{readModel.Collection}\";");
        b.AppendLine($"    public IReadOnlyList<string> Tables => [\"{readModel.Collection}\"];");
        b.AppendLine();
        b.AppendLine("    public async Task InitAsync(CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = store.Connection.CreateCommand();");
        b.AppendLine("        command.CommandText = \"\"\"");
        b.AppendLine($"            CREATE TABLE IF NOT EXISTS {readModel.Collection} (");
        b.AppendLine($"                {keyColumn} TEXT PRIMARY KEY" + (columns.Count > 0 ? "," : ""));
        for (var i = 0; i < columns.Count; i++)
        {
            var field = columns[i];
            var comma = i < columns.Count - 1 ? "," : "";
            b.AppendLine($"                {ToSnakeCase(field.Name)} {GenerationSupport.SqliteType(field.Type)}{comma}");
        }
        b.AppendLine("            )");
        b.AppendLine("            \"\"\";");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    public async Task ApplyAsync(Event ev, CancellationToken ct)");
        b.AppendLine("    {");
        // String literals, not the aggregate's own event constants: On may
        // legitimately name another aggregate's events entirely (read models are
        // cross-cutting), which this package's decider would not have a constant for.
        b.AppendLine($"        if (ev.Type is not ({string.Join(" or ", on.Select(n => $"\"{n}\""))})) return;");
        b.AppendLine();
        b.AppendLine("        // The write-guard denies direct writes on every connection but the one that called");
        b.AppendLine("        // IReadModelStore.InstallWriteGuardAsync -- this IS that connection, but the guard");
        b.AppendLine("        // still fires unless a bypass scope is open, so this projection's own writes need one too.");
        b.AppendLine("        await using var bypass = await store.BeginBypassAsync(ct);");
        b.AppendLine();
        if (columns.Count > 0)
        {
            // only declared when there's a column to read it into -- a read model with
            // no fields beyond its key (columns.Count == 0) has nothing to deserialize
            // ev.Data for, and an unused local would be a compiler warning in every
            // file generated for a key-only read model.
            b.AppendLine("        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
            b.AppendLine();
        }
        b.AppendLine("        await using (var insert = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine("            insert.CommandText = \"\"\"");
        b.AppendLine($"                INSERT INTO {readModel.Collection} ({keyColumn}) VALUES (@id)");
        b.AppendLine($"                ON CONFLICT ({keyColumn}) DO NOTHING");
        b.AppendLine("                \"\"\";");
        b.AppendLine("            insert.AddParam(\"@id\", ev.AggregateId);");
        b.AppendLine("            await insert.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");

        if (columns.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("        var setClauses = new List<string>();");
            b.AppendLine("        await using var update = store.Connection.CreateCommand();");
            b.AppendLine("        update.AddParam(\"@id\", ev.AggregateId);");
            foreach (var field in columns)
            {
                var column = ToSnakeCase(field.Name);
                var valueVar = field.Name + "Value";
                b.AppendLine($"        if (data.TryGetValue(\"{field.Name}\", out var {valueVar}))");
                b.AppendLine("        {");
                b.AppendLine($"            setClauses.Add(\"{column} = @{field.Name}\");");
                b.AppendLine($"            update.AddParam(\"@{field.Name}\", {JsonElementAccessor(field.Type, valueVar)});");
                b.AppendLine("        }");
            }
            b.AppendLine("        if (setClauses.Count > 0)");
            b.AppendLine("        {");
            b.AppendLine("            update.CommandText = \"UPDATE " + readModel.Collection + " SET \" + string.Join(\", \", setClauses) + \" WHERE " + keyColumn + " = @id\";");
            b.AppendLine("            await update.ExecuteNonQueryAsync(ct);");
            b.AppendLine("        }");
        }

        b.AppendLine("    }");
        b.AppendLine("}");

        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }

    private static string JsonElementAccessor(string domainType, string variable) => domainType switch
    {
        "number" => $"{variable}.GetDouble()",
        "bool" => $"{variable}.GetBoolean()",
        "json" => $"{variable}.GetRawText()",
        _ => $"{variable}.GetString()", // text, date
    };

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
