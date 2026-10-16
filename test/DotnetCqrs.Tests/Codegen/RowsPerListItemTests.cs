using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.16.0 <c>readModel.rowsPerListItem</c>: one row per group of a stream's list items, keyed
/// <c>&lt;stream&gt;|&lt;group&gt;</c>, with <c>sumOfItems</c> (a subfield summed over the row's items, absent unless every
/// item carries it) and <c>byEvent</c> (a literal per event). Such a projection used to be written by hand, so its view scenarios couldn't be
/// checked against generated code.
/// The worked values are the example's: d1's two parcels of 13.00 kg are 26.00 kg, d2's one is 6.00 kg, and the 40.50 kg
/// of roll cages has no depot, so it is in neither.
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
        using Generated.Delivery;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new DepotLoadsProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string stream, string type, string data) => new(++position, $"e{position}", "delivery", stream, position, type, data, "{}", "2026-10-01T00:00:00Z");

        async Task Show(string label)
        {
            await using var select = readModel.Connection.CreateCommand();
            select.CommandText = "SELECT load_id, delivery_id, depot_id, customer_id, run_number, status, total_weight_kg, parcels FROM depotLoads ORDER BY load_id";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string Text(int i) => reader.IsDBNull(i) ? "-" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
                var weight = reader.IsDBNull(6) ? "null" : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture);
                var parcels = string.Join(",", JsonNode.Parse(reader.GetString(7))!.AsArray().Select(l => l!["parcelId"]!.GetValue<string>()));
                Console.WriteLine($"{label}: {Text(0)} {Text(1)} {Text(2)} {Text(3)} {Text(4)} {Text(5)} {weight} [{parcels}]");
            }
        }

        await projection.ApplyAsync(On("del-1", "DeliveryPlanned", """{"deliveryId":"del-1","customerId":"c1","parcels":[{"parcelId":"pc1","depotId":"d1","declaredWeightKg":12},{"parcelId":"pc2","depotId":"d1","declaredWeightKg":12},{"parcelId":"pc3","depotId":"d2","declaredWeightKg":5}]}"""), default);
        await Show("planned");
        await projection.ApplyAsync(On("del-2", "DeliveryPlanned", """{"deliveryId":"del-2","customerId":"c2","parcels":[{"parcelId":"pc4","depotId":"d1","declaredWeightKg":4}]}"""), default);
        await projection.ApplyAsync(On("del-1", "DeliveryDispatched", """{"deliveryId":"del-1","runNumber":"RUN-7","vehicleId":"van-3","parcels":[{"parcelId":"pc1","depotId":"d1","declaredWeightKg":12,"weighedWeightKg":13},{"parcelId":"pc2","depotId":"d1","declaredWeightKg":12,"weighedWeightKg":13},{"parcelId":"pc3","depotId":"d2","declaredWeightKg":5,"weighedWeightKg":6},{"parcelId":"roll-cages","weighedWeightKg":40.5,"description":"Roll cages"}]}"""), default);
        await Show("dispatched");
        // Applied again (at-least-once delivery): the same rows, not doubled sums.
        await projection.ApplyAsync(On("del-1", "DeliveryDispatched", """{"deliveryId":"del-1","runNumber":"RUN-7","vehicleId":"van-3","parcels":[{"parcelId":"pc1","depotId":"d1","declaredWeightKg":12,"weighedWeightKg":13},{"parcelId":"pc2","depotId":"d1","declaredWeightKg":12,"weighedWeightKg":13},{"parcelId":"pc3","depotId":"d2","declaredWeightKg":5,"weighedWeightKg":6},{"parcelId":"roll-cages","weighedWeightKg":40.5,"description":"Roll cages"}]}"""), default);
        await Show("again");
        await projection.ApplyAsync(On("del-2", "DeliveryCancelled", """{"deliveryId":"del-2"}"""), default);
        await Show("cancelled");
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
            // A planned delivery's parcels are not weighed yet: its loads have no total weight.
            "planned: del-1|d1 del-1 d1 c1 - planned null [pc1,pc2]",
            "planned: del-1|d2 del-1 d2 c1 - planned null [pc3]",
            // Dispatched: the customer still from the plan; the roll cages have no depot, so they are in no row.
            "dispatched: del-1|d1 del-1 d1 c1 RUN-7 dispatched 26.00 [pc1,pc2]",
            "dispatched: del-1|d2 del-1 d2 c1 RUN-7 dispatched 6.00 [pc3]",
            "dispatched: del-2|d1 del-2 d1 c2 - planned null [pc4]",
            "again: del-1|d1 del-1 d1 c1 RUN-7 dispatched 26.00 [pc1,pc2]",
            "again: del-1|d2 del-1 d2 c1 RUN-7 dispatched 6.00 [pc3]",
            "again: del-2|d1 del-2 d1 c2 - planned null [pc4]",
            // A removal takes every row of its own stream, and only those.
            "cancelled: del-1|d1 del-1 d1 c1 RUN-7 dispatched 26.00 [pc1,pc2]",
            "cancelled: del-1|d2 del-1 d2 c1 RUN-7 dispatched 6.00 [pc3]",
        ], lines);
    }

    [Fact]
    public void The_projection_owns_its_streams_table_so_a_rebuild_clears_both()
    {
        var files = DocumentMapper.Map(DocumentLoader.Parse(Example().ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var projection = files.Single(f => f.Name == "DepotLoadsProjection.cs").Source;
        Assert.Contains("public IReadOnlyList<string> Tables => [\"depotLoads\", \"depotLoads_streams\"];", projection);
    }

    [Fact]
    public void The_query_route_reads_a_sum_of_items_as_a_decimal_not_a_float()
    {
        // Decimal end to end; SQLite holds NUMERIC as a float, so the route reads it back as a decimal.
        var domain = Assert.Single(DocumentMapper.Map(DocumentLoader.Parse(Example().ToJsonString())).Domains);

        var route = ReadModelQueryGenerator.Generate(domain, domain.ReadModels.Single(r => r.Collection == "depotLoads")).Source;
        Assert.Contains("reader.GetName(c) is \"total_weight_kg\" ? reader.GetDecimal(c)", route);
    }

    [Fact]
    public void The_query_route_reads_a_decimal_by_event_value_as_money()
    {
        var node = Example();
        node["readModels"]!["deliveries"] = JsonNode.Parse("""
            {"name": "Deliveries", "builtFromEventIds": ["delivery-planned", "delivery-dispatched"],
             "fields": [{"name": "deliveryId", "type": "string", "idAttribute": true},
                        {"name": "fee", "type": "decimal", "derivation": {"kind": "byEvent", "values": {"delivery-dispatched": 12.5}}}]}
            """);
        var domain = Assert.Single(DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains);

        var route = ReadModelQueryGenerator.Generate(domain, domain.ReadModels.Single(r => r.Collection == "deliveries")).Source;
        Assert.Contains("reader.GetName(c) is \"fee\" ? reader.GetDecimal(c)", route);
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
    public async Task Two_deliveries_in_one_view_scenario_stay_two_streams()
    {
        // The harness keys each given event by its own stream (its idAttribute), so two deliveries aren't merged.
        var node = Example();
        var scenario = node["slices"]!.AsArray().Single(s => (string?)s!["id"] == "view-depot-loads-slice")!["scenarios"]![1]!;
        scenario["given"]!.AsArray().Add(JsonNode.Parse("""
            {"eventId": "delivery-planned", "data": {"deliveryId": "del-2", "customerId": "c2", "parcels": [{"parcelId": "pc4", "depotId": "d1", "declaredWeightKg": 4}]}}
            """));
        scenario["then"]!["result"]!["entries"]!.AsArray().Add(JsonNode.Parse("""{"deliveryId": "del-2", "depotId": "d1", "customerId": "c2"}"""));
        scenario["then"]!["result"]!["entries"]![0]!["customerId"] = "c1";
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        var result = results.Single(r => r.ScenarioId == "a-planned-deliverys-loads-have-no-total-weight");
        Assert.True(result.Passed, result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_it_the_view_scenarios_fail()
    {
        // The scenarios must be able to fail, or they prove nothing about the generated projection.
        var node = Example();
        var loads = node["readModels"]!["depot-loads"]!.AsObject();
        loads.Remove("rowsPerListItem");
        foreach (var field in loads["fields"]!.AsArray())
            if ((string?)field!["derivation"]?["kind"] == "sumOfItems") field.AsObject().Remove("derivation");
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "dispatched-delivery-has-a-load-per-depot").Passed);
    }

    // ---- byEvent on an ordinary read model (one row per stream) ----

    private const string StatusProgramCs = """"
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Delivery;

        var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new DeliveriesProjection(readModel);
        await projection.InitAsync();
        var position = 0L;
        Event On(string type, string data) => new(++position, $"e{position}", "delivery", "del-1", position, type, data, "{}", "2026-10-01T00:00:00Z");
        async Task Show(string label)
        {
            await using var select = readModel.Connection.CreateCommand();
            select.CommandText = "SELECT delivery_id, status FROM deliveries";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync()) Console.WriteLine($"{label}: {reader.GetString(0)} {(reader.IsDBNull(1) ? "-" : reader.GetString(1))}");
        }
        await projection.ApplyAsync(On("DeliveryPlanned", """{"deliveryId":"del-1","customerId":"c1","parcels":[]}"""), default);
        await Show("planned");
        await projection.ApplyAsync(On("DeliveryDispatched", """{"deliveryId":"del-1","runNumber":"RUN-7","parcels":[]}"""), default);
        await Show("dispatched");
        await projection.ApplyAsync(On("DeliveryCancelled", """{"deliveryId":"del-1"}"""), default);
        await Show("cancelled");
        return 0;
        """";

    [Fact]
    public async Task By_event_sets_a_literal_per_event_and_an_event_not_named_leaves_it()
    {
        var node = Example();
        node["readModels"]!["deliveries"] = JsonNode.Parse("""
            {"name": "Deliveries", "builtFromEventIds": ["delivery-planned", "delivery-dispatched", "delivery-cancelled"],
             "fields": [{"name": "deliveryId", "type": "string", "idAttribute": true},
                        {"name": "status", "type": "string", "derivation": {"kind": "byEvent", "values": {"delivery-planned": "planned", "delivery-dispatched": "dispatched"}}}]}
            """);
        var files = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, StatusProgramCs);

        Assert.True(success, output);
        Assert.Contains("planned: del-1 planned", output);
        Assert.Contains("dispatched: del-1 dispatched", output);
        Assert.Contains("cancelled: del-1 dispatched", output);
    }

    [Fact]
    public async Task By_event_reacts_to_the_events_it_names_as_a_toggle_does()
    {
        // Built from the plan alone: the dispatch still sets the status, because byEvent names it.
        var node = Example();
        node["readModels"]!["deliveries"] = JsonNode.Parse("""
            {"name": "Deliveries", "builtFromEventIds": ["delivery-planned"],
             "fields": [{"name": "deliveryId", "type": "string", "idAttribute": true},
                        {"name": "status", "type": "string", "derivation": {"kind": "byEvent", "values": {"delivery-planned": "planned", "delivery-dispatched": "dispatched"}}}]}
            """);
        var files = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

        var (success, output) = await InMemoryProgram.RunAsync(files, StatusProgramCs);

        Assert.True(success, output);
        Assert.Contains("dispatched: del-1 dispatched", output);
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

    private static JsonNode Loads(JsonNode node) => node["readModels"]!["depot-loads"]!;

    private static JsonNode LoadsField(JsonNode node, string name) =>
        Loads(node)["fields"]!.AsArray().Single(f => (string?)f!["name"] == name)!;

    [Fact]
    public void The_example_maps_cleanly()
    {
        Assert.Empty(MappingErrors(_ => { }));
    }

    [Fact]
    public void Not_with_row_key_field()
    {
        var errors = MappingErrors(n => Loads(n)["rowKeyField"] = "deliveryId");
        Assert.Contains(errors, e => e.Contains("\"depot-loads\"") && e.Contains("rowsPerListItem") && e.Contains("rowKeyField"));
    }

    [Fact]
    public void The_list_field_must_be_a_list_of_items_the_read_model_declares()
    {
        var missing = MappingErrors(n => Loads(n)["rowsPerListItem"]!["listField"] = "items");
        Assert.Contains(missing, e => e.Contains("rowsPerListItem") && e.Contains("\"items\""));

        var scalar = MappingErrors(n => Loads(n)["rowsPerListItem"]!["listField"] = "runNumber");
        Assert.Contains(scalar, e => e.Contains("rowsPerListItem") && e.Contains("\"runNumber\"") && e.Contains("must be a list field"));

        // One object with subfields is not a list of items.
        var single = MappingErrors(n =>
        {
            var address = JsonNode.Parse("""{"name": "address", "type": "custom", "subfields": [{"name": "depotId", "type": "string"}]}""");
            n["events"]!["delivery-planned"]!["fields"]!.AsArray().Add(address);
            Loads(n)["fields"]!.AsArray().Add(address!.DeepClone());
            Loads(n)["rowsPerListItem"]!["listField"] = "address";
        });
        Assert.Contains(single, e => e.Contains("rowsPerListItem") && e.Contains("\"address\"") && e.Contains("must be a list field"));
    }

    [Fact]
    public void The_group_must_be_a_text_subfield_of_the_items_and_a_field_of_the_row()
    {
        var notSubfield = MappingErrors(n => Loads(n)["rowsPerListItem"]!["groupBy"] = "customerId");
        Assert.Contains(notSubfield, e => e.Contains("rowsPerListItem") && e.Contains("\"customerId\"") && e.Contains("subfield"));

        var number = MappingErrors(n => Loads(n)["rowsPerListItem"]!["groupBy"] = "declaredWeightKg");
        Assert.Contains(number, e => e.Contains("rowsPerListItem") && e.Contains("\"declaredWeightKg\"") && e.Contains("text"));

        var noRowField = MappingErrors(n => Loads(n)["fields"]!.AsArray().Remove(LoadsField(n, "depotId")));
        Assert.Contains(noRowField, e => e.Contains("rowsPerListItem") && e.Contains("\"depotId\"") && e.Contains("field of the read model"));
    }

    [Fact]
    public void A_sum_of_items_needs_rows_per_list_item_and_a_numeric_subfield()
    {
        var alone = MappingErrors(n => Loads(n).AsObject().Remove("rowsPerListItem"));
        Assert.Contains(alone, e => e.Contains("\"totalWeightKg\"") && e.Contains("sumOfItems") && e.Contains("rowsPerListItem"));

        var text = MappingErrors(n => LoadsField(n, "totalWeightKg")["derivation"]!["subfield"] = "description");
        Assert.Contains(text, e => e.Contains("\"totalWeightKg\"") && e.Contains("\"description\"") && e.Contains("number"));

        var unknown = MappingErrors(n => LoadsField(n, "totalWeightKg")["derivation"]!["subfield"] = "volumeLitres");
        Assert.Contains(unknown, e => e.Contains("\"totalWeightKg\"") && e.Contains("\"volumeLitres\""));
    }

    [Fact]
    public void A_by_event_value_must_suit_the_field_and_name_an_event()
    {
        var number = MappingErrors(n => LoadsField(n, "status")["derivation"]!["values"]!["delivery-dispatched"] = 2);
        Assert.Contains(number, e => e.Contains("\"status\"") && e.Contains("byEvent") && e.Contains("\"delivery-dispatched\""));

        var unknown = MappingErrors(n => LoadsField(n, "status")["derivation"]!["values"]!["delivery-returned"] = "returned");
        Assert.Contains(unknown, e => e.Contains("\"status\"") && e.Contains("byEvent") && e.Contains("\"delivery-returned\""));
    }

    [Fact]
    public void Rollups_and_pii_are_refused_with_it()
    {
        var count = MappingErrors(n => Loads(n)["fields"]!.AsArray().Add(JsonNode.Parse("""
            {"name": "dispatches", "type": "integer", "derivation": {"kind": "count", "incrementOnEventIds": ["delivery-dispatched"], "rowKeyField": "deliveryId"}}
            """)));
        Assert.Contains(count, e => e.Contains("\"depot-loads\"") && e.Contains("rowsPerListItem") && e.Contains("\"dispatches\""));

        var pii = MappingErrors(n =>
        {
            var name = JsonNode.Parse("""{"name": "contactName", "type": "string", "pii": true, "piiSubject": "customerId"}""");
            n["events"]!["delivery-planned"]!["fields"]!.AsArray().Add(name);
            Loads(n)["fields"]!.AsArray().Add(name!.DeepClone());
        });
        Assert.Contains(pii, e => e.Contains("\"depot-loads\"") && e.Contains("rowsPerListItem") && e.Contains("pii"));
    }
}
