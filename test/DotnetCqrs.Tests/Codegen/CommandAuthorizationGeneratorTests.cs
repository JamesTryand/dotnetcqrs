using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves <see cref="CommandAuthorizationGenerator"/>'s output is REAL, working C# --
/// not just text that looks right. The generated file is compiled against the real
/// library together with a small hand-written program that calls
/// <c>Generated.CommandAuthorization.AuthorizeAsync</c> directly against a real (in-memory)
/// SQLite <c>IReadModelStore</c>, and the program is run (<see cref="InMemoryProgram"/>: in
/// memory and in-process, where a scratch <c>dotnet build</c>/<c>dotnet run</c> took minutes)
/// -- one case per declared kind (requiredRole, fieldGatedRole, requiredOwnership, scope with
/// a bypass role and without) plus the "no declared policy" default. A console program
/// rather than a web host, since this generator's output has no HTTP surface of its own --
/// see <c>CqrsGatewayEndpointsTests</c>/<c>CqrsGatewayAuthTests</c> for the separate proof
/// that <c>MapCqrsGateway</c>'s new <c>authorize</c> hook actually calls into code shaped
/// like this.
/// </summary>
// Compiles generated code in memory (InProcessHarness): seconds each, but Roslyn is the bulk of
// the everyday set. The tightest loop skips it: --filter "Category!=Slow&Category!=Compiles".
[Trait("Category", "Compiles")]
public class CommandAuthorizationGeneratorTests
{
    // Same shape as DocumentMapperTests' own CommandAuthorizationDocumentJson fixture:
    // an ownership-only update, a supervisor-bypass-else-region-scoped flag, a supervisor-only
    // setup command, and an Administrator-field-gated onboarding command.
    private const string Json = """
        {
          "eventModelingSchemaVersion": "2.5.0", "id": "command-auth-generator-test", "name": "Command Authorization Generator Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order"},
            "order-updated": {"name": "Order Updated", "swimlaneId": "s", "aggregate": "Order"},
            "order-flagged": {"name": "Order Flagged", "swimlaneId": "s", "aggregate": "Order"},
            "member-onboarded": {"name": "Member Onboarded", "swimlaneId": "s", "aggregate": "Member"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order", "requiredRole": "supervisor"},
            "update-order": {"name": "Update Order", "aggregate": "Order",
              "requiredOwnership": {"via": {"readModelId": "orders", "keyField": "orderId", "ownerField": "ownerId"}}},
            "flag-order": {"name": "Flag Order", "aggregate": "Order",
              "scope": {
                "bypassRoles": ["supervisor"],
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-supervisors", "matchField": "regionId", "actorField": "memberId"}
              }},
            "onboard-member": {"name": "Onboard Member", "aggregate": "Member",
              "fieldGatedRole": {"field": "role", "value": "supervisor", "requiredRole": "administrator"}}
          },
          "readModels": {
            "orders": {"name": "Orders", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "ownerId", "type": "string"}, {"name": "regionId", "type": "string"}]},
            "region-supervisors": {"name": "Region Supervisors", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "memberId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "place-order-slice", "name": "Place Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
            {"id": "update-order-slice", "name": "Update Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "update-order", "eventIds": ["order-updated"], "scenarios": []},
            {"id": "flag-order-slice", "name": "Flag Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "flag-order", "eventIds": ["order-flagged"], "scenarios": []},
            {"id": "onboard-member-slice", "name": "Onboard Member", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "onboard-member", "eventIds": ["member-onboarded"], "scenarios": []}
          ]
        }
        """;

    // Hand-written console harness (not generated) -- seeds a real in-memory SQLite
    // IReadModelStore directly via SQL (matching the real "orders"/"regionSupervisors"
    // table and snake_case column names CommandAuthorizationGenerator's own SQL uses),
    // then calls Generated.CommandAuthorization.AuthorizeAsync for one case per
    // declared kind plus the "no declared policy" default, printing PASS/FAIL lines and
    // exiting non-zero on any failure -- same self-reporting convention this project's
    // own --verify mode already uses.
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

