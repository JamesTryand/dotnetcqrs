using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 4.0.0's <c>scope.message</c>: the refusal's own message when the actor is not a member. The generated
/// evaluator refuses with it as a <c>CommandForbiddenException</c>, which the gateway answers 403 with, as it does the
/// decider's own refusals; a scope without one refuses with <c>false</c>, as before.
/// Compiled and run against a real in-memory store, as <see cref="CommandAuthorizationGeneratorTests"/> does.
/// </summary>
[Trait("Category", "Compiles")]
public class CommandScopeMessageTests
{
    private const string Json = """
        {
          "eventModelingSchemaVersion": "4.0.0", "id": "command-scope-message-test", "name": "Command Scope Message Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order"},
            "order-assigned": {"name": "Order Assigned", "swimlaneId": "s", "aggregate": "Order"},
            "order-flagged": {"name": "Order Flagged", "swimlaneId": "s", "aggregate": "Order"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order"},
            "assign-order": {"name": "Assign Order", "aggregate": "Order",
              "scope": {
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-leads", "matchField": "regionId", "actorField": "memberId"},
                "message": "only a lead of the order's region may assign it"
              }},
            "flag-order": {"name": "Flag Order", "aggregate": "Order",
              "scope": {
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-leads", "matchField": "regionId", "actorField": "memberId"}
              }}
          },
          "readModels": {
            "orders": {"name": "Orders", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}]},
            "region-leads": {"name": "Region Leads", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "memberId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "place-order-slice", "name": "Place Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
            {"id": "assign-order-slice", "name": "Assign Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "assign-order", "eventIds": ["order-assigned"], "scenarios": []},
            {"id": "flag-order-slice", "name": "Flag Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "flag-order", "eventIds": ["order-flagged"], "scenarios": []}
          ]
        }
        """;

    private const string ProgramCs = """"
        using System.Security.Claims;
        using System.Text.Json;
        using DotnetCqrs.Deciders;
        using DotnetCqrs.ReadModels;
        using Generated;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");

        async Task ExecAsync(string sql)
        {
            await using var cmd = store.Connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        // Order o1 is in the north region; lead-1 leads the north region, lead-2 leads none.
        await ExecAsync("CREATE TABLE orders (order_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO orders (order_id, region_id) VALUES ('o1', 'north')");
        await ExecAsync("CREATE TABLE regionLeads (member_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO regionLeads (member_id, region_id) VALUES ('lead-1', 'north')");

        ClaimsPrincipal Actor(string subjectId) => new(new ClaimsIdentity([new Claim("subject", subjectId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => "";
        string ResolveSubjectId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "subject")?.Value ?? "";
        var empty = JsonDocument.Parse("{}").RootElement;

        async Task<string> OutcomeAsync(string subjectId, string command)
        {
            try
            {
                return (await CommandAuthorization.AuthorizeAsync(Actor(subjectId), "order", command, "o1", empty, store, ResolveRole, ResolveSubjectId)).ToString();
            }
            catch (CommandForbiddenException ex)
            {
                return "forbidden: " + ex.Message;
            }
        }

        var failures = 0;
        async Task CheckAsync(string name, string expected, string subjectId, string command)
        {
            var actual = await OutcomeAsync(subjectId, command);
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        await CheckAsync("a lead of the region assigns it", "True", "lead-1", "AssignOrder");
        await CheckAsync("another lead is refused with the scope's message", "forbidden: only a lead of the order's region may assign it", "lead-2", "AssignOrder");
        await CheckAsync("a scope without a message refuses with false", "False", "lead-2", "FlagOrder");

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
        """";

    [Fact(Timeout = 120000)]
    public async Task A_scope_refuses_with_its_message()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(Json));

        var (success, runOutput) = await InMemoryProgram.RunAsync([CommandAuthorizationGenerator.Generate(mapped.Domains)], ProgramCs);

        Assert.True(success, $"generated CommandAuthorization.cs did not compile, or the program reported failures:{Environment.NewLine}{runOutput}");
        Assert.Contains("ALL PASS", runOutput);
    }
}
