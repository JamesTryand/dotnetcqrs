using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.16.0 <c>readModel.rowsPerListItem</c>: one row per group of a stream's list items, keyed
/// <c>&lt;stream&gt;|&lt;group&gt;</c>, with <c>sumOfItems</c> (a subfield summed over the row's items, absent unless every
/// item carries it) and <c>byEvent</c> (a literal per event). Raised by timesheets D33: a PM sees their project's part of a
/// client's invoice, which was a hand projection whose view scenarios couldn't be checked against generated code.
/// The worked values are D33's: p1's two lines of 306.00 are 612.00, p2's one is 136.00, and the 22.44 conversion margin
/// has no project, so it is in neither.
/// </summary>
[Trait("Category", "Compiles")]
public class RowsPerListItemTests
{
    private const int VerifyTimeoutMs = 120000;

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static JsonNode Example() => JsonNode.Parse(File.ReadAllText(TestDataPath("rows-per-list-item.json")))!;

    private const string ProgramCs = """"
        using System.Globalization;
        using System.Text.Json.Nodes;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Invoice;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new InvoicePartsProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string stream, string type, string data) => new(++position, $"e{position}", "invoice", stream, position, type, data, "{}", "2026-10-01T00:00:00Z");

        async Task Show(string label)
        {
            await using var select = readModel.Connection.CreateCommand();
            select.CommandText = "SELECT part_id, invoice_id, project_id, customer_id, invoice_number, status, subtotal, lines FROM invoiceParts ORDER BY part_id";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string Text(int i) => reader.IsDBNull(i) ? "-" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
                var subtotal = reader.IsDBNull(6) ? "null" : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);
                var entries = string.Join(",", JsonNode.Parse(reader.GetString(7))!.AsArray().Select(l => l!["entryId"]!.GetValue<string>()));
                Console.WriteLine($"{label}: {Text(0)} {Text(1)} {Text(2)} {Text(3)} {Text(4)} {Text(5)} {subtotal} [{entries}]");
            }
        }

        await projection.ApplyAsync(On("inv-1", "InvoiceDrafted", """{"invoiceId":"inv-1","customerId":"c1","lines":[{"entryId":"t1","projectId":"p1","localAmount":225},{"entryId":"t2","projectId":"p1","localAmount":225},{"entryId":"t3","projectId":"p2","localAmount":100}]}"""), default);
        await Show("drafted");
        await projection.ApplyAsync(On("inv-2", "InvoiceDrafted", """{"invoiceId":"inv-2","customerId":"c2","lines":[{"entryId":"t4","projectId":"p1","localAmount":50}]}"""), default);
        await projection.ApplyAsync(On("inv-1", "InvoiceIssued", """{"invoiceId":"inv-1","invoiceNumber":"SI-7","invoiceCurrency":"USD","lines":[{"entryId":"t1","projectId":"p1","localAmount":225,"convertedAmount":306},{"entryId":"t2","projectId":"p1","localAmount":225,"convertedAmount":306},{"entryId":"t3","projectId":"p2","localAmount":100,"convertedAmount":136},{"entryId":"currency-conversion","convertedAmount":22.44,"description":"Currency conversion"}]}"""), default);
        await Show("issued");
        // Applied again (at-least-once delivery): the same rows, not doubled sums.
        await projection.ApplyAsync(On("inv-1", "InvoiceIssued", """{"invoiceId":"inv-1","invoiceNumber":"SI-7","invoiceCurrency":"USD","lines":[{"entryId":"t1","projectId":"p1","localAmount":225,"convertedAmount":306},{"entryId":"t2","projectId":"p1","localAmount":225,"convertedAmount":306},{"entryId":"t3","projectId":"p2","localAmount":100,"convertedAmount":136},{"entryId":"currency-conversion","convertedAmount":22.44,"description":"Currency conversion"}]}"""), default);
        await Show("again");
        await projection.ApplyAsync(On("inv-2", "InvoiceDiscarded", """{"invoiceId":"inv-2"}"""), default);
        await Show("discarded");
        return 0;
        """";

