using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates one <see cref="DotnetCqrs.Projections.IProjection"/> per read model —
/// ports pocketcqrs's <c>projectionGo</c>'s generic field-merge approach (not a
/// per-event-type-specific mapping, since the domain model doesn't record which event
/// sets which field): an incoming event's JSON payload is walked property by property,
/// and whichever of the read model's own columns happen to be present are written. A
/// field with a <see cref="Domain.Derivation"/> is the one exception: it is computed
/// from which EVENT TYPE fired (a toggle's literal 1/0, a count/sum's running total)
/// rather than copied from the payload, and a count/sum field is additionally keyed by
/// the firing event's OWN payload (<see cref="Domain.CountDerivation.RowKeyField"/>/
/// <see cref="Domain.SumDerivation.RowKeyField"/>) instead of <c>ev.AggregateId</c> --
/// those events live on a different stream by construction, which is the entire reason
/// a roll-up needs declaring at all (see eventmodelschema's <c>field.derivation</c>).
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
        // SeedOn is only ever narrower than On when a count/sum derivation adds
        // foreign-stream events to On without also making them seed-eligible (see
        // Domain.ReadModel.SeedOn's own doc comment) -- DocumentMapper is the only
        // place that builds a ReadModel, and it always keeps them equal otherwise, so
        // pre-existing (derivation-free) generated code is byte-for-byte unaffected.
        var seedOn = readModel.SeedOn;
        var keyColumn = ToSnakeCase(readModel.Key);
        var allColumns = readModel.Fields.Where(f => f.Name != readModel.Key).ToList();
        var plainColumns = allColumns.Where(f => f.Derivation is null).ToList();
        var toggleColumns = allColumns.Where(f => f.Derivation is Domain.ToggleDerivation).ToList();
        var rollupColumns = allColumns.Where(f => f.Derivation is Domain.CountDerivation or Domain.SumDerivation).ToList();
        var groupByColumns = allColumns.Where(f => f.Derivation is Domain.GroupByDerivation).ToList();
        var typeName = GenerationSupport.ExportName(readModel.Collection) + "Projection";

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        if (groupByColumns.Count > 0) b.AppendLine("using System.Text.Json.Nodes;");
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
        b.AppendLine($"                {keyColumn} TEXT PRIMARY KEY" + (allColumns.Count > 0 ? "," : ""));
        for (var i = 0; i < allColumns.Count; i++)
        {
            var field = allColumns[i];
            var comma = i < allColumns.Count - 1 ? "," : "";
            var sqlType = field.Derivation switch
            {
                Domain.CountDerivation => "INTEGER",
                Domain.SumDerivation => "REAL",
                // a pii column holds the {"$pii":…} envelope's raw JSON whatever the
                // field's own type -- see EmitSeedBlock.
                _ when field.Pii => "TEXT",
                _ => GenerationSupport.SqliteType(field.Type),
            };
            // A count/sum column is arithmetic (col = col +/- n) from the moment its
            // row exists, and SQL arithmetic against NULL yields NULL forever --
            // without a real starting value the very first increment would silently
            // vanish. A toggle's own declared `initial` is the same idea for 0/1.
            var defaultClause = field.Derivation switch
            {
                Domain.ToggleDerivation t => $" DEFAULT {(t.Initial ? 1 : 0)}",
                Domain.CountDerivation or Domain.SumDerivation => " DEFAULT 0",
                // an empty JSON array, not NULL -- a stateView query before any
                // contributing event has landed should see "no rows yet", not a
                // JsonException from trying to parse a null column.
                Domain.GroupByDerivation => " DEFAULT '[]'",
                _ => "",
            };
            b.AppendLine($"                {ToSnakeCase(field.Name)} {sqlType}{defaultClause}{comma}");
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
        b.AppendLine($"        if (ev.Type is not ({Disjunction(on)})) return;");
        b.AppendLine();
        b.AppendLine("        // The write-guard denies direct writes on every connection but the one that called");
        b.AppendLine("        // IReadModelStore.InstallWriteGuardAsync -- this IS that connection, but the guard");
        b.AppendLine("        // still fires unless a bypass scope is open, so this projection's own writes need one too.");
        b.AppendLine("        await using var bypass = await store.BeginBypassAsync(ct);");

        var seedIsUnconditional = seedOn.Count == on.Count;
        if (seedIsUnconditional)
        {
            b.AppendLine();
            EmitSeedBlock(b, readModel, keyColumn, plainColumns, toggleColumns);
        }
        else if (seedOn.Count > 0)
        {
            b.AppendLine();
            b.AppendLine($"        if (ev.Type is ({Disjunction(seedOn)}))");
            b.AppendLine("        {");
            EmitSeedBlock(b, readModel, keyColumn, plainColumns, toggleColumns);
            b.AppendLine("        }");
        }
        // seedOn.Count == 0: every reacted-to event is a foreign-stream roll-up driver
        // (or a count/sum-only read model, flagged in DocumentMapper's own mapping
        // report) -- nothing here seeds a row by aggregate id at all.

        foreach (var field in rollupColumns)
        {
            var column = ToSnakeCase(field.Name);
            switch (field.Derivation)
            {
                case Domain.CountDerivation count:
                    if (count.IncrementOnEvents.Count > 0)
                        EmitRollup(b, readModel, keyColumn, column, count.RowKeyField, count.IncrementOnEvents, $"{column} + 1");
                    if (count.DecrementOnEvents.Count > 0)
                        EmitRollup(b, readModel, keyColumn, column, count.RowKeyField, count.DecrementOnEvents, $"{column} - 1");
                    break;
                case Domain.SumDerivation sum:
                    if (sum.AddOnEvents.Count > 0)
                        EmitRollup(b, readModel, keyColumn, column, sum.RowKeyField, sum.AddOnEvents, $"{column} + @amount", sum.AmountField);
                    if (sum.SubtractOnEvents.Count > 0)
                        EmitRollup(b, readModel, keyColumn, column, sum.RowKeyField, sum.SubtractOnEvents, $"{column} - @amount", sum.AmountField);
                    break;
            }
        }

        foreach (var field in groupByColumns)
        {
            var column = ToSnakeCase(field.Name);
            var groupBy = (Domain.GroupByDerivation)field.Derivation!;
            var groupKeyColumn = ToSnakeCase(groupBy.GroupByField);
            foreach (var subfield in groupBy.Subfields)
            {
                var subColumn = ToSnakeCase(subfield.Name);
                switch (subfield.Derivation)
                {
                    case Domain.CountDerivation count:
                        if (count.IncrementOnEvents.Count > 0)
                            EmitGroupByFold(b, readModel, keyColumn, column, groupKeyColumn, groupBy.GroupByField, count.RowKeyField,
                                subColumn, count.IncrementOnEvents, "current + 1", amountField: null);
                        if (count.DecrementOnEvents.Count > 0)
                            EmitGroupByFold(b, readModel, keyColumn, column, groupKeyColumn, groupBy.GroupByField, count.RowKeyField,
                                subColumn, count.DecrementOnEvents, "current - 1", amountField: null);
                        break;
                    case Domain.SumDerivation sum:
                        if (sum.AddOnEvents.Count > 0)
                            EmitGroupByFold(b, readModel, keyColumn, column, groupKeyColumn, groupBy.GroupByField, sum.RowKeyField,
                                subColumn, sum.AddOnEvents, "current + amount", sum.AmountField);
                        if (sum.SubtractOnEvents.Count > 0)
                            EmitGroupByFold(b, readModel, keyColumn, column, groupKeyColumn, groupBy.GroupByField, sum.RowKeyField,
                                subColumn, sum.SubtractOnEvents, "current - amount", sum.AmountField);
                        break;
                    // Domain.ToggleDerivation/null: DocumentMapper never produces a groupBy
                    // subfield with either -- a toggle is rejected at mapping time (see its
                    // own doc comment), and the field named by groupByField itself carries
                    // no derivation on purpose (its value is the group key, already set when
                    // ApplyGroupByAsync creates the entry).
                }
            }
        }

        b.AppendLine("    }");

        if (groupByColumns.Count > 0)
            EmitApplyGroupByHelper(b);

        b.AppendLine("}");

        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }

    /// <summary>Seeds/keeps the row for an OWN-stream event (<c>ev.AggregateId</c> is a
    /// valid key for it) and applies every plain-copy and toggle column -- exactly the
    /// generic field-merge this generator always did, before any derivation existed.</summary>
    private static void EmitSeedBlock(StringBuilder b, Domain.ReadModel readModel, string keyColumn,
        IReadOnlyList<Domain.Field> plainColumns, IReadOnlyList<Domain.Field> toggleColumns)
    {
        if (plainColumns.Count > 0)
        {
            // only declared when there's a column to read it into -- a read model with
            // no plain fields (columns.Count == 0) has nothing to deserialize ev.Data
            // for, and an unused local would be a compiler warning in every file
            // generated for a key-only/toggle-only read model.
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

        if (plainColumns.Count == 0 && toggleColumns.Count == 0) return;

        b.AppendLine();
        b.AppendLine("        var setClauses = new List<string>();");
        b.AppendLine("        await using var update = store.Connection.CreateCommand();");
        b.AppendLine("        update.AddParam(\"@id\", ev.AggregateId);");
        foreach (var field in plainColumns)
        {
            var column = ToSnakeCase(field.Name);
            var valueVar = field.Name + "Value";
            b.AppendLine($"        if (data.TryGetValue(\"{field.Name}\", out var {valueVar}))");
            b.AppendLine("        {");
            b.AppendLine($"            setClauses.Add(\"{column} = @{field.Name}\");");
            // A pii field arrives as the {"$pii":{"s":…,"c":…}} envelope (an object), not
            // a scalar, so it is stored as that envelope's raw JSON: ciphertext at rest,
            // revealed (or not) at query time. DocumentMapper.CheckReadModelPii guarantees
            // a non-pii column never receives an envelope.
            var accessor = field.Pii ? $"{valueVar}.GetRawText()" : JsonElementAccessor(field.Type, valueVar);
            b.AppendLine($"            update.AddParam(\"@{field.Name}\", {accessor});");
            b.AppendLine("        }");
        }
        foreach (var field in toggleColumns)
        {
            var toggle = (Domain.ToggleDerivation)field.Derivation!;
            var column = ToSnakeCase(field.Name);
            if (toggle.OnEvents.Count > 0)
            {
                b.AppendLine($"        if (ev.Type is ({Disjunction(toggle.OnEvents)}))");
                b.AppendLine($"            setClauses.Add(\"{column} = 1\");");
            }
            if (toggle.OffEvents.Count > 0)
            {
                b.AppendLine($"        if (ev.Type is ({Disjunction(toggle.OffEvents)}))");
                b.AppendLine($"            setClauses.Add(\"{column} = 0\");");
            }
        }
        b.AppendLine("        if (setClauses.Count > 0)");
        b.AppendLine("        {");
        b.AppendLine("            update.CommandText = \"UPDATE " + readModel.Collection + " SET \" + string.Join(\", \", setClauses) + \" WHERE " + keyColumn + " = @id\";");
        b.AppendLine("            await update.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
    }

    /// <summary>Emits one guarded UPDATE for a <c>count</c>/<c>sum</c> derivation's
    /// increment/decrement (or add/subtract) event set. <paramref name="rowKeyField"/>
    /// names the payload field on the firing event to EXTRACT the target row's key
    /// VALUE from -- it is not itself a column name, and may differ from this read
    /// model's own key field (the schema's documented override case, e.g. an event
    /// payload calling it <c>forProjectId</c> while the read model's own key is
    /// <c>projectId</c>). The WHERE clause always filters on <paramref name="keyColumn"/>,
    /// this read model's own physical key column, never a column derived from
    /// <paramref name="rowKeyField"/>'s name -- these events live on a different stream
    /// by construction (see this file's own doc comment), never <c>ev.AggregateId</c>.
    /// Row existence is NOT this block's job -- the target row is created by its own
    /// aggregate's normal seed path (<see cref="EmitSeedBlock"/>); an UPDATE against a
    /// row that doesn't exist yet is simply a no-op, same as a plain-copy field's WHERE
    /// would be.</summary>
    private static void EmitRollup(StringBuilder b, Domain.ReadModel readModel, string keyColumn, string column,
        string rowKeyField, IReadOnlyList<string> eventTypes, string setExpression, string? amountField = null)
    {
        b.AppendLine();
        b.AppendLine($"        if (ev.Type is ({Disjunction(eventTypes)}))");
        b.AppendLine("        {");
        b.AppendLine("            var rollupData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
        b.AppendLine("            await using var rollup = store.Connection.CreateCommand();");
        b.AppendLine($"            rollup.CommandText = \"UPDATE {readModel.Collection} SET {column} = {setExpression} WHERE {keyColumn} = @rowKey\";");
        b.AppendLine($"            rollup.AddParam(\"@rowKey\", rollupData[\"{rowKeyField}\"].GetString());");
        if (amountField is not null)
            b.AppendLine($"            rollup.AddParam(\"@amount\", rollupData[\"{amountField}\"].GetDouble());");
        b.AppendLine("            await rollup.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
    }

    /// <summary>Emits one guarded read-modify-write for a <c>groupBy</c> field's
    /// subfield fold (schema 2.3.0). Unlike <see cref="EmitRollup"/>'s plain SQL
    /// arithmetic, the target column holds a JSON array (one object per distinct
    /// <paramref name="groupByField"/> value), which SQLite has no arithmetic over --
    /// <see cref="EmitApplyGroupByHelper"/>'s <c>ApplyGroupByAsync</c> does the
    /// SELECT/parse/find-or-create-entry/UPDATE round trip; this method only supplies
    /// the per-subfield delta as a closure. <paramref name="rowKeyField"/> plays the
    /// exact same role as <see cref="EmitRollup"/>'s own (which TOP-LEVEL row the
    /// contributing event targets) -- <paramref name="groupByField"/> is unrelated,
    /// naming the payload field whose value picks the entry WITHIN that row's list.</summary>
    private static void EmitGroupByFold(StringBuilder b, Domain.ReadModel readModel, string keyColumn, string column,
        string groupKeyColumn, string groupByField, string rowKeyField, string subColumn,
        IReadOnlyList<string> eventTypes, string deltaExpression, string? amountField)
    {
        b.AppendLine();
        b.AppendLine($"        if (ev.Type is ({Disjunction(eventTypes)}))");
        b.AppendLine("        {");
        b.AppendLine("            var groupByData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
        if (amountField is not null)
            b.AppendLine($"            var amount = groupByData[\"{amountField}\"].GetDouble();");
        b.AppendLine($"            await ApplyGroupByAsync(store, \"{readModel.Collection}\", \"{keyColumn}\", groupByData[\"{rowKeyField}\"].GetString()!,");
        b.AppendLine($"                \"{column}\", \"{groupKeyColumn}\", groupByData[\"{groupByField}\"].GetString()!, ct, entry =>");
        b.AppendLine("                {");
        b.AppendLine($"                    var current = entry[\"{subColumn}\"]?.GetValue<double>() ?? 0;");
        b.AppendLine($"                    entry[\"{subColumn}\"] = {deltaExpression};");
        b.AppendLine("                });");
        b.AppendLine("        }");
    }

    /// <summary>Emitted once per file, only when the read model declares at least one
    /// <c>groupBy</c> field: finds-or-creates the JSON entry (keyed by
    /// <paramref name="groupKeyColumn"/>'s value) inside <paramref name="column"/>'s
    /// current array, applies the caller's delta to it, then writes the whole array
    /// back. Uses <see cref="System.Text.Json.Nodes.JsonNode"/> (mutable), not
    /// <see cref="JsonElement"/> (immutable, everywhere else in this generator) --
    /// building a modified nested list needs real mutation, not just reading.</summary>
    private static void EmitApplyGroupByHelper(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("    private static async Task ApplyGroupByAsync(IReadModelStore store, string table, string keyColumn, string rowKey,");
        b.AppendLine("        string column, string groupKeyColumn, string groupKeyValue, CancellationToken ct, Action<JsonObject> apply)");
        b.AppendLine("    {");
        b.AppendLine("        string? currentJson;");
        b.AppendLine("        await using (var select = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine("            select.CommandText = $\"SELECT {column} FROM {table} WHERE {keyColumn} = @id\";");
        b.AppendLine("            select.AddParam(\"@id\", rowKey);");
        b.AppendLine("            currentJson = (string?)await select.ExecuteScalarAsync(ct);");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        var rows = string.IsNullOrEmpty(currentJson) ? new JsonArray() : JsonNode.Parse(currentJson)!.AsArray();");
        b.AppendLine("        var entry = rows.OfType<JsonObject>().FirstOrDefault(o => o[groupKeyColumn]?.GetValue<string>() == groupKeyValue);");
        b.AppendLine("        if (entry is null)");
        b.AppendLine("        {");
        b.AppendLine("            entry = new JsonObject { [groupKeyColumn] = groupKeyValue };");
        b.AppendLine("            rows.Add(entry);");
        b.AppendLine("        }");
        b.AppendLine("        apply(entry);");
        b.AppendLine();
        b.AppendLine("        await using var update = store.Connection.CreateCommand();");
        b.AppendLine("        update.CommandText = $\"UPDATE {table} SET {column} = @value WHERE {keyColumn} = @id\";");
        b.AppendLine("        update.AddParam(\"@value\", rows.ToJsonString());");
        b.AppendLine("        update.AddParam(\"@id\", rowKey);");
        b.AppendLine("        await update.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
    }

    private static string Disjunction(IEnumerable<string> eventTypes) =>
        string.Join(" or ", eventTypes.Select(n => $"\"{n}\""));

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
