using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A command's authorization declarations combine: every one it declares must hold. Before this, the generated
/// evaluator returned as soon as it found <c>requiredRole</c>, so a <c>requiredOwnership</c>, <c>scope</c> or
/// <c>fieldGatedRole</c> on the same command was never checked (timesheets' paradigm audit, S2). Each case below is a
/// caller who has the role but fails the other declaration, and so must be refused; and one who meets both.
/// Compiled and run against a real in-memory store, as <see cref="CommandAuthorizationGeneratorTests"/> does.
/// </summary>
[Trait("Category", "Compiles")]
public class CommandAuthorizationCombinedTests
{
    private const string Json = """
        {
          "eventModelingSchemaVersion": "2.5.0", "id": "command-auth-combined-test", "name": "Command Authorization Combined Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order"},
            "order-closed": {"name": "Order Closed", "swimlaneId": "s", "aggregate": "Order"},
            "order-assigned": {"name": "Order Assigned", "swimlaneId": "s", "aggregate": "Order"},
            "staff-promoted": {"name": "Staff Promoted", "swimlaneId": "s", "aggregate": "Staff"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order"},
            "close-order": {"name": "Close Order", "aggregate": "Order", "requiredRole": "staff",
              "requiredOwnership": {"via": {"readModelId": "orders", "keyField": "orderId", "ownerField": "ownerId"}}},
            "assign-order": {"name": "Assign Order", "aggregate": "Order", "requiredRole": ["pm", "manager"],
              "scope": {
                "bypassRoles": ["manager"],
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-managers", "matchField": "regionId"}
              }},
            "promote-staff": {"name": "Promote Staff", "aggregate": "Staff", "requiredRole": ["manager", "administrator"],
              "fieldGatedRole": {"field": "role", "value": "manager", "requiredRole": "administrator"}}
          },
          "readModels": {
            "orders": {"name": "Orders", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "ownerId", "type": "string"}, {"name": "regionId", "type": "string"}]},
            "region-managers": {"name": "Region Managers", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "staffId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "place-order-slice", "name": "Place Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
            {"id": "close-order-slice", "name": "Close Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "close-order", "eventIds": ["order-closed"], "scenarios": []},
            {"id": "assign-order-slice", "name": "Assign Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "assign-order", "eventIds": ["order-assigned"], "scenarios": []},
            {"id": "promote-staff-slice", "name": "Promote Staff", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "promote-staff", "eventIds": ["staff-promoted"], "scenarios": []}
          ]
        }
        """;

    private const string ProgramCs = """"
        using System.Security.Claims;
        using System.Text.Json;
        using DotnetCqrs.ReadModels;
        using Generated;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");

        async Task ExecAsync(string sql)
        {
            await using var cmd = store.Connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        // Order o1 belongs to staff-1, in the north region; pm-1 manages the north region, pm-2 manages none.
        await ExecAsync("CREATE TABLE orders (order_id TEXT, owner_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO orders (order_id, owner_id, region_id) VALUES ('o1', 'staff-1', 'north')");
        await ExecAsync("CREATE TABLE regionManagers (staff_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO regionManagers (staff_id, region_id) VALUES ('pm-1', 'north')");

        ClaimsPrincipal Actor(string role, string staffId) =>
            new(new ClaimsIdentity([new Claim("role", role), new Claim("staffId", staffId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
        string ResolveStaffId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "staffId")?.Value ?? "";

        var empty = JsonDocument.Parse("{}").RootElement;
        var toManager = JsonDocument.Parse("""{"role":"manager"}""").RootElement;
        var toStaff = JsonDocument.Parse("""{"role":"staff"}""").RootElement;

        var failures = 0;
        async Task CheckAsync(string name, bool expected, string role, string staffId, string aggregate, string command, JsonElement payload)
        {
            var actual = await CommandAuthorization.AuthorizeAsync(Actor(role, staffId), aggregate, command, "o1", payload, store, ResolveRole, ResolveStaffId);
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        // requiredRole + requiredOwnership
        await CheckAsync("role and owner: the owning staff member closes it", true, "staff", "staff-1", "order", "CloseOrder", empty);
        await CheckAsync("role, not owner: another staff member is refused", false, "staff", "staff-2", "order", "CloseOrder", empty);
        await CheckAsync("owner, not role: the owner as a pm is refused", false, "pm", "staff-1", "order", "CloseOrder", empty);

        // requiredRole + scope (bypass manager)
        await CheckAsync("role and scope: the region's pm assigns it", true, "pm", "pm-1", "order", "AssignOrder", empty);
        await CheckAsync("role and bypass: a manager assigns it", true, "manager", "m-1", "order", "AssignOrder", empty);
        await CheckAsync("role, not scope: another region's pm is refused", false, "pm", "pm-2", "order", "AssignOrder", empty);
        await CheckAsync("scope, not role: the region's member as staff is refused", false, "staff", "pm-1", "order", "AssignOrder", empty);

        // requiredRole + fieldGatedRole
        await CheckAsync("role, gate applies, gated role: an administrator promotes to manager", true, "administrator", "a-1", "staff", "PromoteStaff", toManager);
        await CheckAsync("role, gate applies, not gated role: a manager promoting to manager is refused", false, "manager", "m-1", "staff", "PromoteStaff", toManager);
        await CheckAsync("role, gate doesn't apply: a manager promotes to staff", true, "manager", "m-1", "staff", "PromoteStaff", toStaff);
        await CheckAsync("gate doesn't apply, not role: staff is refused", false, "staff", "s-1", "staff", "PromoteStaff", toStaff);

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
        """";

    [Fact(Timeout = 120000)]
    public async Task Every_declared_requirement_must_hold_not_just_the_role()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(Json));

        var (success, runOutput) = await InMemoryProgram.RunAsync([CommandAuthorizationGenerator.Generate(mapped.Domains)], ProgramCs);

        Assert.True(success, $"generated CommandAuthorization.cs did not compile, or the program reported failures:{Environment.NewLine}{runOutput}");
        Assert.Contains("ALL PASS", runOutput);
    }
}
