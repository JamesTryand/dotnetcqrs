using System.Diagnostics;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves <see cref="CommandAuthorizationGenerator"/>'s output is REAL, working C# --
/// not just text that looks right. Same discipline as <c>ReadModelQueryGeneratorTests</c>/
/// <c>HostGenerationTests</c>: write the generated file into a real scratch project,
/// <c>dotnet build</c> it, then <c>dotnet run</c> a small hand-written harness that calls
/// <c>Generated.CommandAuthorization.AuthorizeAsync</c> directly against a real (in-memory)
/// SQLite <c>IReadModelStore</c> -- one case per declared kind (requiredRole,
/// fieldGatedRole, requiredOwnership, scope with a bypass role and without) plus the
/// "no declared policy" default. A console harness rather than a web host, since this
/// generator's output has no HTTP surface of its own -- see
/// <c>CqrsGatewayEndpointsTests</c>/<c>CqrsGatewayAuthTests</c> for the separate proof
/// that <c>MapCqrsGateway</c>'s new <c>authorize</c> hook actually calls into code shaped
/// like this.
/// </summary>
public class CommandAuthorizationGeneratorTests : IDisposable
{
    private readonly string _scratchDir;

    public CommandAuthorizationGeneratorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-cmd-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratchDir)) return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(_scratchDir, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(500); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(500); }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }

    // Same shape as DocumentMapperTests' own CommandAuthorizationDocumentJson fixture:
    // an ownership-only update, a Manager-bypass-else-region-scoped flag, a Manager-only
    // setup command, and an Administrator-field-gated onboarding command.
    private const string Json = """
        {
          "eventModelingSchemaVersion": "2.5.0", "id": "command-auth-generator-test", "name": "Command Authorization Generator Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order"},
            "order-updated": {"name": "Order Updated", "swimlaneId": "s", "aggregate": "Order"},
            "order-flagged": {"name": "Order Flagged", "swimlaneId": "s", "aggregate": "Order"},
            "staff-onboarded": {"name": "Staff Onboarded", "swimlaneId": "s", "aggregate": "Staff"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order", "requiredRole": "manager"},
            "update-order": {"name": "Update Order", "aggregate": "Order",
              "requiredOwnership": {"via": {"readModelId": "orders", "keyField": "orderId", "ownerField": "ownerId"}}},
            "flag-order": {"name": "Flag Order", "aggregate": "Order",
              "scope": {
                "bypassRoles": ["manager"],
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-managers", "matchField": "regionId"}
              }},
            "onboard-staff": {"name": "Onboard Staff", "aggregate": "Staff",
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
            {"id": "update-order-slice", "name": "Update Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "update-order", "eventIds": ["order-updated"], "scenarios": []},
            {"id": "flag-order-slice", "name": "Flag Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "flag-order", "eventIds": ["order-flagged"], "scenarios": []},
            {"id": "onboard-staff-slice", "name": "Onboard Staff", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "onboard-staff", "eventIds": ["staff-onboarded"], "scenarios": []}
          ]
        }
        """;

    // Hand-written console harness (not generated) -- seeds a real in-memory SQLite
    // IReadModelStore directly via SQL (matching the real "orders"/"regionManagers"
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
        await ExecAsync("INSERT INTO orders (order_id, owner_id, region_id) VALUES ('o1', 'staff-1', 'north')");
        await ExecAsync("CREATE TABLE regionManagers (staff_id TEXT, region_id TEXT)");
        await ExecAsync("INSERT INTO regionManagers (staff_id, region_id) VALUES ('pm-1', 'north')");

        ClaimsPrincipal Actor(string role, string staffId) =>
            new(new ClaimsIdentity([new Claim("role", role), new Claim("staffId", staffId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
        string ResolveStaffId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "staffId")?.Value ?? "";

        var emptyPayload = JsonDocument.Parse("{}").RootElement;
        var managerRolePayload = JsonDocument.Parse("""{"role":"manager"}""").RootElement;
        var staffRolePayload = JsonDocument.Parse("""{"role":"staff"}""").RootElement;

        var failures = 0;
        async Task CheckAsync(string name, bool expected, Task<bool> actualTask)
        {
            var actual = await actualTask;
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        await CheckAsync("requiredRole: manager can PlaceOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("manager", "s1"), "order", "PlaceOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("requiredRole: staff cannot PlaceOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "s1"), "order", "PlaceOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));

        await CheckAsync("fieldGatedRole: administrator can OnboardStaff as manager", true,
            CommandAuthorization.AuthorizeAsync(Actor("administrator", "a1"), "staff", "OnboardStaff", "s2", managerRolePayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("fieldGatedRole: manager cannot OnboardStaff as manager", false,
            CommandAuthorization.AuthorizeAsync(Actor("manager", "m1"), "staff", "OnboardStaff", "s2", managerRolePayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("fieldGatedRole: staff CAN OnboardStaff as plain staff (condition doesn't apply)", true,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "s1"), "staff", "OnboardStaff", "s2", staffRolePayload, store, ResolveRole, ResolveStaffId));

        await CheckAsync("requiredOwnership: owner can UpdateOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "staff-1"), "order", "UpdateOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("requiredOwnership: non-owner cannot UpdateOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "someone-else"), "order", "UpdateOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));

        await CheckAsync("scope: manager bypasses FlagOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("manager", "m1"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("scope: region manager can FlagOrder", true,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "pm-1"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));
        await CheckAsync("scope: non-member staff cannot FlagOrder", false,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "random"), "order", "FlagOrder", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));

        await CheckAsync("no declared policy: always authorized", true,
            CommandAuthorization.AuthorizeAsync(Actor("staff", "s1"), "order", "SomeOtherCommand", "o1", emptyPayload, store, ResolveRole, ResolveStaffId));

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

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj")}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Scratch.csproj"), csproj);
        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, file.Name), file.Source);
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Program.cs"), ProgramCs);

        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        Assert.True(buildExit == 0, $"generated CommandAuthorization.cs did not compile:\n{buildOutput}");

        var (runExit, runOutput) = await RunAsync("dotnet", ["run", "--project", _scratchDir, "--no-build"]);
        Assert.True(runExit == 0, $"harness reported failures:\n{runOutput}");
        Assert.Contains("ALL PASS", runOutput);
        Assert.DoesNotContain("FAIL ", runOutput);
    }
}
