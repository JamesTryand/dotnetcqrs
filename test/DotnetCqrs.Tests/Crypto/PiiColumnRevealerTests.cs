using System.Text.Json;
using DotnetCqrs.Crypto;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// Milestone D3: the query-time reveal of a page of read-model rows. One buffer and one
/// flush per page, a redacted marker for an erased subject, and a refusal (not a leak)
/// when a pii column holds anything but the envelope.
/// </summary>
public class PiiColumnRevealerTests
{
    private static async Task<string> EnvelopeAsync<T>(IKmsClient kms, string subject, T value) =>
        JsonSerializer.Serialize(await Pii<T>.EncryptAsync(kms, subject, value));

    private static Dictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    [Fact]
    public async Task Pii_cells_are_revealed_to_their_natural_shapes_and_other_columns_are_untouched()
    {
        var kms = new InMemoryKmsClient();
        var rows = new List<Dictionary<string, object?>>
        {
            Row(("order_id", "o1"),
                ("email", await EnvelopeAsync(kms, "c1", "alice@example.com")),
                ("age", await EnvelopeAsync(kms, "c1", 42.0)),
                ("vip", await EnvelopeAsync(kms, "c1", true)),
                ("prefs", await EnvelopeAsync(kms, "c1", JsonDocument.Parse("""{"lang":"en"}""").RootElement))),
        };

        await PiiColumnRevealer.RevealAsync(rows, ["email", "age", "vip", "prefs"], kms);

        Assert.Equal("o1", rows[0]["order_id"]);
        Assert.Equal("alice@example.com", rows[0]["email"]);
        Assert.Equal(42.0, rows[0]["age"]);
        Assert.Equal(true, rows[0]["vip"]);
        Assert.Equal("""{"lang":"en"}""", rows[0]["prefs"]);
    }

    [Fact]
    public async Task A_page_costs_one_decrypt_batch_per_distinct_subject()
    {
        var kms = new InMemoryKmsClient();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var (subject, email) in new[] { ("c1", "a@x"), ("c1", "b@x"), ("c2", "c@x"), ("c1", "d@x") })
            rows.Add(Row(("email", await EnvelopeAsync(kms, subject, email))));

        await PiiColumnRevealer.RevealAsync(rows, ["email"], kms);

        Assert.Equal(2, kms.DecryptBatchCalls);
        Assert.Equal(["a@x", "b@x", "c@x", "d@x"], rows.Select(r => r["email"]));
    }

    [Fact]
    public async Task An_erased_subjects_cell_becomes_the_redacted_marker_and_a_null_cell_stays_null()
    {
        var kms = new InMemoryKmsClient();
        var rows = new List<Dictionary<string, object?>>
        {
            Row(("email", await EnvelopeAsync(kms, "gone", "old@x"))),
            Row(("email", null)),
        };
        await kms.DestroyKeyAsync("gone");

        await PiiColumnRevealer.RevealAsync(rows, ["email"], kms);

        var marker = Assert.IsType<JsonElement>(rows[0]["email"]);
        Assert.True(marker.GetProperty("$redacted").GetBoolean());
        Assert.Null(rows[1]["email"]);
    }

    [Fact]
    public async Task A_cache_hit_makes_no_call()
    {
        var kms = new InMemoryKmsClient();
        var cache = new PiiRevealCache();
        var envelope = await EnvelopeAsync(kms, "c1", "alice@example.com");

        await PiiColumnRevealer.RevealAsync([Row(("email", envelope))], ["email"], kms, cache);
        var second = new List<Dictionary<string, object?>> { Row(("email", envelope)) };
        await PiiColumnRevealer.RevealAsync(second, ["email"], kms, cache);

        Assert.Equal(1, kms.DecryptBatchCalls);
        Assert.Equal("alice@example.com", second[0]["email"]);
    }

    [Theory]
    [InlineData("\"alice@example.com\"")] // plaintext JSON where an envelope belongs
    [InlineData("{\"not\":\"an envelope\"}")]
    [InlineData("not json at all")]
    public async Task A_pii_column_that_is_not_an_envelope_is_refused_rather_than_served(string cell)
    {
        var rows = new List<Dictionary<string, object?>> { Row(("email", cell)) };

        var ex = await Assert.ThrowsAsync<PiiColumnException>(() =>
            PiiColumnRevealer.RevealAsync(rows, ["email"], new InMemoryKmsClient()));

        Assert.Equal("email", ex.Column);
    }
}
