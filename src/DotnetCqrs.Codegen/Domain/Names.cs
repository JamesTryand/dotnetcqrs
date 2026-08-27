using System.Text;
using System.Text.RegularExpressions;

namespace DotnetCqrs.Codegen.Domain;

/// <summary>
/// Identifier derivation: ports pocketcqrs's <c>emschema</c> naming helpers
/// (<c>names.go</c>). Three identifier spaces meet here — schema id
/// (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, e.g. <c>"order-placed"</c>), schema name (free
/// prose, e.g. <c>"Order Placed"</c>), and a generated-code identifier (e.g.
/// <c>"OrderPlaced"</c>) — and mapping always folds the first two into the third.
/// </summary>
public static partial class Names
{
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    public static bool IsValidIdentifier(string s) => IdentifierPattern().IsMatch(s);

    /// <summary>Derives a code identifier from a schema element's name, falling back to
    /// its id when the name is unusable. <c>"Order Placed"</c> → <c>"OrderPlaced"</c>.
    /// Non-identifier characters are dropped rather than mangled.</summary>
    public static string TypeName(string? name, string id)
    {
        var fromName = Pascal(name ?? "");
        return fromName.Length > 0 ? fromName : Pascal(id.Replace("-", " "));
    }

    /// <summary>Turns a code identifier back into a schema-id-shaped string. Splits on
    /// lower→upper boundaries, keeping acronym runs intact: <c>"OrderPDFGenerated"</c> →
    /// <c>"order-pdf-generated"</c>, not <c>"order-p-d-f-generated"</c>.</summary>
    public static string DeriveId(string typeName) =>
        string.Join("-", SplitWords(typeName).Select(w => w.ToLowerInvariant()));

    /// <summary>Renders a code identifier as this project's lower-camel aggregate
    /// convention: <c>"Order"</c> → <c>"order"</c>, <c>"SupportTicket"</c> →
    /// <c>"supportTicket"</c>.</summary>
    public static string LowerFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Folds a free-form string (e.g. a document's own aggregate tag) into an
    /// identifier: letters/digits/underscores pass through (case-normalized at word
    /// boundaries); any other character joins the next word rather than vanishing, so
    /// <c>"order-placed"</c> becomes <c>"orderPlaced"</c>, not <c>"orderplaced"</c>. The
    /// result is checked by the caller regardless, so a string with nothing usable in it
    /// still fails loudly.</summary>
    public static string SanitizeName(string s)
    {
        var b = new StringBuilder();
        var upperNext = false;
        foreach (var r in s)
        {
            if (char.IsLetter(r))
            {
                b.Append(upperNext && b.Length > 0 ? char.ToUpperInvariant(r) : r);
                upperNext = false;
            }
            else if (char.IsDigit(r) || r == '_')
            {
                if (b.Length == 0) continue; // an identifier cannot start with a digit
                b.Append(r);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }
        return b.ToString();
    }

    private static string Pascal(string s)
    {
        var b = new StringBuilder();
        var upperNext = true;
        foreach (var r in s)
        {
            if (char.IsLetter(r))
            {
                b.Append(upperNext ? char.ToUpperInvariant(r) : r);
                upperNext = false;
            }
            else if (char.IsDigit(r))
            {
                if (b.Length == 0) continue; // a leading digit cannot start an identifier
                b.Append(r);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }
        return b.ToString();
    }

    private static List<string> SplitWords(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var r = s[i];
            if (i == 0)
            {
                current.Append(r);
            }
            else if (char.IsUpper(r) && !char.IsUpper(s[i - 1]))
            {
                // lower -> upper: a new word starts here
                Flush();
                current.Append(r);
            }
            else if (char.IsUpper(r) && char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]))
            {
                // the last capital of a run belongs to the NEXT word:
                // "PDFGenerated" -> "PDF" + "Generated"
                Flush();
                current.Append(r);
            }
            else
            {
                current.Append(r);
            }
        }
        Flush();
        return words;
    }
}
