using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DotnetCqrs.Host;

/// <summary>
/// The terms of one read-model query, from wherever the request carries them: a GET's query string, or a
/// POST's JSON body. A generated query route (<c>ReadModelQueryGenerator</c>) answers both at the same path
/// through the same code, so the POST gives exactly the GET's answer, with the same access rules and the same
/// refusals. The POST exists so a search by a personal field (an email address) never has to be in a URL,
/// where access logs, proxies and browser history keep it (decided 2026-11-21, for parity with pocketcqrs).
///
/// <para><b>The body</b> is a JSON object whose keys and values are exactly the GET's query parameters:
/// <c>{"emailExact":"alice@example.com"}</c> is <c>?emailExact=alice%40example.com</c>. A value is a string,
/// or a number (taken as its JSON text, so <c>{"estimatedEffort":2}</c> is <c>?estimatedEffort=2</c>). A
/// filter that takes a JSON-encoded value in the query string (dateRange) takes the same encoded string here.
/// An empty body is no terms, as on the command route.</para>
///
/// <para><b>Refused with 400</b> (a problem body): a body that is not one JSON object (an array, a scalar,
/// malformed or trailing text), a value that is not a string or a number (null, a boolean, an array, an
/// object), a key given twice, and a POST that also carries a query string (its terms go in the body only).
/// A body over <see cref="MaxBodyBytes"/> is a 413. No refusal repeats a term's value.</para>
/// </summary>
public static class QueryTerms
{
    /// <summary>The largest POST body a query takes: query terms are short.</summary>
    public const int MaxBodyBytes = 1 << 20;

    /// <summary>The terms read from a request, or the refusal to answer it instead.</summary>
    public readonly record struct Read(IReadOnlyList<KeyValuePair<string, string>> Terms, IResult? Refusal)
    {
        public static Read Refused(IResult refusal) => new([], refusal);
    }

    /// <summary>A query string's terms, one per key (a repeated key's values joined, as <c>StringValues</c>
    /// does).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FromQuery(IQueryCollection query) =>
        query.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.ToString())).ToList();

    /// <summary>The request's terms: its query string for a GET, its JSON body for a POST.</summary>
    public static async Task<Read> FromRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!HttpMethods.IsPost(request.Method))
            return new(FromQuery(request.Query), null);
        if (request.QueryString.HasValue)
            return Refuse("a POST query takes its terms in the JSON body, not in the query string");

        var body = await ReadCappedAsync(request.Body, ct);
        if (body is null)
            return Read.Refused(Results.Problem($"the query body is over {MaxBodyBytes} bytes",
                statusCode: StatusCodes.Status413PayloadTooLarge));
        if (string.IsNullOrWhiteSpace(body))
            return new([], null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Refuse("the query body must be one JSON object of query terms");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Refuse("the query body must be one JSON object of query terms");
            var terms = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    return Refuse($"'{property.Name}' is given more than once");
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };
                if (value is null)
                    return Refuse($"'{property.Name}' must be a string or a number");
                terms.Add(KeyValuePair.Create(property.Name, value));
            }
            return new(terms, null);
        }
    }

    private static Read Refuse(string detail) =>
        Read.Refused(Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest));

    /// <summary>The body as UTF-8 text, or null when it is over the cap.</summary>
    private static async Task<string?> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
