using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.14.0 <c>readModel.rowKeyField</c>: rows keyed by a field the events carry, in place of
/// their stream, for one stream that holds many rows' events. For example,
/// a grant round is one stream, and each of its awards is a row. The generated projection
/// is run directly, with every event on the same stream: the scenario harness keys a given event by
/// the read model's own id field, so a view scenario can't tell a stream key from a row key.
/// </summary>
[Trait("Category", "Compiles")]
public class RowKeyFieldTests
{
    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "repeated-outcomes.json"));

    /// <summary>The example with a revoke event that removes an award's row.</summary>
    private static JsonNode WithVoid()
    {
        var node = JsonNode.Parse(Example)!;
        node["events"]!["award-revoked"] = JsonNode.Parse("""
            {"name": "Award Revoked", "swimlaneId": "grants", "aggregate": "grant_round",
             "fields": [{"name": "grantRoundId", "type": "string", "idAttribute": true}, {"name": "awardId", "type": "string"}]}
            """);
        node["commands"]!["revoke-award"] = JsonNode.Parse("""
            {"name": "Revoke Award", "aggregate": "grant_round", "fields": [{"name": "awardId", "type": "string"}]}
            """);
        node["slices"]!.AsArray().Add(JsonNode.Parse("""
            {"id": "revoke-award-slice", "name": "Revoke Award", "pattern": "stateChange", "swimlaneId": "grants",
             "status": "created", "screenId": "grants-screen", "commandId": "revoke-award", "eventIds": ["award-revoked"], "scenarios": []}
            """));
        var awards = node["readModels"]!["awards"]!;
        awards["builtFromEventIds"]!.AsArray().Add("award-revoked");
        awards["removedByEventIds"] = new JsonArray("award-revoked");
        return node;
    }

    private const string ProgramCs = """"
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Grant_round;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new AwardsProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string type, string data) => new(++position, $"e{position}", "grant_round", "g1", position, type, data, "{}", "2026-10-01T00:00:00Z");
        await projection.ApplyAsync(On("GrantAwarded", """{"grantRoundId":"g1","recipientId":"r1","awardId":"g1:r1","closingDate":"2026-09-30","amount":{"$pii":{"s":"r1","c":"x"}}}"""), default);
        await projection.ApplyAsync(On("GrantAwarded", """{"grantRoundId":"g1","recipientId":"r2","awardId":"g1:r2","closingDate":"2026-09-30","amount":{"$pii":{"s":"r2","c":"y"}}}"""), default);
        if (args.Length > 0 && args[0] == "void")
            await projection.ApplyAsync(On("AwardRevoked", """{"grantRoundId":"g1","awardId":"g1:r1"}"""), default);

        await using var select = readModel.Connection.CreateCommand();
        select.CommandText = "SELECT award_id, recipient_id, amount FROM awards ORDER BY award_id";
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Console.WriteLine($"row {reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
        return 0;
        """";

    private static async Task<(bool Success, string Output)> RunAsync(JsonNode doc, bool voidOne = false)
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(doc.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();
        // The repeated awards are a hook; the projection doesn't need it, but the decider must compile.
        var hook = new GeneratedFile("Grant_roundDecider.Hand.cs", """
            namespace Generated.Grant_round;
            public static partial class Grant_roundDecider
            {
                private static partial IEnumerable<GrantAwardedPayload> CloseGrantRoundOutcome0GrantAwarded(Grant_roundState state, DotnetCqrs.Deciders.Command cmd) => [];
            }
            """);
        return await InMemoryProgram.RunAsync([.. files, hook], voidOne ? ProgramCs.Replace("args.Length > 0 && args[0] == \"void\"", "true") : ProgramCs);
    }

    [Fact]
    public async Task Each_event_updates_the_row_its_field_names_not_its_streams()
    {
        var (success, output) = await RunAsync(JsonNode.Parse(Example)!);

        Assert.True(success, output);
        Assert.Contains("row g1:r1 r1 {\"$pii\":{\"s\":\"r1\",\"c\":\"x\"}}", output);
        Assert.Contains("row g1:r2 r2 {\"$pii\":{\"s\":\"r2\",\"c\":\"y\"}}", output);
        Assert.DoesNotContain("row g1 ", output);
    }

    [Fact]
    public async Task Without_it_a_row_is_a_stream()
    {
        var node = JsonNode.Parse(Example)!;
        node["readModels"]!["awards"]!.AsObject().Remove("rowKeyField");

        var (success, output) = await RunAsync(node);

        Assert.True(success, output);
        Assert.Contains("row g1 r2", output); // both awards merged into the stream's one row
        Assert.DoesNotContain("row g1:r1", output);
    }

    [Fact]
    public async Task A_removal_deletes_the_row_its_field_names()
    {
        var (success, output) = await RunAsync(WithVoid(), voidOne: true);

        Assert.True(success, output);
        Assert.DoesNotContain("row g1:r1", output);
        Assert.Contains("row g1:r2 r2", output);
    }

    // ---- what the mapper refuses ----

    private static IReadOnlyList<string> MappingErrors(Action<JsonNode> change)
    {
        var node = WithVoid();
        change(node);
        try
        {
            DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString()));
            return [];
        }
        catch (DocumentMappingException ex)
        {
            return ex.Report.Errors;
        }
    }

    [Fact]
    public void Every_event_it_is_built_from_must_carry_the_field()
    {
        var errors = MappingErrors(n => n["readModels"]!["awards"]!["rowKeyField"] = "recipientId");
        Assert.Contains(errors, e => e.Contains("\"awards\"") && e.Contains("rowKeyField") && e.Contains("\"recipientId\"") && e.Contains("\"award-revoked\""));
    }

    [Theory]
    [InlineData("contains")] // a search index
    [InlineData("exact")]    // a hashed index
    public void A_search_index_keyed_by_stream_is_refused_with_it(string mode)
    {
        var errors = MappingErrors(n =>
        {
            var name = JsonNode.Parse("""{"name": "recipientName", "type": "string", "pii": true, "piiSubject": "recipientId"}""");
            n["events"]!["grant-awarded"]!["fields"]!.AsArray().Add(name);
            n["readModels"]!["awards"]!["fields"]!.AsArray().Add(name!.DeepClone());
            n["readModels"]!["awards"]!["filters"] = JsonNode.Parse("""[{"param": "q", "field": "recipientName", "kind": "match", "mode": "MODE"}]""".Replace("MODE", mode));
        });
        Assert.Contains(errors, e => e.Contains("\"awards\"") && e.Contains("rowKeyField") && e.Contains("search-indexed"));
    }

    [Fact]
    public void The_field_must_be_required_text()
    {
        var optional = MappingErrors(n => n["events"]!["award-revoked"]!["fields"]![1]!["optional"] = true);
        Assert.Contains(optional, e => e.Contains("\"awards\"") && e.Contains("rowKeyField") && e.Contains("optional"));

        var number = MappingErrors(n => n["events"]!["award-revoked"]!["fields"]![1]!["type"] = "decimal");
        Assert.Contains(number, e => e.Contains("\"awards\"") && e.Contains("rowKeyField") && e.Contains("text"));
    }
}