    private static async Task<(bool Success, string Output)> RunAsync(JsonNode doc)
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(doc.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();
        return await InMemoryProgram.RunAsync(files, ProgramCs);
    }

    [Fact]
    public async Task Each_group_of_a_streams_items_is_a_row_with_the_streams_fields_its_items_and_their_sum()
    {
        var (success, output) = await RunAsync(Example());

        Assert.True(success, output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
        [
            // A draft's lines are in pounds, not converted: its parts have no subtotal.
            "drafted: inv-1|p1 inv-1 p1 c1 - draft null [t1,t2]",
            "drafted: inv-1|p2 inv-1 p2 c1 - draft null [t3]",
            // Issued: the customer still from the draft; the margin line has no project, so it is in no row.
            "issued: inv-1|p1 inv-1 p1 c1 SI-7 issued 612.00 [t1,t2]",
            "issued: inv-1|p2 inv-1 p2 c1 SI-7 issued 136.00 [t3]",
            "issued: inv-2|p1 inv-2 p1 c2 - draft null [t4]",
            "again: inv-1|p1 inv-1 p1 c1 SI-7 issued 612.00 [t1,t2]",
            "again: inv-1|p2 inv-1 p2 c1 SI-7 issued 136.00 [t3]",
            "again: inv-2|p1 inv-2 p1 c2 - draft null [t4]",
            // A removal takes every row of its own stream, and only those.
            "discarded: inv-1|p1 inv-1 p1 c1 SI-7 issued 612.00 [t1,t2]",
            "discarded: inv-1|p2 inv-1 p2 c1 SI-7 issued 136.00 [t3]",
        ], lines);
    }

    [Fact]
    public void The_projection_owns_its_streams_table_so_a_rebuild_clears_both()
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(Example().ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var projection = files.Single(f => f.Name == "InvoicePartsProjection.cs").Source;
        Assert.Contains("public IReadOnlyList<string> Tables => [\"invoiceParts\", \"invoiceParts_streams\"];", projection);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_schema_example_verifies_every_scenario()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("rows-per-list-item.json"));

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Two_invoices_in_one_view_scenario_stay_two_streams()
    {
        // The harness keys each given event by its own stream (its idAttribute), so two invoices aren't merged.
        var node = Example();
        var scenario = node["slices"]!.AsArray().Single(s => (string?)s!["id"] == "view-invoice-parts-slice")!["scenarios"]![1]!;
        scenario["given"]!.AsArray().Add(JsonNode.Parse("""
            {"eventId": "invoice-drafted", "data": {"invoiceId": "inv-2", "customerId": "c2", "lines": [{"entryId": "t4", "projectId": "p1", "localAmount": 50}]}}
            """));
        scenario["then"]!["result"]!["entries"]!.AsArray().Add(JsonNode.Parse("""{"invoiceId": "inv-2", "projectId": "p1", "customerId": "c2"}"""));
        scenario["then"]!["result"]!["entries"]![0]!["customerId"] = "c1";
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        var result = results.Single(r => r.ScenarioId == "a-drafts-parts-have-no-subtotal");
        Assert.True(result.Passed, result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_it_the_view_scenarios_fail()
    {
        // The scenarios must be able to fail, or they prove nothing about the generated projection.
        var node = Example();
        var parts = node["readModels"]!["invoice-parts"]!.AsObject();
        parts.Remove("rowsPerListItem");
        foreach (var field in parts["fields"]!.AsArray())
            if ((string?)field!["derivation"]?["kind"] == "sumOfItems") field.AsObject().Remove("derivation");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "issued-invoice-has-a-part-per-project").Passed);
    }

    // ---- byEvent on an ordinary read model (one row per stream) ----

