using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Tests.Postgres;

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
public class ReadModelQueryGeneratorTests : IDisposable, IClassFixture<PostgresFixture>
{
    private readonly string _scratchDir;
    private readonly PostgresFixture _pg;

    public ReadModelQueryGeneratorTests(PostgresFixture pg)
    {
        _pg = pg;
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
                {"name": "hours", "type": "double"},
                {"name": "billable", "type": "boolean"}
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
                {"name": "hours", "type": "double"},
                {"name": "billable", "type": "boolean"}
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
    // query route and running for real. TEST_PG names a Postgres schema to use instead of
    // an in-memory SQLite database: the same generated code runs on either.
    private const string ProgramCs = """
        using DotnetCqrs.EventStore;
        using DotnetCqrs.Postgres;
        using DotnetCqrs.ReadModels;
        using Generated.TimeEntry;

        IReadModelStore store = Environment.GetEnvironmentVariable("TEST_PG") is { Length: > 0 } pg
            ? await PostgresReadModelStore.OpenAsync(pg)
            : await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new TimeEntriesProjection(store);
        await projection.InitAsync();

        async Task SeedAsync(string entryId, string taskDate, double hours, bool billable, long position)
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new { entryId, taskDate, hours, billable });
            var ev = new Event(position, $"seed-{position}", "timeEntry", entryId, position, "TimeEntryLogged", data, "{}", "1970-01-01T00:00:00.000Z");
            await projection.ApplyAsync(ev, CancellationToken.None);
        }

        await SeedAsync("e1", "2026-08-15", 1234567.89, true, 1);
        await SeedAsync("e2", "2026-08-20", 3, false, 2);
        await SeedAsync("e3", "2026-09-01", 2, true, 3);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        var app = builder.Build();
        app.MapTimeEntriesRoute();
        await app.RunAsync();
        """;

    [Fact(Timeout = 300000)]
    public Task Generated_query_route_filters_by_dateRange_and_by_a_plain_field_over_real_http()
        => RunPlainFilterRouteAsync(postgresConnectionString: null);

    // The same generated projection and route on Postgres: a bool column is written 0/1 into
    // an integer, a number keeps 8-byte precision, typed plain params compare without an
    // "integer = text" error, and concurrent requests don't collide on one connection.
    [SkippableFact(Timeout = 300000)]
    public async Task Generated_query_route_filters_by_dateRange_and_by_a_plain_field_on_postgres()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await RunPlainFilterRouteAsync(await _pg.NewSchemaAsync());
    }

    private async Task RunPlainFilterRouteAsync(string? postgresConnectionString)
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
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs.Postgres", "DotnetCqrs.Postgres.csproj")}" />
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
        psi.Environment["TEST_PG"] = postgresConnectionString ?? "";

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
            static List<string?> EntryIds(JsonElement rows) =>
                rows.EnumerateArray().Select(r => r.GetProperty("entry_id").GetString()).OrderBy(x => x).ToList();

            var dateRangeQuery = "/api/query/timeEntries?dateRange=" +
                Uri.EscapeDataString("""{"kind":"custom","from":"2026-08-01","to":"2026-08-31"}""");
            var byDateRange = await PollAsync(dateRangeQuery);
            Assert.False(process.HasExited, "generated host process exited early");
            Assert.Equal(["e1", "e2"], EntryIds(byDateRange));

            var byPlainField = await client.GetFromJsonAsync<JsonElement>("/api/query/timeEntries?entryId=e1");
            var e1 = Assert.Single(byPlainField.EnumerateArray());
            Assert.Equal("e1", e1.GetProperty("entry_id").GetString());
            // Identical on both databases: full double precision, and a bool as 1/0.
            Assert.Equal(1234567.89, e1.GetProperty("hours").GetDouble());
            Assert.Equal(1, e1.GetProperty("billable").GetInt32());

            // Typed plain params: parsed to the column's type before binding.
            Assert.Equal(["e1"], EntryIds(await client.GetFromJsonAsync<JsonElement>("/api/query/timeEntries?hours=1234567.89")));
            Assert.Equal(["e1", "e3"], EntryIds(await client.GetFromJsonAsync<JsonElement>("/api/query/timeEntries?billable=true")));
            Assert.Equal(["e2"], EntryIds(await client.GetFromJsonAsync<JsonElement>("/api/query/timeEntries?billable=0")));
            using (var notANumber = await client.GetAsync("/api/query/timeEntries?hours=lots"))
                Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
            using (var notABool = await client.GetAsync("/api/query/timeEntries?billable=maybe"))
                Assert.Equal(HttpStatusCode.BadRequest, notABool.StatusCode);