        await ExecAsync("CREATE TABLE orders (order_id TEXT, owner_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO orders (order_id, owner_id, region_id) VALUES ('o1', 'member-1', 'north')");
        await ExecAsync("CREATE TABLE regionSupervisors (member_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO regionSupervisors (member_id, region_id) VALUES ('lead-1', 'north')");

        ClaimsPrincipal Actor(string role, string subjectId) =>
            new(new ClaimsIdentity([new Claim("role", role), new Claim("subject", subjectId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
        string ResolveSubjectId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "subject")?.Value ?? "";

        var emptyPayload = JsonDocument.Parse("{}").RootElement;
        var supervisorRolePayload = JsonDocument.Parse("""{"role":"supervisor"}""").RootElement;
        var memberRolePayload = JsonDocument.Parse("""{"role":"member"}""").RootElement;

        var failures = 0;
        async Task CheckAsync(string name, bool expected, Task<bool> actualTask)
        {
            var actual = await actualTask;
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        await CheckAsync("requiredRole: supervisor can PlaceOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("supervisor", "s1"), "order", "PlaceOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("requiredRole: member cannot PlaceOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("member", "s1"), "order", "PlaceOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));

        await CheckAsync("fieldGatedRole: administrator can OnboardMember as supervisor", true,
            CommandAuthorization.AuthorizeAsync(Actor("administrator", "a1"), "member", "OnboardMember", "s2", supervisorRolePayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("fieldGatedRole: supervisor cannot OnboardMember as supervisor", false,
            CommandAuthorization.AuthorizeAsync(Actor("supervisor", "m1"), "member", "OnboardMember", "s2", supervisorRolePayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("fieldGatedRole: a member CAN OnboardMember as a plain member (condition doesn't apply)", true,
            CommandAuthorization.AuthorizeAsync(Actor("member", "s1"), "member", "OnboardMember", "s2", memberRolePayload, store, ResolveRole, ResolveSubjectId));

        await CheckAsync("requiredOwnership: owner can UpdateOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("member", "member-1"), "order", "UpdateOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("requiredOwnership: non-owner cannot UpdateOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("member", "someone-else"), "order", "UpdateOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));

        await CheckAsync("scope: supervisor bypasses FlagOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("supervisor", "m1"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("scope: region supervisor can FlagOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("member", "lead-1"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));
        await CheckAsync("scope: a member outside the region cannot FlagOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("member", "random"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));

        await CheckAsync("no declared policy: always authorized", true,
            CommandAuthorization.AuthorizeAsync(Actor("member", "s1"), "order", "SomeOtherCommand", "o1", emptyPayload, store, ResolveRole, ResolveSubjectId));

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
        """";

    [Fact(Timeout = 120000)]
    public async Task Generated_CommandAuthorization_evaluates_every_declared_kind_correctly_over_a_real_store()
    {
        var doc = DocumentLoader.Parse(Json);
        var mapped = DocumentMapper.Map(doc);
        Assert.Equal(2, mapped.Domains.Count);

        var files = new List<GeneratedFile> { CommandAuthorizationGenerator.Generate(mapped.Domains) };

        // The member is compared through the field the document names (4.0.0), never an assumed column,
        // and the resolver is named for what it returns: the caller's subject id.
        Assert.Contains("\"member_id\"", files[0].Source);
        Assert.Contains("= @actor", files[0].Source);
        Assert.Contains("resolveOwnSubjectId", files[0].Source);
        // Every id column the generated SQL touches is one the document names: no column is assumed by convention.
        var columns = System.Text.RegularExpressions.Regex.Matches(files[0].Source, @"\b[a-z_]+_id\b").Select(m => m.Value).ToHashSet();
        Assert.Subset(new HashSet<string> { "order_id", "owner_id", "region_id", "member_id" }, columns);

        var (success, runOutput) = await InMemoryProgram.RunAsync(files, ProgramCs);
        Assert.True(success, $"generated CommandAuthorization.cs did not compile, or the program reported failures:{Environment.NewLine}{runOutput}");
        Assert.Contains("ALL PASS", runOutput);
        Assert.DoesNotContain("FAIL ", runOutput);
    }
}
