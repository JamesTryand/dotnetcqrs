using DotnetCqrs.Codegen.Domain;

namespace DotnetCqrs.Codegen.Generation;

internal static class GenerationSupport
{
    /// <summary>Renders a domain-model name (already a validated identifier per
    /// <see cref="DomainValidation.Validate"/>) as an exported C# identifier: just the
    /// first rune upper-cased, since punctuation was already stripped when the name
    /// was derived.</summary>
    public static string ExportName(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>Maps a domain field type onto its C# type — the second half of the
    /// ten-to-five-to-one fold (schema type → domain type → C# type), ported from
    /// pocketcqrs's own <c>goType</c> (its JS→Go conversion table's Go half).</summary>
    public static string CSharpType(string domainType) => domainType switch
    {
        "text" => "string",
        "number" => "double",
        "bool" => "bool",
        "date" => "DateTime",
        "json" => "JsonElement",
        // unreachable: Validate rejects any type not in the five before generation runs
        _ => "string",
    };

    /// <summary>Maps a domain field type onto its SQLite column type, for a generated
    /// projection's <c>CREATE TABLE</c>.</summary>
    public static string SqliteType(string domainType) => domainType switch
    {
        "number" => "REAL",
        "bool" => "INTEGER",
        _ => "TEXT", // text, date (ISO 8601 string), json (raw text)
    };

    /// <summary>Unions every field every event this domain's commands declare, in
    /// first-declared order, resolving each name to one type — ports pocketcqrs's
    /// <c>collectEventFields</c>. A field name declared with more than one type across
    /// different events is a real model ambiguity: the first type seen wins, and the
    /// conflict is named via the returned warning rather than left to surface as a
    /// C# compile error (every event's own data record still uses that event's own
    /// locally-declared type, so the conflict is invisible to the compiler; only
    /// <see cref="Domain.Domain"/>'s unioned State record would be affected, and this
    /// generator does not attempt to unify per-event record shapes into it).</summary>
    public static (List<(string Name, string Type, bool Pii)> Fields, List<string> Warnings) CollectEventFields(Domain.Domain domain)
    {
        var order = new List<string>();
        var types = new Dictionary<string, string>();
        var pii = new Dictionary<string, bool>();
        var firstEvent = new Dictionary<string, string>();
        var warnings = new List<string>();

        foreach (var command in domain.Commands)
        {
            foreach (var @event in command.Events)
            {
                foreach (var field in @event.Fields)
                {
                    if (!types.TryGetValue(field.Name, out var existing))
                    {
                        types[field.Name] = field.Type;
                        pii[field.Name] = field.Pii;
                        firstEvent[field.Name] = @event.Name;
                        order.Add(field.Name);
                        continue;
                    }
                    if (existing != field.Type)
                        warnings.Add($"field \"{field.Name}\" is declared as \"{existing}\" by event \"{firstEvent[field.Name]}\" " +
                            $"but \"{field.Type}\" by event \"{@event.Name}\"; the C# generator uses \"{existing}\" everywhere " +
                            "and keeps the first type it saw");
                    // Once PII, always PII in state: a field one event marks pii cannot be
                    // held in plaintext because another event forgot to.
                    if (field.Pii) pii[field.Name] = true;
                }
            }
        }

        var fields = order.Select(name => (Name: name, Type: types[name], Pii: pii[name])).ToList();
        return (fields, warnings);
    }

    /// <summary>The C# type a generated payload/state property gets: the folded CLR type,
    /// wrapped in <c>Pii&lt;T&gt;</c> (<c>DotnetCqrs.Crypto</c>) when the field is
    /// <c>field.pii</c>. The wrapper's JSON converter reads a command's bare value as
    /// fresh plaintext and a stored envelope as pending ciphertext, so the generated
    /// <c>Decide</c>/<c>Evolve</c> bodies need no PII-specific code at all.</summary>
    public static string FieldCSharpType(string domainType, bool pii) =>
        pii ? $"Pii<{CSharpType(domainType)}>" : CSharpType(domainType);

    /// <summary>Renders names as a C# string-literal array initializer, e.g.
    /// <c>["a", "b"]</c>.</summary>
    public static string QuotedArray(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Select(n => $"\"{n}\"")) + "]";

    /// <summary>The shadow column a non-pii <c>match</c> filter searches: the field's value
    /// after <see cref="MatchNormalizer"/>, kept by the projection beside the field itself,
    /// e.g. <c>customer_email__match_case_fold</c>. One per (field, normalizer), so two
    /// filters that share both share the column.</summary>
    public static string MatchColumn(string fieldName, string normalize) =>
        $"{SnakeCase(fieldName)}__match_{SnakeCase(normalize)}";

    /// <summary>The search-index table for a pii <c>contains</c> filter, in the separate
    /// search store (see <c>SqliteSearchIndexStore</c>).</summary>
    public static string MatchIndexTable(string collection, string fieldName, string normalize) =>
        $"{collection}__match_{SnakeCase(fieldName)}_{SnakeCase(normalize)}";

    /// <summary>The keyed-hash index table for a pii <c>exact</c>/<c>prefix</c> filter (D6), in
    /// the separate search store. One per (field, normalizer, mode); rows of every key version
    /// share it, tagged with <c>key_version</c>.</summary>
    public static string HashIndexTable(string collection, string fieldName, string normalize, string mode) =>
        $"{collection}__hash_{SnakeCase(fieldName)}_{SnakeCase(normalize)}_{mode}";

    /// <summary>The hashed index's name, shared by all key versions (<c>IHashedSearchIndex.IndexName</c>).</summary>
    public static string HashedIndexName(string collection) => $"{collection}:hashed";

    /// <summary>Whether a <c>match</c> filter searches a keyed-hash index: a pii field in
    /// mode <c>exact</c> or <c>prefix</c> (D6). Its parameter is the facade's HMAC of the
    /// normalized term, not a pattern.</summary>
    public static bool IsHashedMatch(Domain.ReadModel readModel, Domain.ReadModelFilter filter) =>
        filter.IsMatch && filter.Mode != "contains" && readModel.Fields.Any(f => f.Name == filter.Field && f.Pii);

    /// <summary>The WHERE clause for one <c>match</c> filter, with <paramref name="paramToken"/>
    /// where the parameter goes. A non-pii field compares its shadow column. A pii
    /// <c>contains</c> selects row keys from its plaintext index, and a pii <c>exact</c>/<c>prefix</c>
    /// from its keyed-hash index, both in the attached <c>search</c> store. The parameter is
    /// <c>MatchNormalizer.Pattern</c>'s output, except for a hashed filter, where it is the HMAC
    /// of the normalized term (<see cref="IsHashedMatch"/>). Used by both the generated route and
    /// the scenario verifier, so the two build the same SQL.</summary>
    public static string MatchClause(Domain.ReadModel readModel, Domain.ReadModelFilter filter, string paramToken)
    {
        if (IsHashedMatch(readModel, filter))
            return $"{SnakeCase(readModel.Key)} IN (SELECT row_key FROM search.{HashIndexTable(readModel.Collection, filter.Field, filter.Normalize!, filter.Mode!)} WHERE hash = {paramToken})";
        var comparison = filter.Mode == "exact" ? $"= {paramToken}" : $"LIKE {paramToken} ESCAPE '\\'";
        var pii = readModel.Fields.Any(f => f.Name == filter.Field && f.Pii);
        return pii
            ? $"{SnakeCase(readModel.Key)} IN (SELECT row_key FROM search.{MatchIndexTable(readModel.Collection, filter.Field, filter.Normalize!)} WHERE term {comparison})"
            : $"{MatchColumn(filter.Field, filter.Normalize!)} {comparison}";
    }

    /// <summary>Non-pii <c>match</c> filters, one per shadow column.</summary>
    public static IEnumerable<Domain.ReadModelFilter> ShadowMatchFilters(Domain.ReadModel readModel)
    {
        var pii = readModel.Fields.Where(f => f.Pii).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return readModel.Filters.Where(f => f.IsMatch && !pii.Contains(f.Field))
            .DistinctBy(f => MatchColumn(f.Field, f.Normalize!));
    }

    /// <summary>pii <c>contains</c> filters, one per index (field, normalizer).</summary>
    public static IEnumerable<Domain.ReadModelFilter> IndexedMatchFilters(Domain.ReadModel readModel)
    {
        var pii = readModel.Fields.Where(f => f.Pii).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return readModel.Filters.Where(f => f.IsMatch && f.Mode == "contains" && pii.Contains(f.Field)).DistinctBy(f => (f.Field, f.Normalize));
    }

    /// <summary>pii <c>exact</c>/<c>prefix</c> filters, one per hashed table (field, normalizer,
    /// mode). Two prefix filters on one table must agree on <c>minPrefixLength</c>, which
    /// <c>DocumentMapper</c> checks.</summary>
    public static IEnumerable<Domain.ReadModelFilter> HashedMatchFilters(Domain.ReadModel readModel) =>
        readModel.Filters.Where(f => IsHashedMatch(readModel, f)).DistinctBy(f => (f.Field, f.Normalize, f.Mode));

    private static string SnakeCase(string name)
    {
        var b = new System.Text.StringBuilder();
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
