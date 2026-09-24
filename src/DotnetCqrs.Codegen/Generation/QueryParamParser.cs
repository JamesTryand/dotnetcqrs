using System.Globalization;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Converts a plain query-string value to the CLR type its column holds, before a generated
/// query route binds it. SQLite compares a text parameter with a numeric column by converting
/// it, but Postgres has no implicit <c>integer = text</c> operator and fails the query, so the
/// route must bind a number as a number. The kinds come from
/// <see cref="GenerationSupport.QueryParamKind"/>; a text column takes the raw string and never
/// reaches here. Called by generated code, so it is public.
/// </summary>
public static class QueryParamParser
{
    /// <summary>Parses <paramref name="raw"/> as <paramref name="kind"/>: <c>number</c> (a
    /// double, invariant culture), <c>integer</c> (a long) or <c>bool</c> (<c>true</c>/<c>false</c>
    /// or <c>1</c>/<c>0</c>, bound as the 1/0 the column stores). Returns false for a value that
    /// isn't one, which the route answers with a 400.</summary>
    public static bool TryParse(string kind, string raw, out object? value)
    {
        value = null;
        var text = raw.Trim();
        switch (kind)
        {
            case "number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number):
                value = number;
                return true;
            case "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                value = integer;
                return true;
            case "bool" when text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase):
                value = 1L;
                return true;
            case "bool" when text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase):
                value = 0L;
                return true;
            default:
                return false;
        }
    }
}
