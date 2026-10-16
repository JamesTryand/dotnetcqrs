using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A generated <c>count</c>, <c>sum</c> or <c>groupBy</c> rollup is the same however often its events are delivered,
/// and whenever its row is seeded. It added to the column on each delivery (apply and
/// checkpoint aren't atomic, so a redelivery added again), and an event that arrived before its row existed was lost.
/// Each contribution is now recorded once, by event, and the totals are worked out from them.
///
/// <para>Worked by hand: travel 412.35 is posted before the ledger is opened, so the opened ledger has 1 entry, 412.35; then food 96.10 (delivered twice), and
/// travel 56.70 posted then voided (the void delivered twice): 2 entries, 508.45, travel 1 / 412.35, food 1 / 96.10.
/// Before, the first was lost, and the doubles counted twice. Closed and opened again, the ledger starts afresh: food 10
/// gives 1 entry, 10.</para>
/// </summary>
[Trait("Category", "Compiles")]
public class RollupIdempotenceTests
{
    private const string Json = """
        {
          "eventModelingSchemaVersion": "3.14.0", "id": "rollup-test", "name": "Rollup Test",
          "swimlanes": [{"id": "s", "name": "S", "kind": "team"}],
          "events": {
            "ledger-opened": {"name": "Ledger Opened", "swimlaneId": "s", "aggregate": "Ledger", "fields": [{"name": "ledgerId", "type": "string", "idAttribute": true}]},
            "ledger-closed": {"name": "Ledger Closed", "swimlaneId": "s", "aggregate": "Ledger", "fields": [{"name": "ledgerId", "type": "string", "idAttribute": true}]},
            "entry-posted": {"name": "Entry Posted", "swimlaneId": "s", "aggregate": "Entry", "fields": [
              {"name": "entryId", "type": "string", "idAttribute": true}, {"name": "ledgerId", "type": "string"},
              {"name": "amount", "type": "decimal"}, {"name": "category", "type": "string"}]},
            "entry-voided": {"name": "Entry Voided", "swimlaneId": "s", "aggregate": "Entry", "fields": [
              {"name": "entryId", "type": "string", "idAttribute": true}, {"name": "ledgerId", "type": "string"},
              {"name": "amount", "type": "decimal"}, {"name": "category", "type": "string"}]}
          },
          "commands": {
            "open-ledger": {"name": "Open Ledger", "aggregate": "Ledger"},
            "close-ledger": {"name": "Close Ledger", "aggregate": "Ledger"},
            "post-entry": {"name": "Post Entry", "aggregate": "Entry"},
            "void-entry": {"name": "Void Entry", "aggregate": "Entry"}
          },
          "readModels": {
            "ledgers": {"name": "Ledgers", "builtFromEventIds": ["ledger-opened", "ledger-closed"], "removedByEventIds": ["ledger-closed"],
              "fields": [
                {"name": "ledgerId", "type": "string", "idAttribute": true},
                {"name": "entries", "type": "integer", "derivation": {"kind": "count", "incrementOnEventIds": ["entry-posted"], "decrementOnEventIds": ["entry-voided"]}},
                {"name": "total", "type": "decimal", "derivation": {"kind": "sum", "addOnEventIds": ["entry-posted"], "subtractOnEventIds": ["entry-voided"], "amountField": "amount"}},
                {"name": "byCategory", "type": "custom", "cardinality": "list", "derivation": {"kind": "groupBy", "groupByField": "category"},
                  "subfields": [
                    {"name": "category", "type": "string"},
                    {"name": "count", "type": "integer", "derivation": {"kind": "count", "incrementOnEventIds": ["entry-posted"], "decrementOnEventIds": ["entry-voided"]}},
                    {"name": "amount", "type": "double", "derivation": {"kind": "sum", "addOnEventIds": ["entry-posted"], "subtractOnEventIds": ["entry-voided"], "amountField": "amount"}}
                  ]}
              ]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "open-slice", "name": "Open", "pattern": "stateChange", "swimlaneId": "s", "status": "created", "screenId": "scr", "commandId": "open-ledger", "eventIds": ["ledger-opened"],
             "scenarios": [{"id": "open", "name": "Open", "kind": "stateChange", "given": [], "when": {"commandId": "open-ledger"}, "then": {"events": [{"eventId": "ledger-opened"}]}}]},
            {"id": "close-slice", "name": "Close", "pattern": "stateChange", "swimlaneId": "s", "status": "created", "screenId": "scr", "commandId": "close-ledger", "eventIds": ["ledger-closed"],
             "scenarios": [{"id": "close", "name": "Close", "kind": "stateChange", "given": [{"eventId": "ledger-opened"}], "when": {"commandId": "close-ledger"}, "then": {"events": [{"eventId": "ledger-closed"}]}}]},
            {"id": "post-slice", "name": "Post", "pattern": "stateChange", "swimlaneId": "s", "status": "created", "screenId": "scr", "commandId": "post-entry", "eventIds": ["entry-posted"],
             "scenarios": [{"id": "post", "name": "Post", "kind": "stateChange", "given": [], "when": {"commandId": "post-entry"}, "then": {"events": [{"eventId": "entry-posted"}]}}]},
            {"id": "void-slice", "name": "Void", "pattern": "stateChange", "swimlaneId": "s", "status": "created", "screenId": "scr", "commandId": "void-entry", "eventIds": ["entry-voided"],
             "scenarios": [{"id": "void", "name": "Void", "kind": "stateChange", "given": [{"eventId": "entry-posted"}], "when": {"commandId": "void-entry"}, "then": {"events": [{"eventId": "entry-voided"}]}}]}
          ]
        }
        """;