    private const string StatusProgramCs = """"
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Invoice;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new InvoicesProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string type, string data) => new(++position, $"e{position}", "invoice", "inv-1", position, type, data, "{}", "2026-10-01T00:00:00Z");
        async Task Show(string label)
        {
            await using var select = readModel.Connection.CreateCommand();
            select.CommandText = "SELECT invoice_id, status FROM invoices";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync()) Console.WriteLine($"{label}: {reader.GetString(0)} {(reader.IsDBNull(1) ? "-" : reader.GetString(1))}");
        }
        await projection.ApplyAsync(On("InvoiceDrafted", """{"invoiceId":"inv-1","customerId":"c1","lines":[]}"""), default);
        await Show("drafted");
        await projection.ApplyAsync(On("InvoiceIssued", """{"invoiceId":"inv-1","invoiceNumber":"SI-7","lines":[]}"""), default);
        await Show("issued");
        await projection.ApplyAsync(On("InvoiceDiscarded", """{"invoiceId":"inv-1"}"""), default);
        await Show("discarded");
        return 0;
        """";

    [Fact]
    public async Task By_event_sets_a_literal_per_event_and_an_event_not_named_leaves_it()
    {
        var node = Example();
        node["readModels"]!["invoices"] = JsonNode.Parse("""
            {"name": "Invoices", "builtFromEventIds": ["invoice-drafted", "invoice-issued", "invoice-discarded"],
             "fields": [{"name": "invoiceId", "type": "string", "idAttribute": true},
                        {"name": "status", "type": "string", "derivation": {"kind": "byEvent", "values": {"invoice-drafted": "draft", "invoice-issued": "issued"}}}]}
            """);
        var files = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, StatusProgramCs);

