using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves <see cref="ReadModelQueryGenerator"/>'s output is a REAL, working HTTP query
/// route -- not just text that looks right. Same discipline as
/// <c>HostGenerationTests</c>' real dotnet-run/HTTP round trip for
/// <c>MapCqrsGateway()</c>, but against a small standalone scratch web host built here
/// rather than <c>HostProjectGenerator</c>/<c>order-fulfillment.json</c> — wiring a
/// generated query route into a real generated-or-hand-written host is Stage 3a's job,
/// out of scope for this generator's own proof.
/// </summary>
public class ReadModelQueryGeneratorTests : IDisposable
{
    private readonly string _scratchDir;

    public ReadModelQueryGeneratorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-query-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratchDir)) return;

        // Same rationale as HostGenerationTests.Dispose: a live host process was just
        // killed, and Windows can hold its output DLLs locked briefly afterward.
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

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Same shape as DocumentMapperTests/ScenarioVerifierTests' own dateRange fixture
    // (project/timesheets's real time-entries read model) -- no stateView scenarios
    // needed here, since this test drives the generated route over real HTTP rather
    // than through ScenarioVerifier's harness.
    private const string Json = """
        {
          "eventModelingSchemaVersion": "2.4.0", "id": "query-route-test", "name": "Query Route Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "time-entry-logged": {"name": "Time Entry Logged", "swimlaneId": "s", "aggregate": "TimeEntry",
              "fields": [
                {"name": "entryId", "type": "string", "idAttribute": true},
                {"name": "taskDate", "type": "date"},
                {"name": "hours", "type": "double"}
              ]}
          },
          "commands": {
            "log-time-entry": {"name": "Log Time Entry", "aggregate": "TimeEntry"}
          },
          "readModels": {
            "time-entries": {
              "name": "Time Entries",
              "builtFromEventIds": ["time-entry-logged"],
              "fields": [
                {"name": "entryId", "type": "string", "idAttribute": true},
                {"name": "taskDate", "type": "date"},
                {"name": "hours", "type": "double"}
              ],
              "filters": [
                {"param": "dateRange", "field": "taskDate", "kind": "dateRange", "presets": ["last7Days", "lastCalendarMonth", "custom"]}
              ]
            }
          },
          "screens": {"scr1": {"name": "Log Screen"}},
          "slices": [
            {
              "id": "log-time-entry-slice", "name": "Log Time Entry", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "log-time-entry", "eventIds": ["time-entry-logged"],
              "scenarios": []
            }
          ]
        }
        """;

    // Hand-written, not generated -- this test's own scratch web host, seeding rows by
    // calling the generated projection's ApplyAsync directly (the same technique
    // HarnessProgram.txt's own RunViewScenarioAsync uses), then mapping the generated
    // query route and running for real.
    private const string ProgramCs = """
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.TimeEntry;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new TimeEntriesProjection(store);
        await projection.InitAsync();

        async Task SeedAsync(string entryId, string taskDate, double hours, long position)
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new { entryId, taskDate, hours });
            var ev = new Event(position, $"seed-{position}", "timeEntry", entryId, position, "TimeEntryLogged", data, "{}", "1970-01-01T00:00:00.000Z");
            await projection.ApplyAsync(ev, CancellationToken.None);
        }

        await SeedAsync("e1", "2026-08-15", 4, 1);
        await SeedAsync("e2", "2026-08-20", 3, 2);
        await SeedAsync("e3", "2026-09-01", 2, 3);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        var app = builder.Build();
        app.MapTimeEntriesRoute();
        await app.RunAsync();
        """;

    [Fact(Timeout = 300000)]
    public async Task Generated_query_route_filters_by_dateRange_and_by_a_plain_field_over_real_http()
    {
        var doc = DocumentLoader.Parse(Json);
        var mapped = DocumentMapper.Map(doc);
        var domain = Assert.Single(mapped.Domains);
        var readModel = Assert.Single(domain.ReadModels);
        // Pinned so the hand-written ProgramCs above (which names these literally) is
        // known to line up with what the generator actually produced.
        Assert.Equal("timeEntry", domain.Aggregate);
        Assert.Equal("timeEntries", readModel.Collection);

        var files = new List<GeneratedFile>(CSharpGenerator.Generate(domain))
        {
            ReadModelQueryGenerator.Generate(domain, readModel),
        };

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj")}" />
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs.Codegen", "DotnetCqrs.Codegen.csproj")}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Scratch.csproj"), csproj);
        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, file.Name), file.Source);
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Program.cs"), ProgramCs);

        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        Assert.True(buildExit == 0, $"generated query route project did not compile:\n{buildOutput}");

        var port = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_scratchDir);
        psi.ArgumentList.Add("--no-build");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            async Task<JsonElement> PollAsync(string path)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(500);
                    try { return await client.GetFromJsonAsync<JsonElement>(path); }
                    catch (HttpRequestException) { /* not listening yet -- retry */ }
                }
                throw new TimeoutException($"generated host never answered {path}");
            }

            var dateRangeQuery = "/api/query/timeEntries?dateRange=" +
                Uri.EscapeDataString("""{"kind":"custom","from":"2026-08-01","to":"2026-08-31"}""");
            var byDateRange = await PollAsync(dateRangeQuery);
            Assert.False(process.HasExited, "generated host process exited early");
            Assert.Equal(2, byDateRange.GetArrayLength());
            var entryIds = byDateRange.EnumerateArray().Select(r => r.GetProperty("entry_id").GetString()).OrderBy(x => x).ToList();
            Assert.Equal(["e1", "e2"], entryIds);

            var byPlainField = await client.GetFromJsonAsync<JsonElement>("/api/query/timeEntries?entryId=e1");
            Assert.Equal(1, byPlainField.GetArrayLength());
            Assert.Equal("e1", byPlainField[0].GetProperty("entry_id").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    // Schema 2.7.0 readModel.requiredRole. A minimal single-read-model document rather
    // than reusing the dateRange fixture above -- keeps the two concerns (filters,
    // requiredRole) each provable in isolation.
    private const string RoleGateJson = """
        {
          "eventModelingSchemaVersion": "2.7.0", "id": "role-gate-test", "name": "Role Gate Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "widget-made": {"name": "Widget Made", "swimlaneId": "s", "aggregate": "Widget",
              "fields": [
                {"name": "widgetId", "type": "string", "idAttribute": true},
                {"name": "name", "type": "string"}
              ]}
          },
          "commands": {
            "make-widget": {"name": "Make Widget", "aggregate": "Widget"}
          },
          "readModels": {
            "widgets": {
              "name": "Widgets",
              "builtFromEventIds": ["widget-made"],
              "fields": [
                {"name": "widgetId", "type": "string", "idAttribute": true},
                {"name": "name", "type": "string"}
              ],
              "requiredRole": ["manager", "administrator"]
            }
          },
          "screens": {"scr1": {"name": "Make Screen"}},
          "slices": [
            {
              "id": "make-widget-slice", "name": "Make Widget", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "make-widget", "eventIds": ["widget-made"],
              "scenarios": []
            }
          ]
        }
        """;

    // Reads an X-Test-Role header into a ClaimsPrincipal claim -- a stand-in for a real
    // auth scheme, same spirit as HostGenerationTests' own unauthenticated-by-default
    // host: this test isn't proving an auth SCHEME, only that the generated route
    // consults whatever resolveOwnRole delegate it's given.
    private const string RoleGateProgramCsTemplate = """
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Widget;
        using System.Security.Claims;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new WidgetsProjection(store);
        await projection.InitAsync();

        var seedEvent = new Event(1, "seed-1", "widget", "w1", 1, "WidgetMade",
            System.Text.Json.JsonSerializer.Serialize(new { widgetId = "w1", name = "Widget One" }),
            "{}", "1970-01-01T00:00:00.000Z");
        await projection.ApplyAsync(seedEvent, CancellationToken.None);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var role = context.Request.Headers["X-Test-Role"].ToString();
            var claims = role.Length > 0 ? new[] { new Claim("role", role) } : Array.Empty<Claim>();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
            await next();
        });

        string ResolveOwnRole(ClaimsPrincipal user) => user.FindFirst("role")?.Value ?? "";

        {{MAP_CALL}}
        await app.RunAsync();
        """;

    private static (GeneratedFile[] Files, Domain Domain, ReadModel ReadModel) GenerateRoleGateRoute()
    {
        var doc = DocumentLoader.Parse(RoleGateJson);
        var mapped = DocumentMapper.Map(doc);
        var domain = Assert.Single(mapped.Domains);
        var readModel = Assert.Single(domain.ReadModels);
        Assert.Equal("widget", domain.Aggregate);
        Assert.Equal("widgets", readModel.Collection);
        Assert.Equal(["manager", "administrator"], readModel.RequiredRole);

        var files = new List<GeneratedFile>(CSharpGenerator.Generate(domain))
        {
            ReadModelQueryGenerator.Generate(domain, readModel),
        };
        return (files.ToArray(), domain, readModel);
    }

    private async Task<int> StartHostAndBuildAsync(GeneratedFile[] files, string programCs, string scratchDir)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj")}" />
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs.Codegen", "DotnetCqrs.Codegen.csproj")}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(scratchDir, "Scratch.csproj"), csproj);
        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(scratchDir, file.Name), file.Source);
        await File.WriteAllTextAsync(Path.Combine(scratchDir, "Program.cs"), programCs);

        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", scratchDir, "-v", "quiet"]);
        Assert.True(buildExit == 0, $"generated query route project did not compile:\n{buildOutput}");
        return FreeTcpPort();
    }

    [Fact(Timeout = 300000)]
    public async Task Generated_query_route_enforces_requiredRole_when_resolveOwnRole_is_wired()
    {
        var (files, _, _) = GenerateRoleGateRoute();
        var programCs = RoleGateProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapWidgetsRoute(resolveOwnRole: ResolveOwnRole);");
        var port = await StartHostAndBuildAsync(files, programCs, _scratchDir);

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_scratchDir);
        psi.ArgumentList.Add("--no-build");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            async Task<HttpResponseMessage> PollAsync(string? role)
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(500);
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/query/widgets");
                        if (role is not null) req.Headers.Add("X-Test-Role", role);
                        return await client.SendAsync(req);
                    }
                    catch (HttpRequestException) { /* not listening yet -- retry */ }
                }
                throw new TimeoutException("generated host never answered /api/query/widgets");
            }

            using var staffResponse = await PollAsync("staff");
            Assert.False(process.HasExited, "generated host process exited early");
            Assert.Equal(HttpStatusCode.Forbidden, staffResponse.StatusCode);

            using var noRoleResponse = await client.GetAsync("/api/query/widgets");
            Assert.Equal(HttpStatusCode.Forbidden, noRoleResponse.StatusCode);

            using var managerResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/query/widgets") { Headers = { { "X-Test-Role", "manager" } } });
            Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);
            var rows = await managerResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, rows.GetArrayLength());
            Assert.Equal("w1", rows[0].GetProperty("widget_id").GetString());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    [Fact(Timeout = 300000)]
    public async Task Generated_query_route_stays_open_when_resolveOwnRole_is_not_wired()
    {
        // Same "generated code is a scaffold, wiring real auth is the operator's job"
        // posture command authorization already established: a read model declaring
        // requiredRole doesn't self-enforce it -- unset resolveOwnRole (this generator's
        // default, and HostProjectGenerator's own generated Program.cs) means the route
        // stays exactly as open as it was before this capability existed.
        var (files, _, _) = GenerateRoleGateRoute();
        var programCs = RoleGateProgramCsTemplate.Replace("{{MAP_CALL}}", "app.MapWidgetsRoute();");
        var port = await StartHostAndBuildAsync(files, programCs, _scratchDir);

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_scratchDir);
        psi.ArgumentList.Add("--no-build");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            async Task<HttpResponseMessage> PollAsync()
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(500);
                    try { return await client.GetAsync("/api/query/widgets"); }
                    catch (HttpRequestException) { /* not listening yet -- retry */ }
                }
                throw new TimeoutException("generated host never answered /api/query/widgets");
            }

            using var response = await PollAsync();
            Assert.False(process.HasExited, "generated host process exited early");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, rows.GetArrayLength());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }
}
