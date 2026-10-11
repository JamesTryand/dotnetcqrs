using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.14.0 <c>readModel.rowKeyField</c>: rows keyed by a field the events carry, in place of
/// their stream, for one stream that holds many rows' events. Raised by timesheets D30.20 (F16):
/// the payroll is one stream, and each release's payments are a row each. The generated projection
/// is run directly, with every event on the same stream: the scenario harness keys a given event by
/// the read model's own id field, so a view scenario can't tell a stream key from a row key.
/// </summary>
[Trait("Category", "Compiles")]
public class RowKeyFieldTests
{
    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "repeated-outcomes.json"));

    /// <summary>The example with a void event that removes a payment's row.</summary>
    private static JsonNode WithVoid()
    {
        var node = JsonNode.Parse(Example)!;
        node["events"]!["payment-voided"] = JsonNode.Parse("""
            {"name": "Payment Voided", "swimlaneId": "payroll", "aggregate": "pay_run",
             "fields": [{"name": "payRunId", "type": "string", "idAttribute": true}, {"name": "paymentId", "type": "string"}]}
            """);
        node["commands"]!["void-payment"] = JsonNode.Parse("""
            {"name": "Void Payment", "aggregate": "pay_run", "fields": [{"name": "paymentId", "type": "string"}]}
            """);
        node["slices"]!.AsArray().Add(JsonNode.Parse("""
            {"id": "void-payment-slice", "name": "Void Payment", "pattern": "stateChange", "swimlaneId": "payroll",
             "status": "created", "screenId": "payroll-screen", "commandId": "void-payment", "eventIds": ["payment-voided"], "scenarios": []}
            """));
        var payments = node["readModels"]!["payments"]!;
        payments["builtFromEventIds"]!.AsArray().Add("payment-voided");
        payments["removedByEventIds"] = new JsonArray("payment-voided");
        return node;
    }

    private const string ProgramCs = """"
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Pay_run;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new PaymentsProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string type, string data) => new(++position, $"e{position}", "pay_run", "r1", position, type, data, "{}", "2026-10-01T00:00:00Z");
        await projection.ApplyAsync(On("PersonPaid", """{"payRunId":"r1","personId":"p1","paymentId":"r1:p1","periodEnd":"2026-09-30","amount":{"$pii":{"s":"p1","c":"x"}}}"""), default);
        await projection.ApplyAsync(On("PersonPaid", """{"payRunId":"r1","personId":"p2","paymentId":"r1:p2","periodEnd":"2026-09-30","amount":{"$pii":{"s":"p2","c":"y"}}}"""), default);
        if (args.Length > 0 && args[0] == "void")
            await projection.ApplyAsync(On("PaymentVoided", """{"payRunId":"r1","paymentId":"r1:p1"}"""), default);

        await using var select = readModel.Connection.CreateCommand();
        select.CommandText = "SELECT payment_id, person_id, amount FROM payments ORDER BY payment_id";
        await using var reader = await select.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Console.WriteLine($"row {reader.GetString(0)} {reader.GetString(1)} {reader.GetString(2)}");
        return 0;
        """";

    private static async Task<(bool Success, string Output)> RunAsync(JsonNode doc, bool voidOne = false)
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(doc.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();
        // The repeated payments are a hook; the projection doesn't need it, but the decider must compile.
        var hook = new GeneratedFile("Pay_runDecider.Hand.cs", """
            namespace Generated.Pay_run;
            public static partial class Pay_runDecider
            {
                private static partial IEnumerable<PersonPaidPayload> ReleasePayRunOutcome0PersonPaid(Pay_runState state, DotnetCqrs.Deciders.Command cmd) => [];
            }
            """);
        return await InMemoryProgram.RunAsync([.. files, hook], voidOne ? ProgramCs.Replace("args.Length > 0 && args[0] == \"void\"", "true") : ProgramCs);
    }

    [Fact]
    public async Task Each_event_updates_the_row_its_field_names_not_its_streams()
    {
        var (success, output) = await RunAsync(JsonNode.Parse(Example)!);

        Assert.True(success, output);
        Assert.Contains("row r1:p1 p1 {\"$pii\":{\"s\":\"p1\",\"c\":\"x\"}}", output);
        Assert.Contains("row r1:p2 p2 {\"$pii\":{\"s\":\"p2\",\"c\":\"y\"}}", output);
        Assert.DoesNotContain("row r1 ", output);
    }

    [Fact]
    public async Task Without_it_a_row_is_a_stream()
    {
        var node = JsonNode.Parse(Example)!;
        node["readModels"]!["payments"]!.AsObject().Remove("rowKeyField");

        var (success, output) = await RunAsync(node);

        Assert.True(success, output);
        Assert.Contains("row r1 p2", output); // both payments merged into the stream's one row
        Assert.DoesNotContain("row r1:p1", output);
    }

    [Fact]
    public async Task A_removal_deletes_the_row_its_field_names()
    {
        var (success, output) = await RunAsync(WithVoid(), voidOne: true);

        Assert.True(success, output);
        Assert.DoesNotContain("row r1:p1", output);
        Assert.Contains("row r1:p2 p2", output);
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
        var errors = MappingErrors(n => n["readModels"]!["payments"]!["rowKeyField"] = "personId");
        Assert.Contains(errors, e => e.Contains("\"payments\"") && e.Contains("rowKeyField") && e.Contains("\"personId\"") && e.Contains("\"payment-voided\""));
    }

    [Theory]
    [InlineData("contains")] // a search index
    [InlineData("exact")]    // a hashed index
    public void A_search_index_keyed_by_stream_is_refused_with_it(string mode)
    {
        var errors = MappingErrors(n =>
        {
            var name = JsonNode.Parse("""{"name": "personName", "type": "string", "pii": true, "piiSubject": "personId"}""");
            n["events"]!["person-paid"]!["fields"]!.AsArray().Add(name);
            n["readModels"]!["payments"]!["fields"]!.AsArray().Add(name!.DeepClone());
            n["readModels"]!["payments"]!["filters"] = JsonNode.Parse("""[{"param": "q", "field": "personName", "kind": "match", "mode": "MODE"}]""".Replace("MODE", mode));
        });
        Assert.Contains(errors, e => e.Contains("\"payments\"") && e.Contains("rowKeyField") && e.Contains("search-indexed"));
    }

    [Fact]
    public void The_field_must_be_required_text()
    {
        var optional = MappingErrors(n => n["events"]!["payment-voided"]!["fields"]![1]!["optional"] = true);
        Assert.Contains(optional, e => e.Contains("\"payments\"") && e.Contains("rowKeyField") && e.Contains("optional"));

        var number = MappingErrors(n => n["events"]!["payment-voided"]!["fields"]![1]!["type"] = "decimal");
        Assert.Contains(number, e => e.Contains("\"payments\"") && e.Contains("rowKeyField") && e.Contains("text"));
    }
}