    private const string ProgramCs = """"
        using System.Globalization;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Ledger;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new LedgersProjection(store);
        await projection.InitAsync();
        Console.WriteLine("tables " + string.Join(",", projection.Tables));

        var position = 0L;
        Event Ledger(string type)
        {
            position++;
            return new(position, $"e{position}", "ledger", "l1", position, type, """{"ledgerId":"l1"}""", "{}", "2026-10-14T00:00:00Z");
        }
        Event Entry(string type, string entryId, string amount, string category)
        {
            position++;
            return new(position, $"e{position}", "entry", entryId, position, type,
                $$"""{"entryId":"{{entryId}}","ledgerId":"l1","amount":{{amount}},"category":"{{category}}"}""", "{}", "2026-10-14T00:00:00Z");
        }
        async Task ApplyAsync(Event ev) => await projection.ApplyAsync(ev, default);
        async Task ShowAsync(string label)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = "SELECT entries, total, by_category FROM ledgers WHERE ledger_id = 'l1'";
            await using var reader = await command.ExecuteReaderAsync();
            Console.WriteLine(!await reader.ReadAsync() ? $"{label} none"
                : $"{label} entries={reader.GetInt64(0)} total={reader.GetDecimal(1).ToString(CultureInfo.InvariantCulture)} byCategory={reader.GetString(2)}");
        }

        await ApplyAsync(Entry("EntryPosted", "a", "412.35", "travel"));
        await ApplyAsync(Ledger("LedgerOpened"));
        await ShowAsync("opened");
        var food = Entry("EntryPosted", "b", "96.10", "food");
        await ApplyAsync(food);
        await ApplyAsync(food);
        await ApplyAsync(Entry("EntryPosted", "c", "56.70", "travel"));
        var voided = Entry("EntryVoided", "c", "56.70", "travel");
        await ApplyAsync(voided);
        await ApplyAsync(voided);
        await ShowAsync("after");

        await ApplyAsync(Ledger("LedgerClosed"));
        await ApplyAsync(Ledger("LedgerOpened"));
        await ApplyAsync(Entry("EntryPosted", "d", "10", "food"));
        await ShowAsync("reopened");
        return 0;
        """";

    [Fact(Timeout = 120000)]
    public async Task Totals_count_each_event_once_whenever_it_arrives()
    {
        var domains = DocumentMapper.Map(DocumentLoader.Parse(Json)).Domains;
        var owner = domains.Single(d => d.ReadModels.Any(rm => rm.Collection == "ledgers"));
        var files = owner.ReadModels.Select(rm => ProjectionGenerator.Generate(owner, rm)).ToList();
        var program = ProgramCs.Replace("using Generated.Ledger;", $"using Generated.{GenerationSupport.ExportName(owner.Aggregate)};");

        var (success, output) = await InMemoryProgram.RunAsync(files, program);

        Assert.True(success, output);
        Assert.Contains("tables ledgers,ledgers_contributions", output);
        Assert.Contains("""opened entries=1 total=412.35 byCategory=[{"category":"travel","count":1,"amount":412.35}]""", output);
        Assert.Contains("""after entries=2 total=508.45 byCategory=[{"category":"travel","count":1,"amount":412.35},{"category":"food","count":1,"amount":96.1}]""", output);
        Assert.Contains("""reopened entries=1 total=10 byCategory=[{"category":"food","count":1,"amount":10}]""", output);
    }
}
