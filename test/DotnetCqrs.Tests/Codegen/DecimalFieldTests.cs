using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// The schema's <c>decimal</c> field type is exact: a C# <c>decimal</c>, a <c>NUMERIC</c> column, read
/// back as a decimal. It was folded into the one <c>number</c> type, a <c>double</c>, so money declared
/// <c>decimal</c> lost half-pennies (30.06 x 8.25 = 247.995 rounded to 247.99) and a summed column
/// printed 565.1500000000001. Raised by timesheets D30.21 (F17). <c>double</c>, <c>integer</c> and
/// <c>long</c> still fold to <c>number</c>.
/// </summary>
[Trait("Category", "Compiles")]
public class DecimalFieldTests
{
    private const int VerifyTimeoutMs = 120000;

    // A ledger: amounts recorded at a rate and hours, summed per ledger. A ledger refuses an
    // amount once a large one has been recorded, refuses the same amount twice running, and an
    // amount over 1000 is also flagged, with a decimal fee fixed by the outcome.
    private const string LedgerJson = """
        {
          "eventModelingSchemaVersion": "3.14.0", "id": "decimal-test", "name": "Decimal Test",
          "swimlanes": [{"id": "s", "name": "S", "kind": "team"}],
          "events": {
            "ledger-opened": {"name": "Ledger Opened", "swimlaneId": "s", "aggregate": "Ledger", "fields": [
              {"name": "ledgerId", "type": "string", "idAttribute": true}]},
            "amount-recorded": {"name": "Amount Recorded", "swimlaneId": "s", "aggregate": "Ledger", "fields": [
              {"name": "ledgerId", "type": "string", "idAttribute": true}, {"name": "amount", "type": "decimal"},
              {"name": "rate", "type": "decimal"}, {"name": "hours", "type": "decimal"}]},
            "amount-flagged": {"name": "Amount Flagged", "swimlaneId": "s", "aggregate": "Ledger", "fields": [
              {"name": "ledgerId", "type": "string", "idAttribute": true}, {"name": "fee", "type": "decimal"}]}
          },
          "commands": {
            "open-ledger": {"name": "Open Ledger", "aggregate": "Ledger", "fields": []},
            "record-amount": {"name": "Record Amount", "aggregate": "Ledger", "fields": [
                {"name": "amount", "type": "decimal"}, {"name": "rate", "type": "decimal"}, {"name": "hours", "type": "decimal"}],
              "refuseWhen": [
                {"message": "an amount of zero", "all": [{"command": "amount", "equals": 0}]},
                {"message": "ledger closed by a large amount", "all": [{"state": "amount", "greaterThan": 100}]},
                {"message": "the same amount twice", "all": [{"command": "amount", "equals": {"state": "amount"}}]},
                {"message": "amount above the previous one's rate", "all": [{"state": "rate", "is": "set"}, {"command": "amount", "lessThan": {"state": "rate"}}]}
              ]}
          },
          "readModels": {
            "ledgers": {"name": "Ledgers", "builtFromEventIds": ["ledger-opened", "amount-recorded"], "fields": [
              {"name": "ledgerId", "type": "string", "idAttribute": true},
              {"name": "total", "type": "decimal", "derivation": {"kind": "sum", "addOnEventIds": ["amount-recorded"], "amountField": "amount"}},
              {"name": "amount", "type": "decimal"}]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "open-slice", "name": "Open", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "open-ledger", "eventIds": ["ledger-opened"],
             "scenarios": [{"id": "open", "name": "Open", "kind": "stateChange", "given": [],
               "when": {"commandId": "open-ledger", "data": {}}, "then": {"events": [{"eventId": "ledger-opened"}]}}]},
            {"id": "record-slice", "name": "Record", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
             "screenId": "scr", "commandId": "record-amount", "eventIds": ["amount-recorded", "amount-flagged"],
             "outcomes": [
               {"eventIds": ["amount-recorded", "amount-flagged"], "when": [{"all": [{"command": "amount", "greaterThan": 1000}]}],
                "set": {"amount-flagged": {"fee": 2.5}}},
               {"eventIds": ["amount-recorded"]}
             ],
             "scenarios": [
               {"id": "small-after-small", "name": "A small amount after a small one", "kind": "stateChange",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}},
                          {"eventId": "amount-recorded", "data": {"ledgerId": "l1", "amount": 99.99, "rate": 1.5, "hours": 1}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 56.7, "rate": 1.5, "hours": 1}},
                "then": {"events": [{"eventId": "amount-recorded", "data": {"amount": 56.7}}]}},
               {"id": "zero", "name": "Refused at zero (decimal command equals a literal)", "kind": "error",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 0, "rate": 1.5, "hours": 1}},
                "then": {"error": {"message": "an amount of zero"}}},
               {"id": "after-large", "name": "Refused after a large amount (decimal state > literal)", "kind": "error",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}},
                          {"eventId": "amount-recorded", "data": {"ledgerId": "l1", "amount": 100.01, "rate": 1.5, "hours": 1}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 5, "rate": 1.5, "hours": 1}},
                "then": {"error": {"message": "ledger closed by a large amount"}}},
               {"id": "same-twice", "name": "Refused twice running (command equals decimal state)", "kind": "error",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}},
                          {"eventId": "amount-recorded", "data": {"ledgerId": "l1", "amount": 56.7, "rate": 1.5, "hours": 1}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 56.7, "rate": 1.5, "hours": 1}},
                "then": {"error": {"message": "the same amount twice"}}},
               {"id": "below-rate", "name": "Refused below the previous rate (command < decimal state)", "kind": "error",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}},
                          {"eventId": "amount-recorded", "data": {"ledgerId": "l1", "amount": 50, "rate": 30.06, "hours": 1}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 30.05, "rate": 1.5, "hours": 1}},
                "then": {"error": {"message": "amount above the previous one's rate"}}},
               {"id": "flagged", "name": "A large first amount is flagged with a decimal fee", "kind": "stateChange",
                "given": [{"eventId": "ledger-opened", "data": {"ledgerId": "l1"}}],
                "when": {"commandId": "record-amount", "data": {"ledgerId": "l1", "amount": 1000.01, "rate": 1.5, "hours": 1}},
                "then": {"events": [{"eventId": "amount-recorded"}, {"eventId": "amount-flagged", "data": {"fee": 2.5}}]}}
             ]}
          ]
        }
        """;

