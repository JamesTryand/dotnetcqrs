using System.Globalization;
using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// The <c>normalize</c> rules of a schema 3.1.0 <c>match</c> filter. The stored value
/// and the query term both pass through here, and a match only works if they normalize
/// identically. Shared, not duplicated, between the generated projections (which fill the
/// shadow column or the search index), the generated query routes (which normalize the
/// term) and the scenario-verify harness, the same way <see cref="DateRangeResolver"/> is.
///
/// <para><b>Pinned rules</b> (user, 2026-09-23; <c>platform/eventmodeling-codegen</c>
/// findings, "D5 decisions"). The schema's design notes define these loosely, and a 3.1.1
/// clarification is filed with <c>platform/eventmodeling-schema</c>:</para>
/// <list type="bullet">
/// <item><c>none</c>: unchanged.</item>
/// <item><c>caseFold</c> (the default) and <c>email</c>: Unicode NFKC, then simple
/// invariant lowercase, then trim.</item>
/// <item><c>personName</c>: <c>caseFold</c>, then NFD with combining marks removed, then
/// runs of whitespace collapsed to one space, then recomposed (NFC) so scripts that
/// decompose without marks, such as Hangul, come back in their usual form.</item>
/// <item><c>phone</c>: a leading <c>+</c> kept, every other non-digit dropped. No country
/// is inferred, so a local and an international form of one number do not match.</item>
/// </list>
///
/// <para><b>Watch this before hashing (D6) or porting (pocketcqrs).</b> "Lowercase" here
/// is .NET's simple, per-character mapping, not full Unicode case folding: Go's
/// <c>cases.Fold</c> maps <c>ß</c> to <c>ss</c>, and this does not. A hashed index only
/// matches if every implementation produces the same bytes, so another implementation must
/// follow these rules exactly, not its platform's nearest equivalent. Changing a rule
/// invalidates every index built with the old one.</para>
/// </summary>
public static class MatchNormalizer
{
    public static string Normalize(string normalize, string value) => normalize switch
    {
        "none" => value,
        "caseFold" or "email" => CaseFold(value),
        "personName" => PersonName(value),
        "phone" => Phone(value),
        _ => throw new ArgumentOutOfRangeException(nameof(normalize), normalize, "unknown match normalizer"),
    };

    /// <summary>Escapes a normalized term for a SQL <c>LIKE ... ESCAPE '\'</c> pattern, so
    /// <c>%</c>, <c>_</c> and <c>\</c> in a search term match themselves rather than
    /// acting as wildcards.</summary>
    public static string EscapeLike(string term) =>
        term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>The SQL parameter value for a normalized <paramref name="term"/> under
    /// <paramref name="mode"/>: the term itself for <c>exact</c>, an escaped <c>LIKE</c>
    /// pattern for <c>prefix</c> and <c>contains</c>. Pairs with the clause from
    /// <c>GenerationSupport.MatchClause</c>.</summary>
    public static string Pattern(string mode, string term) => mode switch
    {
        "exact" => term,
        "prefix" => EscapeLike(term) + "%",
        "contains" => "%" + EscapeLike(term) + "%",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown match mode"),
    };

    private static string CaseFold(string value) =>
        value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim();

    private static string PersonName(string value)
    {
        var decomposed = CaseFold(value).Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c)) { pendingSpace = b.Length > 0; continue; }
            if (pendingSpace) { b.Append(' '); pendingSpace = false; }
            b.Append(c);
        }
        return b.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Phone(string value)
    {
        var trimmed = value.Trim();
        var b = new StringBuilder(trimmed.Length);
        if (trimmed.StartsWith('+')) b.Append('+');
        foreach (var c in trimmed)
            if (c is >= '0' and <= '9') b.Append(c);
        return b.ToString();
    }
}
