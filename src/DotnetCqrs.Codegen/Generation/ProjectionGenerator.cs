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
        if (readModel.RowsPerListItem is { } perItem)
            return GenerateRowsPerListItem(domain, readModel, perItem);
        var on = readModel.On.Count > 0 ? readModel.On : domain.Events();
        // SeedOn is only ever narrower than On when a count/sum derivation adds
        // foreign-stream events to On without also making them seed-eligible (see
        // Domain.ReadModel.SeedOn's own doc comment) -- DocumentMapper is the only
        // place that builds a ReadModel, and it always keeps them equal otherwise, so
        // pre-existing (derivation-free) generated code is byte-for-byte unaffected.
        var seedOn = readModel.SeedOn;
        var keyColumn = ToSnakeCase(readModel.Key);
        // Schema 3.14.0: the row a seed or removal event names, by default its own stream.
        var rowKey = readModel.RowKeyField is null ? "ev.AggregateId" : "RowKey(ev)";
        var allColumns = readModel.Fields.Where(f => f.Name != readModel.Key).ToList();
        var plainColumns = allColumns.Where(f => f.Derivation is null).ToList();
        var toggleColumns = allColumns.Where(f => f.Derivation is Domain.ToggleDerivation).ToList();
        var rollupColumns = allColumns.Where(f => f.Derivation is Domain.CountDerivation or Domain.SumDerivation).ToList();
        var groupByColumns = allColumns.Where(f => f.Derivation is Domain.GroupByDerivation).ToList();
        var byEventColumns = allColumns.Where(f => f.Derivation is Domain.ByEventDerivation).ToList();
        var typeName = GenerationSupport.ExportName(readModel.Collection) + "Projection";
        // Schema 3.1.0 match filters on non-pii fields search a normalized copy of the field
        // (a shadow column), because SQL can't apply the normalizer itself.
        var shadowFilters = GenerationSupport.ShadowMatchFilters(readModel).ToList();

        var b = new StringBuilder();
        if (rollupColumns.Count > 0 || groupByColumns.Count > 0) b.AppendLine("using System.Globalization;");
        b.AppendLine("using System.Text.Json;");
        if (groupByColumns.Count > 0) b.AppendLine("using System.Text.Json.Nodes;");
        if (shadowFilters.Count > 0) b.AppendLine("using DotnetCqrs.Codegen.Generation;");
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
        // R2: a rollup's contributions, one per event and column, so its totals are worked out from them (idempotent
        // under redelivery, and whenever the row is seeded). Part of the projection: a rebuild clears it too.
        var hasRollups = rollupColumns.Count > 0 || groupByColumns.Count > 0;
        var contributions = readModel.Collection + "_contributions";
        b.AppendLine(hasRollups
            ? $"    public IReadOnlyList<string> Tables => [\"{readModel.Collection}\", \"{contributions}\"];"
            : $"    public IReadOnlyList<string> Tables => [\"{readModel.Collection}\"];");
        b.AppendLine();
        b.AppendLine("    public async Task InitAsync(CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = store.Connection.CreateCommand();");
        b.AppendLine("        command.CommandText = \"\"\"");
        b.AppendLine($"            CREATE TABLE IF NOT EXISTS {readModel.Collection} (");
        var columnCount = allColumns.Count + shadowFilters.Count;
        b.AppendLine($"                {keyColumn} TEXT PRIMARY KEY" + (columnCount > 0 ? "," : ""));
        for (var i = 0; i < allColumns.Count; i++)
        {
            var field = allColumns[i];
            var comma = i < columnCount - 1 ? "," : "";
            // Type names valid on SQLite and Postgres alike -- see GenerationSupport.ColumnType.
            var sqlType = field.Derivation switch
            {
                Domain.CountDerivation => "BIGINT",
                Domain.SumDerivation => field.Type == "decimal" ? "NUMERIC" : "DOUBLE PRECISION",
                // a pii column holds the {"$pii":…} envelope's raw JSON whatever the
                // field's own type -- see EmitSeedBlock.
                _ when field.Pii => "TEXT",
                _ => GenerationSupport.ColumnType(field.Type),
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
        for (var i = 0; i < shadowFilters.Count; i++)
        {
            var comma = allColumns.Count + i < columnCount - 1 ? "," : "";
            b.AppendLine($"                {GenerationSupport.MatchColumn(shadowFilters[i].Field, shadowFilters[i].Normalize!)} TEXT{comma}");
        }
        b.AppendLine("            )");
        b.AppendLine("            \"\"\";");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        if (hasRollups)
        {
            b.AppendLine($"        foreach (var sql in new[] {{ \"CREATE TABLE IF NOT EXISTS {contributions} (event_id TEXT NOT NULL, column_name TEXT NOT NULL, row_key TEXT NOT NULL, group_key TEXT, amount TEXT NOT NULL, seq BIGINT NOT NULL, PRIMARY KEY (event_id, column_name))\",");
            b.AppendLine($"            \"CREATE INDEX IF NOT EXISTS ix_{contributions}_row ON {contributions} (row_key, seq)\" }})");
            b.AppendLine("        {");
            b.AppendLine("            await using var create = store.Connection.CreateCommand();");
            b.AppendLine("            create.CommandText = sql;");
            b.AppendLine("            await create.ExecuteNonQueryAsync(ct);");
            b.AppendLine("        }");
        }
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

        if (readModel.RemovedOn.Count > 0)
        {
            // Schema 3.9.0 removedByEventIds: the row this event's own stream keys is deleted;
            // a later seed event for that stream starts it afresh.
            b.AppendLine();
            b.AppendLine($"        if (ev.Type is ({Disjunction(readModel.RemovedOn)}))");
            b.AppendLine("        {");
            b.AppendLine("            await using var remove = store.Connection.CreateCommand();");
            b.AppendLine($"            remove.CommandText = \"DELETE FROM {readModel.Collection} WHERE {keyColumn} = @id\";");
            b.AppendLine($"            remove.AddParam(\"@id\", {rowKey});");
            b.AppendLine("            await remove.ExecuteNonQueryAsync(ct);");
            if (hasRollups)
            {
                // A removed row's contributions go with it: seeded again, it starts afresh.
                b.AppendLine("            await using var forget = store.Connection.CreateCommand();");
                b.AppendLine($"            forget.CommandText = \"DELETE FROM {contributions} WHERE row_key = @id\";");
                b.AppendLine($"            forget.AddParam(\"@id\", {rowKey});");
                b.AppendLine("            await forget.ExecuteNonQueryAsync(ct);");
            }
            b.AppendLine("            return;");
            b.AppendLine("        }");
        }

        var seedIsUnconditional = seedOn.Count == on.Count;
        if (seedIsUnconditional)
        {
            b.AppendLine();
            EmitSeedBlock(b, readModel, keyColumn, plainColumns, toggleColumns, byEventColumns, rowKey);
        }
        else if (seedOn.Count > 0)
        {
            b.AppendLine();
            b.AppendLine($"        if (ev.Type is ({Disjunction(seedOn)}))");
            b.AppendLine("        {");
            EmitSeedBlock(b, readModel, keyColumn, plainColumns, toggleColumns, byEventColumns, rowKey);
            // A row seeded after some of its contributions arrived (they come from other streams) gets them now. When
            // every event seeds its own row, a contribution never comes before its row, so there is nothing to catch up.
            if (hasRollups) b.AppendLine($"            await RecomputeAsync({rowKey}, ct);");
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
                        EmitRollup(b, column, count.RowKeyField, count.IncrementOnEvents, "1m");
                    if (count.DecrementOnEvents.Count > 0)
                        EmitRollup(b, column, count.RowKeyField, count.DecrementOnEvents, "-1m");
                    break;
                case Domain.SumDerivation sum:
                    if (sum.AddOnEvents.Count > 0)
                        EmitRollup(b, column, sum.RowKeyField, sum.AddOnEvents, $"rollupData[\"{sum.AmountField}\"].GetDecimal()");
                    if (sum.SubtractOnEvents.Count > 0)
                        EmitRollup(b, column, sum.RowKeyField, sum.SubtractOnEvents, $"-rollupData[\"{sum.AmountField}\"].GetDecimal()");
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
                            EmitGroupByFold(b, column, groupBy.GroupByField, count.RowKeyField, subColumn, count.IncrementOnEvents, "1m");
                        if (count.DecrementOnEvents.Count > 0)
                            EmitGroupByFold(b, column, groupBy.GroupByField, count.RowKeyField, subColumn, count.DecrementOnEvents, "-1m");
                        break;
                    case Domain.SumDerivation sum:
                        if (sum.AddOnEvents.Count > 0)
                            EmitGroupByFold(b, column, groupBy.GroupByField, sum.RowKeyField, subColumn, sum.AddOnEvents,
                                $"groupByData[\"{sum.AmountField}\"].GetDecimal()");
                        if (sum.SubtractOnEvents.Count > 0)
                            EmitGroupByFold(b, column, groupBy.GroupByField, sum.RowKeyField, subColumn, sum.SubtractOnEvents,
                                $"-groupByData[\"{sum.AmountField}\"].GetDecimal()");
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

        if (hasRollups)
            EmitRollupHelpers(b, readModel.Collection, keyColumn, contributions, rollupColumns, groupByColumns);
        if (readModel.RowKeyField is { } rowKeyField)
        {
            // The mapper requires every seed and removal event to carry the field as text.
            b.AppendLine();
            b.AppendLine($"    /// <summary>The row an event names (schema 3.14.0 rowKeyField \"{rowKeyField}\"), in place of its stream.</summary>");
            b.AppendLine("    private static string RowKey(Event ev) =>");
            b.AppendLine($"        (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [])[\"{rowKeyField}\"].GetString()!;");
        }

        b.AppendLine("}");

        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }

    /// <summary>Seeds/keeps the row for an OWN-stream event (<c>ev.AggregateId</c> is a
    /// valid key for it) and applies every plain-copy and toggle column -- exactly the
    /// generic field-merge this generator always did, before any derivation existed.</summary>
    private static void EmitSeedBlock(StringBuilder b, Domain.ReadModel readModel, string keyColumn,
        IReadOnlyList<Domain.Field> plainColumns, IReadOnlyList<Domain.Field> toggleColumns, IReadOnlyList<Domain.Field> byEventColumns, string rowKey)
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
        b.AppendLine($"            insert.AddParam(\"@id\", {rowKey});");
        b.AppendLine("            await insert.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");

        if (plainColumns.Count == 0 && toggleColumns.Count == 0 && byEventColumns.Count == 0) return;

        b.AppendLine();
        b.AppendLine("        var setClauses = new List<string>();");
        b.AppendLine("        await using var update = store.Connection.CreateCommand();");
        b.AppendLine($"        update.AddParam(\"@id\", {rowKey});");
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
            // A match filter's shadow column follows its field in the same UPDATE, so the
            // two can never disagree. DocumentMapper only allows match on a plain text field.
            foreach (var filter in GenerationSupport.ShadowMatchFilters(readModel).Where(f => f.Field == field.Name))
            {
                var shadow = GenerationSupport.MatchColumn(filter.Field, filter.Normalize!);
                b.AppendLine($"            setClauses.Add(\"{shadow} = @{shadow}\");");
                b.AppendLine($"            update.AddParam(\"@{shadow}\", {valueVar}.ValueKind == JsonValueKind.String");
                b.AppendLine($"                ? MatchNormalizer.Normalize(\"{filter.Normalize}\", {valueVar}.GetString()!) : null);");
            }
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
        foreach (var field in byEventColumns)
            EmitByEvent(b, field);
        b.AppendLine("        if (setClauses.Count > 0)");
        b.AppendLine("        {");
        b.AppendLine("            update.CommandText = \"UPDATE " + readModel.Collection + " SET \" + string.Join(\", \", setClauses) + \" WHERE " + keyColumn + " = @id\";");
        b.AppendLine("            await update.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
    }

    /// <summary>Emits one count/sum roll-up's contribution: the firing event adds <paramref name="amountExpression"/>
    /// (1 or -1 for a count, the payload's amount or its negation for a sum) to <paramref name="column"/> of the row
    /// <paramref name="rowKeyField"/> names (these events live on another stream by construction, so never
    /// <c>ev.AggregateId</c>). The contribution is recorded once per event and column, and the row's totals are
    /// worked out from its contributions (R2): a redelivery adds nothing, and a row that doesn't exist yet gets its
    /// contributions when it is seeded.</summary>
    private static void EmitRollup(StringBuilder b, string column, string rowKeyField, IReadOnlyList<string> eventTypes, string amountExpression)
    {
        b.AppendLine();
        b.AppendLine($"        if (ev.Type is ({Disjunction(eventTypes)}))");
        b.AppendLine("        {");
        b.AppendLine("            var rollupData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
        b.AppendLine($"            var rollupKey = rollupData[\"{rowKeyField}\"].GetString()!;");
        b.AppendLine($"            await ContributeAsync(ev, \"{column}\", rollupKey, null, {amountExpression}, ct);");
        b.AppendLine("            await RecomputeAsync(rollupKey, ct);");
        b.AppendLine("        }");
    }

    /// <summary>Emits one <c>groupBy</c> subfield's contribution (schema 2.3.0): as <see cref="EmitRollup"/>, recorded
    /// under <c>&lt;column&gt;.&lt;subfield&gt;</c> with the group <paramref name="groupByField"/> names, so the row's list
    /// (one entry per group, in the order groups first appeared) is worked out from its contributions too.</summary>
    private static void EmitGroupByFold(StringBuilder b, string column, string groupByField, string rowKeyField, string subColumn,
        IReadOnlyList<string> eventTypes, string amountExpression)
    {
        b.AppendLine();
        b.AppendLine($"        if (ev.Type is ({Disjunction(eventTypes)}))");
        b.AppendLine("        {");
        b.AppendLine("            var groupByData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
        b.AppendLine($"            var groupByKey = groupByData[\"{rowKeyField}\"].GetString()!;");
        b.AppendLine($"            await ContributeAsync(ev, \"{column}.{subColumn}\", groupByKey, groupByData[\"{groupByField}\"].GetString()!, {amountExpression}, ct);");
        b.AppendLine("            await RecomputeAsync(groupByKey, ct);");
        b.AppendLine("        }");
    }

    /// <summary>Emitted once per file when the read model has a roll-up: <c>ContributeAsync</c> records a contribution
    /// once (by event and column), and <c>RecomputeAsync</c> sets every roll-up column of a row from its contributions,
    /// summed as decimals (exact; a double column gets the decimal total as a double). A count is a whole number; a
    /// <c>groupBy</c> list has one entry per group, in the order groups first appeared, each holding the subfields
    /// that group has contributions for.</summary>
    private static void EmitRollupHelpers(StringBuilder b, string table, string keyColumn, string contributions,
        IReadOnlyList<Domain.Field> rollupColumns, IReadOnlyList<Domain.Field> groupByColumns)
    {
        b.AppendLine();
        b.AppendLine("    private async Task ContributeAsync(Event ev, string column, string rowKey, string? groupKey, decimal amount, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        await using var insert = store.Connection.CreateCommand();");
        b.AppendLine($"        insert.CommandText = \"INSERT INTO {contributions} (event_id, column_name, row_key, group_key, amount, seq) \" +");
        b.AppendLine($"            \"SELECT @event, @column, @rowKey, @groupKey, @amount, COALESCE(MAX(seq), 0) + 1 FROM {contributions} WHERE true \" +");
        b.AppendLine("            \"ON CONFLICT (event_id, column_name) DO NOTHING\";");
        b.AppendLine("        insert.AddParam(\"@event\", ev.Id);");
        b.AppendLine("        insert.AddParam(\"@column\", column);");
        b.AppendLine("        insert.AddParam(\"@rowKey\", rowKey);");
        b.AppendLine("        insert.AddParam(\"@groupKey\", groupKey);");
        b.AppendLine("        insert.AddParam(\"@amount\", amount.ToString(CultureInfo.InvariantCulture));");
        b.AppendLine("        await insert.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    private async Task RecomputeAsync(string rowKey, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        var contributions = new List<(string Column, string? Group, decimal Amount)>();");
        b.AppendLine("        await using (var select = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine($"            select.CommandText = \"SELECT column_name, group_key, amount FROM {contributions} WHERE row_key = @rowKey ORDER BY seq\";");
        b.AppendLine("            select.AddParam(\"@rowKey\", rowKey);");
        b.AppendLine("            await using var reader = await select.ExecuteReaderAsync(ct);");
        b.AppendLine("            while (await reader.ReadAsync(ct))");
        b.AppendLine("                contributions.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));");
        b.AppendLine("        }");
        b.AppendLine("        decimal Total(string column) => contributions.Where(c => c.Column == column).Sum(c => c.Amount);");
        b.AppendLine();
        var columns = rollupColumns.Concat(groupByColumns).Select(f => ToSnakeCase(f.Name)).ToList();
        b.AppendLine("        await using var update = store.Connection.CreateCommand();");
        b.AppendLine($"        update.CommandText = \"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"{c} = @{c}"))} WHERE {keyColumn} = @rowKey\";");
        b.AppendLine("        update.AddParam(\"@rowKey\", rowKey);");
        foreach (var field in rollupColumns)
        {
            var column = ToSnakeCase(field.Name);
            var value = field.Derivation switch
            {
                Domain.CountDerivation => $"(long)Total(\"{column}\")",
                _ when field.Type == "decimal" => $"Total(\"{column}\")",
                _ => $"(double)Total(\"{column}\")",
            };
            b.AppendLine($"        update.AddParam(\"@{column}\", {value});");
        }
        foreach (var field in groupByColumns)
        {
            var column = ToSnakeCase(field.Name);
            var groupKeyColumn = ToSnakeCase(((Domain.GroupByDerivation)field.Derivation!).GroupByField);
            b.AppendLine($"        update.AddParam(\"@{column}\", Groups(contributions, \"{column}\", \"{groupKeyColumn}\"));");
        }
        b.AppendLine("        await update.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        if (groupByColumns.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("    private static string Groups(List<(string Column, string? Group, decimal Amount)> contributions, string column, string groupKeyColumn)");
            b.AppendLine("    {");
            b.AppendLine("        var rows = new JsonArray();");
            b.AppendLine("        foreach (var group in contributions.Where(c => c.Column.StartsWith(column + \".\", StringComparison.Ordinal)).GroupBy(c => c.Group))");
            b.AppendLine("        {");
            b.AppendLine("            var entry = new JsonObject { [groupKeyColumn] = group.Key };");
            b.AppendLine("            foreach (var subfield in group.GroupBy(c => c.Column[(column.Length + 1)..]))");
            b.AppendLine("                entry[subfield.Key] = (double)subfield.Sum(c => c.Amount);");
            b.AppendLine("            rows.Add(entry);");
            b.AppendLine("        }");
            b.AppendLine("        return rows.ToJsonString();");
            b.AppendLine("    }");
        }
    }

    /// <summary>Schema 3.16.0 <c>byEvent</c>: the literal the firing event names, into the field's column
    /// (a bool as 1/0, as everywhere); an event it doesn't name leaves the column as it was.</summary>
    private static void EmitByEvent(StringBuilder b, Domain.Field field)
    {
        var byEvent = (Domain.ByEventDerivation)field.Derivation!;
        var column = ToSnakeCase(field.Name);
        var arms = byEvent.Values.Select(v => $"\"{v.Key}\" => (object?){Literal(v.Value)}, ");
        b.AppendLine($"        var {field.Name}ByEvent = ev.Type switch {{ {string.Concat(arms)}_ => null }};");
        b.AppendLine($"        if ({field.Name}ByEvent is not null)");
        b.AppendLine("        {");
        b.AppendLine($"            setClauses.Add(\"{column} = @{field.Name}ByEvent\");");
        b.AppendLine($"            update.AddParam(\"@{field.Name}ByEvent\", {field.Name}ByEvent);");
        b.AppendLine("        }");
    }

    private static string Literal(object value) => value switch
    {
        string text => System.Text.Json.JsonSerializer.Serialize(text),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
        bool flag => flag ? "1L" : "0L",
        _ => throw new InvalidOperationException($"unexpected byEvent literal {value}"),
    };

    /// <summary>Schema 3.16.0 <c>rowsPerListItem</c>: one row per group of each stream's list items.
    /// The stream's own facts (its plain, toggle and byEvent fields, and the list itself, which each
    /// carrying event replaces) are merged into a companion table, <c>&lt;table&gt;_streams</c>, exactly as a
    /// one-row-per-stream projection merges its row. After each event the stream's rows are rebuilt from
    /// that: one per distinct group value among the items (an item without one is in no row), keyed
    /// <c>&lt;stream&gt;|&lt;group&gt;</c>, holding the group's items, any <c>sumOfItems</c>, and a copy of the stream's
    /// facts. A hidden <c>_stream</c> column says whose rows they are. Deterministic and idempotent, so a
    /// rebuild (both tables cleared) replays to the same rows.</summary>
    private static GeneratedFile GenerateRowsPerListItem(Domain.Domain domain, Domain.ReadModel readModel, Domain.ReadModelRowsPerListItem perItem)
    {
        var on = readModel.On;
        var rows = readModel.Collection;
        var streams = rows + "_streams";
        var keyColumn = ToSnakeCase(readModel.Key);
        var groupColumn = ToSnakeCase(perItem.GroupBy);
        var listColumn = ToSnakeCase(perItem.ListField);
        var allColumns = readModel.Fields.Where(f => f.Name != readModel.Key).ToList();
        // The stream's facts: everything but the group (each row's own) and the sums (computed per row).
        var streamColumns = allColumns.Where(f => f.Name != perItem.GroupBy && f.Derivation is not Domain.SumOfItemsDerivation).ToList();
        var plainColumns = streamColumns.Where(f => f.Derivation is null).ToList();
        var toggleColumns = streamColumns.Where(f => f.Derivation is Domain.ToggleDerivation).ToList();
        var byEventColumns = streamColumns.Where(f => f.Derivation is Domain.ByEventDerivation).ToList();
        var sumColumns = allColumns.Where(f => f.Derivation is Domain.SumOfItemsDerivation).ToList();
        var copied = streamColumns.Where(f => f.Name != perItem.ListField).Select(f => ToSnakeCase(f.Name)).ToList();
        var typeName = GenerationSupport.ExportName(rows) + "Projection";

        string SqlType(Domain.Field field) => field.Derivation switch
        {
            Domain.ToggleDerivation t => $"INTEGER DEFAULT {(t.Initial ? 1 : 0)}",
            _ => GenerationSupport.ColumnType(field.Type),
        };

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using System.Text.Json.Nodes;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.Projections;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Projects \"{domain.Aggregate}\" events into the \"{rows}\" table, one row per group of a stream's");
        b.AppendLine($"/// \"{perItem.ListField}\" items by \"{perItem.GroupBy}\" (schema 3.16.0 rowsPerListItem), keyed <c>&lt;stream&gt;|&lt;{perItem.GroupBy}&gt;</c>.");
        b.AppendLine($"/// The stream's own facts are merged into \"{streams}\"; after each event its rows are rebuilt from them.</summary>");
        b.AppendLine($"public sealed class {typeName}(IReadModelStore store) : IProjection");
        b.AppendLine("{");
        b.AppendLine($"    public string Name => \"{rows}\";");
        b.AppendLine($"    public IReadOnlyList<string> Tables => [\"{rows}\", \"{streams}\"];");
        b.AppendLine();
        b.AppendLine("    public async Task InitAsync(CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        await using (var command = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine("            command.CommandText = \"\"\"");
        b.AppendLine($"                CREATE TABLE IF NOT EXISTS {rows} (");
        b.AppendLine($"                    {keyColumn} TEXT PRIMARY KEY,");
        foreach (var field in allColumns)
            b.AppendLine($"                    {ToSnakeCase(field.Name)} {(field.Derivation is Domain.SumOfItemsDerivation ? GenerationSupport.ColumnType(field.Type) : SqlType(field))},");
        b.AppendLine("                    _stream TEXT");
        b.AppendLine("                )");
        b.AppendLine("                \"\"\";");
        b.AppendLine("            await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
        b.AppendLine("        await using (var command = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine("            command.CommandText = \"\"\"");
        b.AppendLine($"                CREATE TABLE IF NOT EXISTS {streams} (");
        b.AppendLine("                    stream_id TEXT PRIMARY KEY" + (streamColumns.Count > 0 ? "," : ""));
        for (var i = 0; i < streamColumns.Count; i++)
            b.AppendLine($"                    {ToSnakeCase(streamColumns[i].Name)} {SqlType(streamColumns[i])}{(i < streamColumns.Count - 1 ? "," : "")}");
        b.AppendLine("                )");
        b.AppendLine("                \"\"\";");
        b.AppendLine("            await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    public async Task ApplyAsync(Event ev, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine($"        if (ev.Type is not ({Disjunction(on)})) return;");
        b.AppendLine();
        b.AppendLine("        // The projection's own writes need a write-guard bypass scope, as every generated projection's do.");
        b.AppendLine("        await using var bypass = await store.BeginBypassAsync(ct);");
        if (readModel.RemovedOn.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("        // Schema 3.9.0 removedByEventIds: every row of the event's own stream goes, with its facts.");
            b.AppendLine($"        if (ev.Type is ({Disjunction(readModel.RemovedOn)}))");
            b.AppendLine("        {");
            b.AppendLine($"            await ExecAsync(\"DELETE FROM {rows} WHERE _stream = @id\", ev.AggregateId, ct);");
            b.AppendLine($"            await ExecAsync(\"DELETE FROM {streams} WHERE stream_id = @id\", ev.AggregateId, ct);");
            b.AppendLine("            return;");
            b.AppendLine("        }");
        }
        b.AppendLine();
        // The stream's facts, merged into its row of the streams table as a one-row-per-stream projection merges.
        var streamTable = new Domain.ReadModel { Collection = streams, Key = "streamId" };
        EmitSeedBlock(b, streamTable, "stream_id", plainColumns, toggleColumns, byEventColumns, "ev.AggregateId");
        b.AppendLine();
        b.AppendLine("        await RegroupAsync(ev.AggregateId, ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    /// <summary>Rebuilds the stream's rows from its facts: one per distinct group among its items.</summary>");
        b.AppendLine("    private async Task RegroupAsync(string stream, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        string? itemsJson;");
        b.AppendLine("        await using (var select = store.Connection.CreateCommand())");
        b.AppendLine("        {");
        b.AppendLine($"            select.CommandText = \"SELECT {listColumn} FROM {streams} WHERE stream_id = @id\";");
        b.AppendLine("            select.AddParam(\"@id\", stream);");
        b.AppendLine("            itemsJson = await select.ExecuteScalarAsync(ct) as string;");
        b.AppendLine("        }");
        b.AppendLine($"        await ExecAsync(\"DELETE FROM {rows} WHERE _stream = @id\", stream, ct);");
        b.AppendLine();
        b.AppendLine("        var items = (string.IsNullOrEmpty(itemsJson) ? null : JsonNode.Parse(itemsJson) as JsonArray) ?? [];");
        b.AppendLine("        // An item without a group (its subfield missing, empty or not text) is in no row.");
        b.AppendLine("        var groups = items.OfType<JsonObject>()");
        b.AppendLine($"            .Select(item => (Item: item, Group: item[\"{perItem.GroupBy}\"] is JsonValue value && value.TryGetValue<string>(out var group) ? group : null))");
        b.AppendLine("            .Where(g => !string.IsNullOrEmpty(g.Group))");
        b.AppendLine("            .GroupBy(g => g.Group!, g => g.Item, StringComparer.Ordinal);");
        b.AppendLine("        foreach (var group in groups)");
        b.AppendLine("        {");
        b.AppendLine("            var groupItems = group.ToList();");
        b.AppendLine("            await using var insert = store.Connection.CreateCommand();");
        var insertColumns = new List<string> { keyColumn, "_stream", groupColumn, listColumn };
        var insertValues = new List<string> { "@key", "@stream", "@group", "@items" };
        insertColumns.AddRange(sumColumns.Select(f => ToSnakeCase(f.Name)));
        insertValues.AddRange(sumColumns.Select(f => "@" + f.Name));
        insertColumns.AddRange(copied);
        insertValues.AddRange(copied);
        b.AppendLine($"            insert.CommandText = \"INSERT INTO {rows} ({string.Join(", ", insertColumns)}) SELECT {string.Join(", ", insertValues)} FROM {streams} WHERE stream_id = @stream\";");
        b.AppendLine("            insert.AddParam(\"@key\", $\"{stream}|{group.Key}\");");
        b.AppendLine("            insert.AddParam(\"@stream\", stream);");
        b.AppendLine("            insert.AddParam(\"@group\", group.Key);");
        b.AppendLine("            insert.AddParam(\"@items\", new JsonArray([.. groupItems.Select(item => item.DeepClone())]).ToJsonString());");
        foreach (var field in sumColumns)
        {
            var sum = (Domain.SumOfItemsDerivation)field.Derivation!;
            var helper = field.Type == "decimal" ? "SumOfItems" : "SumOfItemsAsNumber";
            b.AppendLine($"            insert.AddParam(\"@{field.Name}\", {helper}(groupItems, \"{sum.Subfield}\"));");
        }
        b.AppendLine("            await insert.ExecuteNonQueryAsync(ct);");
        b.AppendLine("        }");
        b.AppendLine("    }");
        if (sumColumns.Any(f => f.Type == "decimal"))
        {
            b.AppendLine();
            b.AppendLine("    /// <summary>Schema 3.16.0 sumOfItems: null unless every item carries a number for the subfield.</summary>");
            b.AppendLine("    private static decimal? SumOfItems(IReadOnlyList<JsonObject> items, string subfield)");
            b.AppendLine("    {");
            b.AppendLine("        var total = 0m;");
            b.AppendLine("        foreach (var item in items)");
            b.AppendLine("        {");
            b.AppendLine("            if (item[subfield] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<decimal>(out var amount)) return null;");
            b.AppendLine("            total += amount;");
            b.AppendLine("        }");
            b.AppendLine("        return total;");
            b.AppendLine("    }");
        }
        if (sumColumns.Any(f => f.Type != "decimal"))
        {
            b.AppendLine();
            b.AppendLine("    /// <summary>Schema 3.16.0 sumOfItems: null unless every item carries a number for the subfield.</summary>");
            b.AppendLine("    private static double? SumOfItemsAsNumber(IReadOnlyList<JsonObject> items, string subfield)");
            b.AppendLine("    {");
            b.AppendLine("        var total = 0d;");
            b.AppendLine("        foreach (var item in items)");
            b.AppendLine("        {");
            b.AppendLine("            if (item[subfield] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<double>(out var amount)) return null;");
            b.AppendLine("            total += amount;");
            b.AppendLine("        }");
            b.AppendLine("        return total;");
            b.AppendLine("    }");
        }
        b.AppendLine();
        b.AppendLine("    private async Task ExecAsync(string sql, string id, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = store.Connection.CreateCommand();");
        b.AppendLine("        command.CommandText = sql;");
        b.AppendLine("        command.AddParam(\"@id\", id);");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine("}");
        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }

    private static string Disjunction(IEnumerable<string> eventTypes) =>
        string.Join(" or ", eventTypes.Select(n => $"\"{n}\""));

    private static string JsonElementAccessor(string domainType, string variable) => domainType switch
    {
        "number" => $"{variable}.GetDouble()",
        "decimal" => $"{variable}.GetDecimal()",
        // 0/1 into the INTEGER column (GenerationSupport.ColumnType): SQLite stored a bound
        // bool as 1/0 anyway, but Postgres rejects a boolean parameter for an integer column.
        "bool" => $"({variable}.GetBoolean() ? 1L : 0L)",
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