            // Overlapping requests: each read gets a connection it can use.
            var responses = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => client.GetAsync("/api/query/timeEntries?billable=true")));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            foreach (var r in responses) r.Dispose();
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
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs.Crypto", "DotnetCqrs.Crypto.csproj")}" />
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

    // Milestone D3: a read model with a pii column. The event carries the {"$pii":...}
    // envelope, as the generated protector writes it; the projection stores it as-is.
    private const string PiiJson = """
        {
          "eventModelingSchemaVersion": "3.0.0", "id": "pii-route-test", "name": "Pii Route Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]}
          },
          "commands": {
            "register-customer": {"name": "Register Customer", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]}
          },
          "readModels": {
            "customers": {
              "name": "Customers",
              "builtFromEventIds": ["customer-registered"],
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]
            }
          },
          "screens": {"scr1": {"name": "Register Screen"}},
          "slices": [
            {
              "id": "register-customer-slice", "name": "Register Customer", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"],
              "scenarios": []
            }
          ]
        }
        """;

    private const string PiiProgramCs = """
        using System.Text.Json;
        using DotnetCqrs.Crypto;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Customer;

        var kms = new InMemoryKmsClient();
        var cache = new PiiRevealCache();
        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new CustomersProjection(store);
        await projection.InitAsync();

        long position = 0;
        foreach (var (id, email) in new[] { ("c1", "alice@example.com"), ("c2", "bob@example.com") })
        {
            var sealedEmail = await Pii<string>.EncryptAsync(kms, id, email);
            var data = JsonSerializer.Serialize(new { customerId = id, email = sealedEmail });
            position++;
            await projection.ApplyAsync(new Event(position, $"seed-{position}", "customer", id, 1, "CustomerRegistered",
                data, "{}", "1970-01-01T00:00:00.000Z"), CancellationToken.None);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        builder.Services.AddSingleton<IKmsClient>(kms);
        builder.Services.AddSingleton(cache);
        var app = builder.Build();

        app.MapCustomersRoute();
        app.MapGet("/test/calls", () => kms.DecryptBatchCalls);
        // Erasure as a host sees it: the key destroyer and the cache evictor both react
        // to SubjectErased.
        app.MapPost("/test/erase/{id}", async (string id) =>
        {
            var erased = new Event(99, "erase", DataSubject.Aggregate, id, 1, DataSubject.SubjectErasedEvent, "{}", "{}", "");
            await new SubjectKeyDestroyer(kms).ApplyAsync(erased, CancellationToken.None);
            await new PiiCacheEvictor(cache).ApplyAsync(erased, CancellationToken.None);
        });
        await app.RunAsync();
        """;

    [Fact(Timeout = 300000)]
    public async Task Generated_query_route_reveals_pii_columns_redacts_erased_subjects_and_refuses_pii_filters()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(PiiJson));
        var domain = Assert.Single(mapped.Domains);
        var readModel = Assert.Single(domain.ReadModels);
        Assert.True(readModel.Fields.Single(f => f.Name == "email").Pii);
        var files = new List<GeneratedFile>(CSharpGenerator.Generate(domain)) { ReadModelQueryGenerator.Generate(domain, readModel) };
        var port = await StartHostAndBuildAsync([.. files], PiiProgramCs, _scratchDir);

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

            async Task<JsonElement> PollAsync()
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(500);
                    try { return await client.GetFromJsonAsync<JsonElement>("/api/query/customers"); }
                    catch (HttpRequestException) { /* not listening yet -- retry */ }
                }
                throw new TimeoutException("generated host never answered /api/query/customers");
            }

            static string Email(JsonElement rows, string id)
            {
                var e = rows.EnumerateArray().Single(r => r.GetProperty("customer_id").GetString() == id).GetProperty("email");
                return e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
            }

            // Revealed: one decrypt-batch per subject on the page.
            var first = await PollAsync();
            Assert.False(process.HasExited, "generated host process exited early");
            Assert.Equal("alice@example.com", Email(first, "c1"));
            Assert.Equal("bob@example.com", Email(first, "c2"));
            Assert.Equal(2, await client.GetFromJsonAsync<int>("/test/calls"));

            // Warm: the cache answers, no further calls.
            var warm = await client.GetFromJsonAsync<JsonElement>("/api/query/customers");
            Assert.Equal("alice@example.com", Email(warm, "c1"));
            Assert.Equal(2, await client.GetFromJsonAsync<int>("/test/calls"));

            // A pii column can't be a filter: ciphertext never equals the query value.
            using var byEmail = await client.GetAsync("/api/query/customers?email=alice%40example.com");
            Assert.Equal(HttpStatusCode.BadRequest, byEmail.StatusCode);

            // A query key becomes a column name in the SQL, so only the table's own columns
            // are accepted. An injected UNION aliasing ciphertext as a pii column would
            // otherwise get it decrypted by the reveal.
            var injectedKey = Uri.EscapeDataString("customer_id = 'x' union select customer_id, email as email from customers --");
            using var injected = await client.GetAsync($"/api/query/customers?{injectedKey}=1");
            Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
            using var unknown = await client.GetAsync("/api/query/customers?nope=1");
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using var byKey = await client.GetAsync("/api/query/customers?customerId=c2");
            Assert.Equal(HttpStatusCode.OK, byKey.StatusCode);
            Assert.Equal(1, (await byKey.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
            Assert.Equal(2, await client.GetFromJsonAsync<int>("/test/calls")); // c2 was cached

            // Erased: the marker, not the value and not null. The other subject is unaffected.
            using var erase = await client.PostAsync("/test/erase/c1", null);
            erase.EnsureSuccessStatusCode();
            var after = await client.GetFromJsonAsync<JsonElement>("/api/query/customers");
            Assert.Equal("""{"$redacted":true}""", Email(after, "c1"));
            Assert.Equal("bob@example.com", Email(after, "c2"));
            Assert.Equal(2, await client.GetFromJsonAsync<int>("/test/calls"));
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

    // Milestones D5/D6: schema 3.1.0 match filters. A plain `name` searched three ways (shadow
    // columns) and a pii `email` searched by contains (the plaintext index) and by exact and
    // prefix (the keyed-hash index), all in the separate search store.
    private const string MatchJson = """
        {
          "eventModelingSchemaVersion": "3.1.0", "id": "match-route-test", "name": "Match Route Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "name", "type": "string"},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]}
          },
          "commands": {"register-customer": {"name": "Register Customer", "aggregate": "Customer"}},
          "readModels": {
            "customers": {
              "name": "Customers",
              "builtFromEventIds": ["customer-registered"],
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "name", "type": "string"},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ],
              "filters": [
                {"param": "nameSearch", "field": "name", "kind": "match", "mode": "contains", "normalize": "personName"},
                {"param": "namePrefix", "field": "name", "kind": "match", "mode": "prefix", "minPrefixLength": 2},
                {"param": "nameExact", "field": "name", "kind": "match", "mode": "exact"},
                {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"},
                {"param": "emailExact", "field": "email", "kind": "match", "mode": "exact", "normalize": "email"},
                {"param": "emailPrefix", "field": "email", "kind": "match", "mode": "prefix", "normalize": "email", "minPrefixLength": 3}
              ]
            }
          },
          "screens": {"scr1": {"name": "Register Screen"}},
          "slices": [
            {
              "id": "register-customer-slice", "name": "Register Customer", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"],
              "scenarios": []
            }
          ]
        }
        """;

    private const string MatchProgramCs = """
        using System.Text.Json;
        using DotnetCqrs.Crypto;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Customer;

        var kms = new InMemoryKmsClient();
        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        var projection = new CustomersProjection(store);
        await projection.InitAsync();
        var searchPath = Path.Combine(AppContext.BaseDirectory, "search.db");
        File.Delete(searchPath);
        var searchStore = await SqliteSearchIndexStore.OpenAsync(searchPath);
        await SqliteSearchIndexStore.AttachAsync(store.Connection, searchPath);
        var searchIndex = new CustomersSearchIndex(searchStore, kms);
        await searchIndex.InitAsync();
        // The hashed index goes through the real registration (it records the key version the
        // route pins its hmac call to), fed from an event store by the engine.
        var events = await SqliteEventStore.OpenAsync(":memory:");
        await kms.EnsureIndexKeyAsync("app");
        var indexKey = new HashedIndexKey("app");
        CustomersHashedIndex? hashedIndex = null;
        var engine = new DotnetCqrs.Consumers.ConsumerEngine(events, events);
        await engine.RegisterHashedSearchIndexAsync(searchStore, indexKey, await kms.GetIndexKeyVersionAsync("app"),
            version => hashedIndex = new CustomersHashedIndex(searchStore, kms, "app", version));

        long position = 0;
        foreach (var (id, name, email) in new[] { ("c1", "José Núñez", "Alice@Example.com"), ("c2", "Bob 50% Off", "bob@example.com"), ("c3", "Joseph", "carol@example.com") })
        {
            var sealedEmail = await Pii<string>.EncryptAsync(kms, id, email);
            var ev = new Event(++position, $"seed-{position}", "customer", id, 1, "CustomerRegistered",
                JsonSerializer.Serialize(new { customerId = id, name, email = sealedEmail }), "{}", "1970-01-01T00:00:00.000Z");
            await projection.ApplyAsync(ev, CancellationToken.None);
            await searchIndex.ApplyAsync(ev, CancellationToken.None);
            await events.AppendAsync("customer", id, 0, [new NewEvent("CustomerRegistered", ev.Data)]);
        }
        await engine.RunOnceAsync();

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        builder.Services.AddSingleton<IKmsClient>(kms);
        builder.Services.AddSingleton(indexKey);
        var app = builder.Build();
        app.MapCustomersRoute();
        app.MapPost("/test/erase/{id}", async (string id) =>
        {
            var erased = new Event(99, "erase", DataSubject.Aggregate, id, 1, DataSubject.SubjectErasedEvent, "{}", "{}", "");
            await new SubjectKeyDestroyer(kms).ApplyAsync(erased, CancellationToken.None);
            await searchIndex.ApplyAsync(erased, CancellationToken.None);
            await hashedIndex!.ApplyAsync(erased, CancellationToken.None);
        });
        await app.RunAsync();
        """;

    [Fact(Timeout = 300000)]
    public async Task Generated_query_route_searches_match_filters_on_shadow_columns_and_the_pii_search_index()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(MatchJson));
        var domain = Assert.Single(mapped.Domains);
        var readModel = Assert.Single(domain.ReadModels);
        var files = new List<GeneratedFile>(CSharpGenerator.Generate(domain)) { ReadModelQueryGenerator.Generate(domain, readModel) };
        Assert.Contains(files, f => f.Name == "CustomersSearchIndex.cs");
        Assert.Contains(files, f => f.Name == "CustomersHashedIndex.cs");
        var port = await StartHostAndBuildAsync([.. files], MatchProgramCs, _scratchDir);

        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--project", _scratchDir, "--no-build" }) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(500);
                try { (await client.GetAsync("/api/query/customers")).EnsureSuccessStatusCode(); break; }
                catch (HttpRequestException) { /* not listening yet -- retry */ }
            }
            Assert.False(process.HasExited, "generated host process exited early");

            async Task<string[]> IdsAsync(string query)
            {
                var rows = await client.GetFromJsonAsync<JsonElement>($"/api/query/customers?{query}");
                return [.. rows.EnumerateArray().Select(r => r.GetProperty("customer_id").GetString()!).Order()];
            }

            // Non-pii, via shadow columns: the query term is normalized exactly as the stored side.
            Assert.Equal(["c1"], await IdsAsync("nameSearch=NUNEZ"));              // personName: case and diacritics
            Assert.Equal(["c1", "c3"], await IdsAsync("namePrefix=JO"));           // caseFold prefix
            Assert.Equal(["c3"], await IdsAsync("nameExact=%20joseph%20"));        // caseFold exact, trimmed
            Assert.Equal(["c2"], await IdsAsync("nameSearch=%25"));                // a literal %, not a wildcard
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/query/customers?namePrefix=j")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/query/customers?nameSearch=%20")).StatusCode);

            // The shadow columns are an implementation detail: never in the response.
            var all = await client.GetFromJsonAsync<JsonElement>("/api/query/customers");
            Assert.DoesNotContain(all[0].EnumerateObject(), p => p.Name.Contains("__match"));

            // pii contains, via the search store: the row comes back with its email revealed.
            var hit = await client.GetFromJsonAsync<JsonElement>("/api/query/customers?emailSearch=ALICE%40");
            Assert.Equal("c1", Assert.Single(hit.EnumerateArray()).GetProperty("customer_id").GetString());
            Assert.Equal("Alice@Example.com", hit[0].GetProperty("email").GetString());

            // pii exact/prefix, via the keyed-hash index: the term is normalized, then hashed.
            Assert.Equal(["c1"], await IdsAsync("emailExact=%20ALICE%40example.COM"));
            Assert.Empty(await IdsAsync("emailExact=alice"));                     // exact is not prefix
            Assert.Equal(["c1"], await IdsAsync("emailPrefix=ALI"));
            Assert.Equal(["c1"], await IdsAsync("emailPrefix=alice%40example.com")); // the whole value is a prefix too
            Assert.Empty(await IdsAsync("emailPrefix=alix"));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/query/customers?emailPrefix=al")).StatusCode);
            var hashed = await client.GetFromJsonAsync<JsonElement>("/api/query/customers?emailPrefix=bob");
            Assert.Equal("bob@example.com", Assert.Single(hashed.EnumerateArray()).GetProperty("email").GetString());

            // Erasure deletes the subject's index entries, plaintext and hashed alike: "no match"
            // is now the right answer, and a guessed value can no longer be confirmed.
            (await client.PostAsync("/test/erase/c1", null)).EnsureSuccessStatusCode();
            Assert.Empty(await IdsAsync("emailSearch=alice"));
            Assert.Equal(["c2"], await IdsAsync("emailSearch=bob"));
            Assert.Empty(await IdsAsync("emailExact=alice%40example.com"));
            Assert.Empty(await IdsAsync("emailPrefix=ali"));
            Assert.Equal(["c2"], await IdsAsync("emailPrefix=bob"));
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
