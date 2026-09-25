using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates the search index for a read model's pii <c>contains</c> filters (schema
/// 3.1.0 <c>match</c>): a consumer that keeps, per filter, a table of
/// <c>(row_key, term, subject)</c> in the separate search store
/// (an <c>ISearchIndexStore</c>: <c>search.db</c> on SQLite, the unlogged <c>search</c> schema on
/// Postgres). <c>term</c> is the field's value,
/// revealed and normalized, which is the one place plaintext PII is kept at rest.
///
/// <para>Why that is acceptable, and the rules it follows (findings, "D5 decisions"):
/// the store is excluded from backups and rebuilt from the log; <c>SubjectErased</c>
/// deletes the subject's entries, so "no match" is then the right answer; a value whose
/// key is already destroyed is never indexed; and the index's checkpoint lives in the same
/// store, so losing the file means rebuilding, never a silently partial index.</para>
///
/// <para>A hit yields row keys only (the route's <c>IN (SELECT row_key ...)</c>); displayed
/// values still come through the normal envelope read and its reveal. While the key
/// service is down this consumer's reveal fails, the engine retries, and the index falls
/// behind rather than going wrong.</para>
/// </summary>
internal static class SearchIndexGenerator
{
    public static GeneratedFile? Generate(Domain.Domain domain, Domain.ReadModel readModel)
    {
        var filters = GenerationSupport.IndexedMatchFilters(readModel).ToList();
        if (filters.Count == 0) return null;

        var typeName = GenerationSupport.ExportName(readModel.Collection) + "SearchIndex";
        var tables = filters.Select(f => GenerationSupport.MatchIndexTable(readModel.Collection, f.Field, f.Normalize!)).ToList();

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.Codegen.Generation;");
        b.AppendLine("using DotnetCqrs.Consumers;");
        b.AppendLine("using DotnetCqrs.Crypto;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Search index for \"{readModel.Collection}\"'s pii contains filters: normalized PLAINTEXT");
        b.AppendLine("/// in the separate search store, deleted on erasure, excluded from backups, rebuilt from the log.");
        b.AppendLine("/// Register it with the store as its checkpoint store:");
        b.AppendLine("/// <c>engine.Register(index, searchStore)</c>.</summary>");
        b.AppendLine($"public sealed class {typeName}(ISearchIndexStore index, IKmsClient kms, PiiRevealCache? cache = null) : IConsumer");
        b.AppendLine("{");
        b.AppendLine($"    public string Name => \"{readModel.Collection}:search\";");
        b.AppendLine();
        b.AppendLine("    public async Task InitAsync(CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = index.Connection.CreateCommand();");
        // The store says how to create a table: unlogged on Postgres (ISearchIndexStore.CreateTable).
        b.AppendLine("        command.CommandText = $\"\"\"");
        foreach (var table in tables)
        {
            b.AppendLine($"            {{index.CreateTable}} IF NOT EXISTS {table} (row_key TEXT PRIMARY KEY, term TEXT NOT NULL, subject TEXT NOT NULL);");
            b.AppendLine($"            CREATE INDEX IF NOT EXISTS {table}_subject ON {table} (subject);");
        }
        b.AppendLine("            \"\"\";");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    public async Task ApplyAsync(Event ev, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        if (ev.Type == DataSubject.SubjectErasedEvent && ev.Aggregate == DataSubject.Aggregate)");
        b.AppendLine("        {");
        foreach (var table in tables)
            b.AppendLine($"            await ExecuteAsync(\"DELETE FROM {table} WHERE subject = @subject\", ct, (\"@subject\", ev.AggregateId));");
        // Every table, not just those that lost a row: an earlier value of this subject's can
        // survive on disk as an old row version from an update (ISearchIndexStore.ScrubAsync).
        b.AppendLine($"            await index.ScrubAsync({GenerationSupport.QuotedArray(tables)}, ct);");
        b.AppendLine("            return;");
        b.AppendLine("        }");
        if (readModel.SeedOn.Count == 0)
        {
            b.AppendLine("    }");
        }
        else
        {
            b.AppendLine($"        if (ev.Type is not ({string.Join(" or ", readModel.SeedOn.Select(e => $"\"{e}\""))})) return;");
            b.AppendLine();
            b.AppendLine("        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];");
            foreach (var group in filters.GroupBy(f => f.Field))
            {
                var field = group.Key;
                var valueVar = field + "Value";
                var plainVar = field + "Plain";
                b.AppendLine($"        if (data.TryGetValue(\"{field}\", out var {valueVar}))");
                b.AppendLine("        {");
                // One reveal per field per event, shared by every normalizer indexing it.
                b.AppendLine($"            var {plainVar} = await PiiSearchReveal.RevealAsync({valueVar}, \"{field}\", kms, cache, ct);");
                foreach (var filter in group)
                {
                    var table = GenerationSupport.MatchIndexTable(readModel.Collection, filter.Field, filter.Normalize!);
                    b.AppendLine($"            if ({plainVar} is null)");
                    b.AppendLine($"                await ExecuteAsync(\"DELETE FROM {table} WHERE row_key = @key\", ct, (\"@key\", ev.AggregateId));");
                    b.AppendLine("            else");
                    b.AppendLine("                await ExecuteAsync(");
                    b.AppendLine($"                    \"INSERT INTO {table} (row_key, term, subject) VALUES (@key, @term, @subject) \" +");
                    b.AppendLine("                    \"ON CONFLICT (row_key) DO UPDATE SET term = excluded.term, subject = excluded.subject\", ct,");
                    b.AppendLine($"                    (\"@key\", ev.AggregateId), (\"@term\", MatchNormalizer.Normalize(\"{filter.Normalize}\", {plainVar}.Value.Plaintext)), (\"@subject\", {plainVar}.Value.Subject));");
                }
                b.AppendLine("        }");
            }
            b.AppendLine("    }");
        }
        b.AppendLine();
        b.AppendLine("    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = index.Connection.CreateCommand();");
        b.AppendLine("        command.CommandText = sql;");
        b.AppendLine("        foreach (var (name, value) in parameters) command.AddParam(name, value);");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine("}");

        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }
}
