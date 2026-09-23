using System.Text.Json;

namespace DotnetCqrs.Crypto;

/// <summary>The query-time half of the read path (D3): a read model's pii columns hold
/// the <c>{"$pii":…}</c> envelope at rest (P4), and this reveals them for one page of
/// rows just before the rows leave the process. Every pending cell on the page goes
/// through ONE <see cref="PiiRevealBuffer"/> and ONE flush, so a page costs at most one
/// <c>decrypt-batch</c> per distinct subject, and none at all for cells the
/// <see cref="PiiRevealCache"/> already holds.
///
/// <para>Shared by the generated query routes and the scenario-verify harness, so a
/// stateView scenario sees exactly what a real caller would.</para>
///
/// <para>A revealed cell becomes the value's natural CLR shape: a string, a double or a
/// bool; a date stays its ISO string; a json value becomes its raw JSON text, the same
/// convention every non-pii json column already follows. An erased subject's cell
/// becomes <see cref="RedactedMarker"/> (<c>{"$redacted":true}</c>), which callers can
/// tell apart from a null, i.e. "never set".</para>
///
/// <para><b>Fails closed:</b> a non-null pii cell that is not an envelope means
/// plaintext reached a column that should only ever hold ciphertext. That is a bug
/// upstream, so the page is refused rather than served.</para></summary>
public static class PiiColumnRevealer
{
    /// <summary>What an erased value looks like on the wire.</summary>
    public static JsonElement RedactedMarker { get; } = JsonDocument.Parse("""{"$redacted":true}""").RootElement.Clone();

    public static async Task RevealAsync(
        IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyCollection<string> piiColumns,
        IKmsClient kms, PiiRevealCache? cache = null, CancellationToken ct = default)
    {
        if (rows.Count == 0 || piiColumns.Count == 0) return;

        var buffer = new PiiRevealBuffer(kms, cache);
        var pending = new List<(Dictionary<string, object?> Row, string Column, Task<Pii<JsonElement>> Reveal)>();
        foreach (var row in rows)
        {
            foreach (var column in piiColumns)
            {
                if (!row.TryGetValue(column, out var cell) || cell is null) continue;
                if (cell is not string json)
                    throw new PiiColumnException(column, $"holds a {cell.GetType().Name}, not the ciphertext envelope");

                Pii<JsonElement>? pii;
                try { pii = JsonSerializer.Deserialize<Pii<JsonElement>>(json); }
                catch (JsonException ex) { throw new PiiColumnException(column, "holds malformed JSON", ex); }
                if (pii is null) { row[column] = null; continue; }
                if (pii.State == PiiState.Fresh)
                    throw new PiiColumnException(column, "holds a plaintext value, not the ciphertext envelope");

                pending.Add((row, column, pii.RevealAsync(buffer, ct)));
            }
        }

        await buffer.FlushAsync(ct).ConfigureAwait(false);
        foreach (var (row, column, reveal) in pending)
        {
            var revealed = await reveal.ConfigureAwait(false);
            row[column] = revealed.State == PiiState.Redacted ? RedactedMarker : Natural(revealed.Value);
        }
    }

    private static object? Natural(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}

/// <summary>A pii column held something other than the ciphertext envelope. Fail-closed:
/// the query is refused instead of returning what might be plaintext.</summary>
public sealed class PiiColumnException(string column, string problem, Exception? inner = null)
    : Exception($"pii column '{column}' {problem}; refusing to serve it.", inner)
{
    public string Column { get; } = column;
}