    private static List<GeneratedFile> Generate()
    {
        var domains = DocumentMapper.Map(DocumentLoader.Parse(LedgerJson)).Domains;
        return [.. domains.SelectMany(CSharpGenerator.Generate),
            .. domains.SelectMany(d => d.ReadModels.Select(rm => ReadModelQueryGenerator.Generate(d, rm)))];
    }

    private static DotnetCqrs.Codegen.Model.Field F(string name, string type) =>
        new(name, type, null, null, null, null, null, null, null, null);

    [Fact]
    public void Decimal_folds_to_decimal_and_the_other_numbers_to_number()
    {
        Assert.Equal("decimal", FieldTypeFolding.Fold(F("amount", "decimal")));
        Assert.Null(FieldTypeFolding.Note("event x", F("amount", "decimal")));
        foreach (var type in new[] { "double", "integer", "long" })
            Assert.Equal("number", FieldTypeFolding.Fold(F("n", type)));
        Assert.Equal("decimal", GenerationSupport.CSharpType("decimal"));
        Assert.Equal("NUMERIC", GenerationSupport.ColumnType("decimal"));
    }

    [Fact]
    public void The_ledger_maps_without_errors()
    {
        var result = DocumentMapper.Map(DocumentLoader.Parse(LedgerJson));
        Assert.Empty(result.Report.Errors);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_decimal_payload_multiplies_exactly_so_a_half_penny_rounds_up()
    {
        // 30.06 x 8.25 = 247.995 exactly; half away from zero gives 248.00. As doubles the product
        // is 247.99499999999998, which rounds to 247.99.
        const string program = """"
            using System.Globalization;
            using System.Reflection;
            using System.Text.Json;
            using Generated.Ledger;

            // The payload type is private to the decider; multiply in whatever type it was generated with.
            var type = typeof(LedgerDecider).GetNestedType("AmountRecordedPayload", BindingFlags.NonPublic)!;
            var payload = JsonSerializer.Deserialize("""{"ledgerId":"l1","amount":0,"rate":30.06,"hours":8.25}""", type,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var rate = type.GetProperty("Rate")!.GetValue(payload)!;
            var hours = type.GetProperty("Hours")!.GetValue(payload)!;
            var pay = rate is decimal r
                ? Math.Round(r * (decimal)hours, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)
                : Math.Round((double)rate * (double)hours, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            Console.WriteLine($"pay {pay} as {rate.GetType().Name}");
            return 0;
            """";

        var (success, output) = await InMemoryProgram.RunAsync(Generate(), program);

        Assert.True(success, output);
        Assert.Contains("pay 248.00 as Decimal", output);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_summed_decimal_column_is_returned_exactly()
    {
        // 412.35 + 96.10 + 56.70 = 565.15. Summed in a float column it reads 565.1500000000001.
        const string program = """"
            using System.Reflection;
            using System.Text.Json;
            using DotnetCqrs.EventStore;
            using DotnetCqrs.Host;
            using DotnetCqrs.ReadModels;
            using Generated.Ledger;
            using Microsoft.AspNetCore.Http;

            var readModel = await SqliteReadModelStore.OpenAsync(":memory:");
            var projection = new LedgersProjection(readModel);
            await projection.InitAsync();
            var position = 0L;
            Event On(string type, string data) => new(++position, $"e{position}", "ledger", "l1", position, type, data, "{}", "2026-10-01T00:00:00Z");
            await projection.ApplyAsync(On("LedgerOpened", """{"ledgerId":"l1"}"""), default);
            foreach (var amount in new[] { "412.35", "96.10", "56.70" })
                await projection.ApplyAsync(On("AmountRecorded", $$"""{"ledgerId":"l1","amount":{{amount}},"rate":1,"hours":1}"""), default);

            var query = typeof(LedgersQueryRoute).GetMethod("QueryAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var http = new DefaultHttpContext();
            var callArgs = query.GetParameters().Select(p =>
                p.ParameterType == typeof(HttpRequest) ? http.Request
                : p.ParameterType == typeof(IReadModelStore) ? readModel
                : p.ParameterType == typeof(CancellationToken) ? (object?)CancellationToken.None
                : null).ToArray();
            var outcome = await (Task<LiveView.Outcome>)query.Invoke(null, callArgs)!;
            Console.WriteLine("rows " + JsonSerializer.Serialize(outcome.Rows));
            return 0;
            """";

        var (success, output) = await InMemoryProgram.RunAsync(Generate(), program);

        Assert.True(success, output);
        Assert.Contains("\"total\":565.15", output);
        Assert.Contains("\"amount\":56.7", output);
        Assert.DoesNotContain("565.1500000000001", output);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Refusals_and_outcomes_compare_and_set_decimal_fields()
    {
        var doc = DocumentLoader.Parse(LedgerJson);

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }
}
