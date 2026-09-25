using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates the keyed-hash search index for a read model's pii <c>exact</c>/<c>prefix</c>
/// filters (schema 3.1.0 <c>match</c>, D6): an <c>IHashedSearchIndex</c> that keeps, per
/// filter, a table of <c>(row_key, key_version, hash, subject)</c> in the separate search store,
/// beside the <c>contains</c> indexes (<see cref="SearchIndexGenerator"/>).
///
/// <para>Pinned rules (platform/eventmodeling-codegen findings, "D6 decisions"): <c>hash</c> is
/// the key-management facade's HMAC of the UTF-8 bytes of the value after
/// <see cref="MatchNormalizer"/>, with nothing added, stored as the whole <c>vault:vN:...</c>
/// string. <c>exact</c> stores one row per record. <c>prefix</c> stores one per
/// <see cref="MatchNormalizer.Prefixes"/> (from <c>minPrefixLength</c> code points to the whole
/// value), so a query hashes its whole term once.</para>
///
/// <para>Hashes survive crypto-shredding: anyone who can call <c>hmac</c> could confirm a
/// guessed value. So <c>SubjectErased</c> deletes the subject's rows, a value whose key is
/// already destroyed is never indexed, and the file stays out of backups.</para>
///
/// <para>One instance hashes with one key version. Rotation (a rebuild at the new version
/// while searches stay pinned to the old one) is <c>HashedSearchIndexRegistration</c>'s job.
/// Each event costs one <c>hmac-batch</c> call covering every normalizer, mode and prefix. The
/// hashes are computed before anything is written, so a failed call leaves the rows as they were
/// and the engine retries the event.</para>
/// </summary>
internal static class HashedIndexGenerator
{
    public static GeneratedFile? Generate(Domain.Domain domain, Domain.ReadModel readModel)
    {
        var filters = GenerationSupport.HashedMatchFilters(readModel).ToList();
        if (filters.Count == 0) return null;

        var typeName = GenerationSupport.ExportName(readModel.Collection) + "HashedIndex";
        string Table(Domain.ReadModelFilter f) => GenerationSupport.HashIndexTable(readModel.Collection, f.Field, f.Normalize!, f.Mode!);
        var tables = filters.Select(Table).ToList();

        var b = new StringBuilder();
        b.AppendLine("using System.Text;");
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.Codegen.Generation;");
        b.AppendLine("using DotnetCqrs.Crypto;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Keyed-hash search index for \"{readModel.Collection}\"'s pii exact/prefix filters, at one");
        b.AppendLine("/// index key version, in the separate search store: deleted on erasure, excluded from backups,");
        b.AppendLine("/// rebuilt from the log. Register it with <c>engine.RegisterHashedSearchIndexAsync</c>, which");
        b.AppendLine("/// handles key rotation.</summary>");
        b.AppendLine($"public sealed class {typeName}(ISearchIndexStore index, IKmsClient kms, string keyName, int keyVersion, PiiRevealCache? cache = null) : IHashedSearchIndex");
        b.AppendLine("{");
        b.AppendLine($"    private static readonly string[] Tables = {GenerationSupport.QuotedArray(tables)};");
        b.AppendLine();
        b.AppendLine($"    public string IndexName => \"{GenerationSupport.HashedIndexName(readModel.Collection)}\";");
        b.AppendLine("    public int KeyVersion => keyVersion;");
        b.AppendLine("    public string Name => $\"{IndexName}:v{keyVersion}\";");
        b.AppendLine();
        b.AppendLine("    public async Task InitAsync(CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        await using var command = index.Connection.CreateCommand();");
        // The store says how to create a table: unlogged on Postgres (ISearchIndexStore.CreateTable).
        b.AppendLine("        command.CommandText = $\"\"\"");
        foreach (var table in tables)
        {
            b.AppendLine($"            {{index.CreateTable}} IF NOT EXISTS {table} (row_key TEXT NOT NULL, key_version INTEGER NOT NULL, hash TEXT NOT NULL, subject TEXT NOT NULL);");
            b.AppendLine($"            CREATE INDEX IF NOT EXISTS {table}_hash ON {table} (hash);");
            b.AppendLine($"            CREATE INDEX IF NOT EXISTS {table}_row ON {table} (row_key, key_version);");
            b.AppendLine($"            CREATE INDEX IF NOT EXISTS {table}_subject ON {table} (subject);");
        }
        b.AppendLine("            \"\"\";");
        b.AppendLine("        await command.ExecuteNonQueryAsync(ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    public async Task DeleteVersionsExceptAsync(IReadOnlyCollection<int> keep, CancellationToken ct = default)");
        b.AppendLine("    {");
        b.AppendLine("        var where = keep.Count == 0 ? \"\" : $\" WHERE key_version NOT IN ({string.Join(\", \", keep)})\";");
        b.AppendLine("        foreach (var table in Tables) await ExecuteAsync($\"DELETE FROM {table}{where}\", ct);");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    public async Task ApplyAsync(Event ev, CancellationToken ct)");
        b.AppendLine("    {");
        b.AppendLine("        if (ev.Type == DataSubject.SubjectErasedEvent && ev.Aggregate == DataSubject.Aggregate)");
        b.AppendLine("        {");
        b.AppendLine("            // Every version: a hash left behind would confirm a guessed value.");
        b.AppendLine("            foreach (var table in Tables)");
        b.AppendLine("                await ExecuteAsync($\"DELETE FROM {table} WHERE subject = @subject\", ct, (\"@subject\", ev.AggregateId));");
        b.AppendLine("            await index.ScrubAsync(Tables, ct);");
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
            b.AppendLine("        // Tables whose rows for this record are replaced, and the (table, term, subject) rows to write.");
            b.AppendLine("        var replaced = new List<string>();");
            b.AppendLine("        var rows = new List<(string Table, string Term, string Subject)>();");
            foreach (var fieldGroup in filters.GroupBy(f => f.Field))
            {
                var field = fieldGroup.Key;
                var valueVar = field + "Value";
                var plainVar = field + "Plain";
                b.AppendLine($"        if (data.TryGetValue(\"{field}\", out var {valueVar}))");
                b.AppendLine("        {");
                // One reveal per field per event, shared by every normalizer and mode.
                b.AppendLine($"            var {plainVar} = await PiiSearchReveal.RevealAsync({valueVar}, \"{field}\", kms, cache, ct);");
                foreach (var filter in fieldGroup)
                    b.AppendLine($"            replaced.Add(\"{Table(filter)}\");");
                b.AppendLine($"            if ({plainVar} is {{ }} {field}Known)");
                b.AppendLine("            {");
                foreach (var normGroup in fieldGroup.GroupBy(f => f.Normalize!))
                {
                    var termVar = field + "_" + normGroup.Key;
                    b.AppendLine($"                var {termVar} = MatchNormalizer.Normalize(\"{normGroup.Key}\", {field}Known.Plaintext);");
                    foreach (var filter in normGroup)
                    {
                        if (filter.Mode == "exact")
                            b.AppendLine($"                if ({termVar}.Length > 0) rows.Add((\"{Table(filter)}\", {termVar}, {field}Known.Subject));");
                        else
                            b.AppendLine($"                foreach (var prefix in MatchNormalizer.Prefixes({termVar}, {filter.MinPrefixLength!.Value})) rows.Add((\"{Table(filter)}\", prefix, {field}Known.Subject));");
                    }
                }
                b.AppendLine("            }");
                b.AppendLine("        }");
            }
            b.AppendLine();
            b.AppendLine("        var hashes = await HashAsync(rows, ct);");
            b.AppendLine("        foreach (var table in replaced)");
            b.AppendLine("            await ExecuteAsync($\"DELETE FROM {table} WHERE row_key = @key AND key_version = @version\", ct,");
            b.AppendLine("                (\"@key\", ev.AggregateId), (\"@version\", keyVersion));");
            b.AppendLine("        for (var i = 0; i < rows.Count; i++)");
            b.AppendLine("            await ExecuteAsync($\"INSERT INTO {rows[i].Table} (row_key, key_version, hash, subject) VALUES (@key, @version, @hash, @subject)\", ct,");
            b.AppendLine("                (\"@key\", ev.AggregateId), (\"@version\", keyVersion), (\"@hash\", hashes[i]), (\"@subject\", rows[i].Subject));");
            b.AppendLine("    }");
            b.AppendLine();
            b.AppendLine("    /// <summary>One hmac-batch call per 1000 terms, pinned to this instance's key version.</summary>");
            b.AppendLine("    private async Task<List<string>> HashAsync(List<(string Table, string Term, string Subject)> rows, CancellationToken ct)");
            b.AppendLine("    {");
            b.AppendLine("        var hashes = new List<string>(rows.Count);");
            b.AppendLine("        foreach (var chunk in rows.Chunk(KmsClient.MaxBatchItems))");
            b.AppendLine("        {");
            b.AppendLine("            var results = await kms.HmacBatchAsync(keyName, [.. chunk.Select(r => Encoding.UTF8.GetBytes(r.Term))], keyVersion, ct);");
            b.AppendLine("            if (results.Count != chunk.Length)");
            b.AppendLine("                throw new KmsProtocolException($\"hmac-batch returned {results.Count} results for {chunk.Length} inputs\");");
            b.AppendLine("            foreach (var r in results)");
            b.AppendLine("                hashes.Add(r.Hmac ?? throw new KmsProtocolException($\"hmac-batch item failed: {r.Error}\"));");
            b.AppendLine("        }");
            b.AppendLine("        return hashes;");
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
