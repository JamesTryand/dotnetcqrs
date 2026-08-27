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
    public static (List<(string Name, string Type)> Fields, List<string> Warnings) CollectEventFields(Domain.Domain domain)
    {
        var order = new List<string>();
        var types = new Dictionary<string, string>();
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
                        firstEvent[field.Name] = @event.Name;
                        order.Add(field.Name);
                        continue;
                    }
                    if (existing != field.Type)
                        warnings.Add($"field \"{field.Name}\" is declared as \"{existing}\" by event \"{firstEvent[field.Name]}\" " +
                            $"but \"{field.Type}\" by event \"{@event.Name}\"; the C# generator uses \"{existing}\" everywhere " +
                            "and keeps the first type it saw");
                }
            }
        }

        var fields = order.Select(name => (Name: name, Type: types[name])).ToList();
        return (fields, warnings);
    }

    /// <summary>Renders names as a C# string-literal array initializer, e.g.
    /// <c>["a", "b"]</c>.</summary>
    public static string QuotedArray(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Select(n => $"\"{n}\"")) + "]";
}