        Assert.True(success, output);
        Assert.Contains("drafted: inv-1 draft", output);
        Assert.Contains("issued: inv-1 issued", output);
        Assert.Contains("discarded: inv-1 issued", output);
    }

    [Fact]
    public async Task By_event_reacts_to_the_events_it_names_as_a_toggle_does()
    {
        // Built from the draft alone: the issue still sets the status, because byEvent names it.
        var node = Example();
        node["readModels"]!["invoices"] = JsonNode.Parse("""
            {"name": "Invoices", "builtFromEventIds": ["invoice-drafted"],
             "fields": [{"name": "invoiceId", "type": "string", "idAttribute": true},
                        {"name": "status", "type": "string", "derivation": {"kind": "byEvent", "values": {"invoice-drafted": "draft", "invoice-issued": "issued"}}}]}
            """);
        var files = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, StatusProgramCs);

        Assert.True(success, output);
        Assert.Contains("issued: inv-1 issued", output);
    }

    // ---- what the mapper refuses ----

    private static IReadOnlyList<string> MappingErrors(Action<JsonNode> change)
    {
        var node = Example();
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

    private static JsonNode Parts(JsonNode node) => node["readModels"]!["invoice-parts"]!;

    private static JsonNode PartsField(JsonNode node, string name) =>
        Parts(node)["fields"]!.AsArray().Single(f => (string?)f!["name"] == name)!;

    [Fact]
    public void The_example_maps_cleanly()
    {
        Assert.Empty(MappingErrors(_ => { }));
    }

    [Fact]
    public void Not_with_row_key_field()
    {
        var errors = MappingErrors(n => Parts(n)["rowKeyField"] = "invoiceId");
        Assert.Contains(errors, e => e.Contains("\"invoice-parts\"") && e.Contains("rowsPerListItem") && e.Contains("rowKeyField"));
    }

    [Fact]
    public void The_list_field_must_be_a_list_of_items_the_read_model_declares()
    {
        var missing = MappingErrors(n => Parts(n)["rowsPerListItem"]!["listField"] = "items");
        Assert.Contains(missing, e => e.Contains("rowsPerListItem") && e.Contains("\"items\""));

        var scalar = MappingErrors(n => Parts(n)["rowsPerListItem"]!["listField"] = "invoiceNumber");
        Assert.Contains(scalar, e => e.Contains("rowsPerListItem") && e.Contains("\"invoiceNumber\"") && e.Contains("must be a list field"));

        // One object with subfields is not a list of items.
        var single = MappingErrors(n =>
        {
            var address = JsonNode.Parse("""{"name": "address", "type": "custom", "subfields": [{"name": "projectId", "type": "string"}]}""");
            n["events"]!["invoice-drafted"]!["fields"]!.AsArray().Add(address);
            Parts(n)["fields"]!.AsArray().Add(address!.DeepClone());
            Parts(n)["rowsPerListItem"]!["listField"] = "address";
        });
        Assert.Contains(single, e => e.Contains("rowsPerListItem") && e.Contains("\"address\"") && e.Contains("must be a list field"));
    }

    [Fact]
    public void The_group_must_be_a_text_subfield_of_the_items_and_a_field_of_the_row()
    {
        var notSubfield = MappingErrors(n => Parts(n)["rowsPerListItem"]!["groupBy"] = "customerId");
        Assert.Contains(notSubfield, e => e.Contains("rowsPerListItem") && e.Contains("\"customerId\"") && e.Contains("subfield"));

        var number = MappingErrors(n => Parts(n)["rowsPerListItem"]!["groupBy"] = "localAmount");
        Assert.Contains(number, e => e.Contains("rowsPerListItem") && e.Contains("\"localAmount\"") && e.Contains("text"));

        var noRowField = MappingErrors(n => Parts(n)["fields"]!.AsArray().Remove(PartsField(n, "projectId")));
        Assert.Contains(noRowField, e => e.Contains("rowsPerListItem") && e.Contains("\"projectId\"") && e.Contains("field of the read model"));
    }

    [Fact]
    public void A_sum_of_items_needs_rows_per_list_item_and_a_numeric_subfield()
    {
        var alone = MappingErrors(n => Parts(n).AsObject().Remove("rowsPerListItem"));
        Assert.Contains(alone, e => e.Contains("\"subtotal\"") && e.Contains("sumOfItems") && e.Contains("rowsPerListItem"));

        var text = MappingErrors(n => PartsField(n, "subtotal")["derivation"]!["subfield"] = "description");
        Assert.Contains(text, e => e.Contains("\"subtotal\"") && e.Contains("\"description\"") && e.Contains("number"));

        var unknown = MappingErrors(n => PartsField(n, "subtotal")["derivation"]!["subfield"] = "vatAmount");
        Assert.Contains(unknown, e => e.Contains("\"subtotal\"") && e.Contains("\"vatAmount\""));
    }

    [Fact]
    public void A_by_event_value_must_suit_the_field_and_name_an_event()
    {
        var number = MappingErrors(n => PartsField(n, "status")["derivation"]!["values"]!["invoice-issued"] = 2);
        Assert.Contains(number, e => e.Contains("\"status\"") && e.Contains("byEvent") && e.Contains("\"invoice-issued\""));

        var unknown = MappingErrors(n => PartsField(n, "status")["derivation"]!["values"]!["invoice-paid"] = "paid");
        Assert.Contains(unknown, e => e.Contains("\"status\"") && e.Contains("byEvent") && e.Contains("\"invoice-paid\""));
    }

    [Fact]
    public void Rollups_and_pii_are_refused_with_it()
    {
        var count = MappingErrors(n => Parts(n)["fields"]!.AsArray().Add(JsonNode.Parse("""
            {"name": "issues", "type": "integer", "derivation": {"kind": "count", "incrementOnEventIds": ["invoice-issued"], "rowKeyField": "invoiceId"}}
            """)));
        Assert.Contains(count, e => e.Contains("\"invoice-parts\"") && e.Contains("rowsPerListItem") && e.Contains("\"issues\""));

        var pii = MappingErrors(n =>
        {
            var name = JsonNode.Parse("""{"name": "contactName", "type": "string", "pii": true, "piiSubject": "customerId"}""");
            n["events"]!["invoice-drafted"]!["fields"]!.AsArray().Add(name);
            Parts(n)["fields"]!.AsArray().Add(name!.DeepClone());
        });
        Assert.Contains(pii, e => e.Contains("\"invoice-parts\"") && e.Contains("rowsPerListItem") && e.Contains("pii"));
    }
}
