using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A command's authorization declarations combine: every one it declares must hold. Before this, the generated
/// evaluator returned as soon as it found <c>requiredRole</c>, so a <c>requiredOwnership</c>, <c>scope</c> or
/// <c>fieldGatedRole</c> on the same command was never checked. Each case below is a
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
            "member-promoted": {"name": "Member Promoted", "swimlaneId": "s", "aggregate": "Member"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order"},
            "close-order": {"name": "Close Order", "aggregate": "Order", "requiredRole": "member",
              "requiredOwnership": {"via": {"readModelId": "orders", "keyField": "orderId", "ownerField": "ownerId"}}},
            "assign-order": {"name": "Assign Order", "aggregate": "Order", "requiredRole": ["lead", "supervisor"],
              "scope": {
                "bypassRoles": ["supervisor"],
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-leads", "matchField": "regionId", "actorField": "memberId"}
              }},
            "promote-member": {"name": "Promote Member", "aggregate": "Member", "requiredRole": ["supervisor", "administrator"],
              "fieldGatedRole": {"field": "role", "value": "supervisor", "requiredRole": "administrator"}}
          },
          "readModels": {
            "orders": {"name": "Orders", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "ownerId", "type": "string"}, {"name": "regionId", "type": "string"}]},
            "region-leads": {"name": "Region Leads", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "memberId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "place-order-slice", "name": "Place Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
            {"id": "close-order-slice", "name": "Close Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "close-order", "eventIds": ["order-closed"], "scenarios": []},
            {"id": "assign-order-slice", "name": "Assign Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "assign-order", "eventIds": ["order-assigned"], "scenarios": []},
            {"id": "promote-member-slice", "name": "Promote Member", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "promote-member", "eventIds": ["member-promoted"], "scenarios": []}
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

        // Order o1 belongs to member-1, in the north region; lead-1 leads the north region, lead-2 leads none.
        await ExecAsync("CREATE TABLE orders (order_id TEXT, owner_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO orders (order_id, owner_id, region_id) VALUES ('o1', 'member-1', 'north')");
        await ExecAsync("CREATE TABLE regionLeads (member_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO regionLeads (member_id, region_id) VALUES ('lead-1', 'north')");

        ClaimsPrincipal Actor(string role, string subjectId) =>
            new(new ClaimsIdentity([new Claim("role", role), new Claim("subject", subjectId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
        string ResolveSubjectId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "subject")?.Value ?? "";

        var empty = JsonDocument.Parse("{}").RootElement;
        var toSupervisor = JsonDocument.Parse("""{"role":"supervisor"}""").RootElement;
        var toMember = JsonDocument.Parse("""{"role":"member"}""").RootElement;

        var failures = 0;
        async Task CheckAsync(string name, bool expected, string role, string subjectId, string aggregate, string command, JsonElement payload)
        {
            var actual = await CommandAuthorization.AuthorizeAsync(Actor(role, subjectId), aggregate, command, "o1", payload, store, ResolveRole, ResolveSubjectId);
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        // requiredRole + requiredOwnership
        await CheckAsync("role and owner: the owning member closes it", true, "member", "member-1", "order", "CloseOrder", empty);
        await CheckAsync("role, not owner: another member is refused", false, "member", "member-2", "order", "CloseOrder", empty);
        await CheckAsync("owner, not role: the owner as a lead is refused", false, "lead", "member-1", "order", "CloseOrder", empty);

        // requiredRole + scope (bypass supervisor)
        await CheckAsync("role and scope: the region's lead assigns it", true, "lead", "lead-1", "order", "AssignOrder", empty);
        await CheckAsync("role and bypass: a supervisor assigns it", true, "supervisor", "sup-1", "order", "AssignOrder", empty);
        await CheckAsync("role, not scope: another region's lead is refused", false, "lead", "lead-2", "order", "AssignOrder", empty);
        await CheckAsync("scope, not role: the region's lead as a member is refused", false, "member", "lead-1", "order", "AssignOrder", empty);

        // requiredRole + fieldGatedRole
        await CheckAsync("role, gate applies, gated role: an administrator promotes to supervisor", true, "administrator", "a-1", "member", "PromoteMember", toSupervisor);
        await CheckAsync("role, gate applies, not gated role: a supervisor promoting to supervisor is refused", false, "supervisor", "sup-1", "member", "PromoteMember", toSupervisor);
        await CheckAsync("role, gate doesn't apply: a supervisor promotes to member", true, "supervisor", "sup-1", "member", "PromoteMember", toMember);
        await CheckAsync("gate doesn't apply, not role: a member is refused", false, "member", "member-3", "member", "PromoteMember", toMember);

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
