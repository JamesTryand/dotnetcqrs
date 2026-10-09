using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>Schema 3.8.0 read access: who may read which rows. The rule itself (ReadAccess), the
/// declarations the loader and mapper refuse, the verify harness running the read-access example's
/// scenarios, and the generated route over real HTTP. Raised by project/timesheets D21: hand-written
/// routes scoped a staff caller only when the request carried the scope param.</summary>
public class ReadAccessTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static string ReadAccessJson => File.ReadAllText(TestDataPath("read-access.json"));

    private static readonly ReadAccess.Policy WithRules = new(["manager"], "staff_id", "mine",
        [new ReadAccess.Grant("managedBy", "projectManagers", "staff_id", "project_id", "project_id")]);

    private static readonly ReadAccess.Policy RoleOnly = new(["manager"], null, null, []);

    [Theory]
    [InlineData("manager", ReadAccess.Standing.Unrestricted)]
    [InlineData("MANAGER", ReadAccess.Standing.Unrestricted)]
    [InlineData("staff", ReadAccess.Standing.Restricted)]
    [InlineData("", ReadAccess.Standing.Restricted)]
    public void A_role_holder_sees_everything_and_anyone_else_is_restricted(string role, ReadAccess.Standing expected) =>
        Assert.Equal(expected, ReadAccess.Decide(WithRules, roleResolverWired: true, role));

    [Fact]
    public void Without_access_rules_a_non_holder_is_refused_and_an_unwired_role_resolver_stays_open_as_in_2_7_0()
    {
        Assert.Equal(ReadAccess.Standing.Refused, ReadAccess.Decide(RoleOnly, roleResolverWired: true, "staff"));
        Assert.Equal(ReadAccess.Standing.Unrestricted, ReadAccess.Decide(RoleOnly, roleResolverWired: false, null));
    }

    [Fact]
    public void With_access_rules_an_unwired_role_resolver_fails_closed()
    {
        Assert.Equal(ReadAccess.Standing.Refused, ReadAccess.Decide(WithRules, roleResolverWired: false, null));
    }

    [Fact]
    public void Without_requiredRole_access_rules_restrict_everyone_and_no_declarations_leave_it_open()
    {
        Assert.Equal(ReadAccess.Standing.Restricted, ReadAccess.Decide(WithRules with { RequiredRole = null }, roleResolverWired: true, "manager"));
        Assert.Equal(ReadAccess.Standing.Unrestricted, ReadAccess.Decide(new ReadAccess.Policy(null, null, null, []), roleResolverWired: false, null));
    }

    [Fact]
    public void A_restricted_caller_gets_their_own_rows_or_their_granted_rows_and_nothing_without_a_subject()
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var next = ReadAccess.Restrict(WithRules, "s1", clauses, parameters, 0);
        Assert.Equal(1, next);
        Assert.Equal("(staff_id = @p0 OR project_id IN (SELECT project_id FROM projectManagers WHERE staff_id = @p0))", Assert.Single(clauses));
        Assert.Equal("s1", parameters["@p0"]);

        clauses.Clear();
        ReadAccess.Restrict(WithRules, null, clauses, parameters, 0);
        Assert.Equal("1 = 0", Assert.Single(clauses));
    }

    [Fact]
    public void The_loader_refuses_features_the_generator_does_not_implement()
    {
        var json = ReadAccessJson
            .Replace("\"selfAccess\": { \"subjectField\": \"staffId\", \"param\": \"mine\" }",
                "\"selfAccess\": { \"subjectField\": \"staffId\", \"param\": \"mine\", \"via\": { \"readModelId\": \"project-managers\", \"keyField\": \"staffId\", \"ownerField\": \"staffId\" } }")
            .Replace("\"swimlanes\": [", "\"partitioning\": { \"key\": \"tenantId\" },\n  \"swimlanes\": [");
        Assert.Contains("\"via\"", json);
        Assert.Contains("\"partitioning\"", json);

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
        Assert.Contains(ex.Errors, e => e.InstanceLocation == "/readModels/time-entries/selfAccess/via" && e.Message.Contains("not supported"));
        Assert.Contains(ex.Errors, e => e.InstanceLocation == "/partitioning");
    }

    [Theory]
    [InlineData("\"subjectField\": \"staffId\", \"param\": \"mine\"", "\"subjectField\": \"nobody\", \"param\": \"mine\"", "is not one of its fields")]
    [InlineData("\"subjectField\": \"staffId\", \"param\": \"mine\"", "\"subjectField\": \"staffId\", \"param\": \"managedBy\"", "is also a filter or scope param")]
    public void The_mapper_refuses_access_declarations_it_cannot_apply_as_written(string from, string to, string message)
    {
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace(from, to));
        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("time-entries") && e.Contains(message));
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Compiles")]
    public async Task The_harness_applies_the_rule_to_every_scenario_in_the_read_access_example()
    {
        var doc = DocumentLoader.Parse(ReadAccessJson);
        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        var views = results.Where(r => r.Kind == "stateView").ToList();
        Assert.Equal(6, views.Count);
        Assert.All(views, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Compiles")]
    public async Task The_harness_fails_the_granting_scenario_when_the_scope_does_not_grant_access()
    {
        // The same document with grantsAccess switched off: the project manager's scenario must
        // now fail, which shows the passing run above is checking the rule, not passing vacuously.
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace("\"grantsAccess\": true", "\"grantsAccess\": false"));
        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "project-manager-also-sees-managed-projects").Passed);
        Assert.True(results.Single(r => r.ScenarioId == "staff-sees-only-own-entries").Passed);
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Compiles")]
    public async Task Removing_the_row_that_granted_access_takes_the_access_away_and_without_removedBy_it_would_not()
    {
        // Schema 3.9.0: project-managers declares removedByEventIds, so unassigning a project
        // manager deletes their row, and the grant through it goes too.
        var doc = DocumentLoader.Parse(ReadAccessJson);
        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));
        Assert.True(results.Single(r => r.ScenarioId == "an-unassigned-project-manager-loses-the-grant").Passed);

        // Without the declaration the generated projection keeps the row (the bug D23 found in
        // project/timesheets), so the same scenario must fail.
        var keeps = DocumentLoader.Parse(ReadAccessJson.Replace("\"removedByEventIds\": [\"project-manager-unassigned\"],", ""));
        var keepResults = await ScenarioVerifier.VerifyAsync(keeps, DocumentMapper.Map(keeps));
        Assert.False(keepResults.Single(r => r.ScenarioId == "an-unassigned-project-manager-loses-the-grant").Passed);
    }

    [Theory]
    [InlineData("\"removedByEventIds\": [\"project-manager-unassigned\"]", "\"removedByEventIds\": [\"time-logged\"]", "not in its builtFromEventIds")]
    public void The_mapper_refuses_a_removal_it_cannot_apply_as_written(string from, string to, string message)
    {
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace(from, to));
        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("project-managers") && e.Contains(message));
    }

    // The generated routes for both read models, served for real. X-Test-Role and X-Test-Subject
    // stand in for what a host resolves from the signed-in user.
    private const string ProgramCsTemplate = """
        using System.Security.Claims;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Project;
        using Generated.TimeEntry;

        IReadModelStore store = await SqliteReadModelStore.OpenAsync(":memory:");
        var entries = new TimeEntriesProjection(store);
        await entries.InitAsync();
        var managers = new ProjectManagersProjection(store);
        await managers.InitAsync();

        long position = 0;
        async Task SeedAsync(DotnetCqrs.Projections.IProjection projection, string aggregate, string id, string type, object data)
        {
            position++;
            var ev = new Event(position, $"seed-{position}", aggregate, id, position, type, System.Text.Json.JsonSerializer.Serialize(data), "{}", "1970-01-01T00:00:00.000Z");
            await projection.ApplyAsync(ev, CancellationToken.None);
        }

        // s1 manages p2. Entries: s1 on p1, s2 on p2, s3 on p3, m1 on p1.
        await SeedAsync(managers, "project", "p2:s1", "ProjectManagerAssigned", new { assignmentId = "p2:s1", projectId = "p2", staffId = "s1" });
        await SeedAsync(entries, "timeEntry", "e1", "TimeLogged", new { entryId = "e1", projectId = "p1", staffId = "s1", hours = 1 });
        await SeedAsync(entries, "timeEntry", "e2", "TimeLogged", new { entryId = "e2", projectId = "p2", staffId = "s2", hours = 2 });
        await SeedAsync(entries, "timeEntry", "e3", "TimeLogged", new { entryId = "e3", projectId = "p3", staffId = "s3", hours = 3 });
        await SeedAsync(entries, "timeEntry", "e4", "TimeLogged", new { entryId = "e4", projectId = "p1", staffId = "m1", hours = 4 });

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var claims = new List<Claim>();
            if (context.Request.Headers["X-Test-Role"].ToString() is { Length: > 0 } role) claims.Add(new Claim("role", role));
            if (context.Request.Headers["X-Test-Subject"].ToString() is { Length: > 0 } subject) claims.Add(new Claim("sub", subject));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
            await next();
        });
        string ResolveOwnRole(ClaimsPrincipal user) => user.FindFirst("role")?.Value ?? "";
        string? ResolveSubjectId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;

        {{MAP_CALL}}
        await DotnetCqrs.Tests.Codegen.InMemoryHost.ServeAsync(app);
        """;

    private static GeneratedFile[] GenerateReadAccessHost()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(ReadAccessJson));
        var files = new List<GeneratedFile>();
        foreach (var domain in mapped.Domains)
        {
            files.AddRange(CSharpGenerator.Generate(domain));
            files.AddRange(domain.ReadModels.Select(rm => ReadModelQueryGenerator.Generate(domain, rm)));
        }
        return files.ToArray();
    }

    private static async Task<string[]> EntryIdsAsync(HttpClient client, string? role, string? subject, string query = "")
    {
        using var response = await SendAsync(client, role, subject, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        return rows.EnumerateArray().Select(r => r.GetProperty("entry_id").GetString()!).Order().ToArray();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? role, string? subject, string query = "")
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/query/timeEntries" + query);
                if (role is not null) request.Headers.Add("X-Test-Role", role);
                if (subject is not null) request.Headers.Add("X-Test-Subject", subject);
                return await client.SendAsync(request);
            }
            catch (HttpRequestException) when (attempt < 60)
            {
                await Task.Delay(500); // not listening yet
            }
        }
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_route_applies_the_rule_over_real_http()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapTimeEntriesRoute(resolveOwnRole: ResolveOwnRole, resolveSubjectId: ResolveSubjectId);"));
        var client = host.Client;

        // A manager holds requiredRole: every row, and the selfAccess param narrows them to their own.
        Assert.Equal(["e1", "e2", "e3", "e4"], await EntryIdsAsync(client, "manager", "m1"));
        Assert.Equal(["e4"], await EntryIdsAsync(client, "manager", "m1", "?mine=anything"));

        // Staff with no params: their own row, plus the rows on the project they manage. Never e3.
        Assert.Equal(["e1", "e2"], await EntryIdsAsync(client, "staff", "s1"));
        // A plain column filter cannot widen it.
        Assert.Equal(Array.Empty<string>(), await EntryIdsAsync(client, "staff", "s1", "?staffId=s3"));
        // A bound param ignores the value sent: managedBy=s2 still means "managed by me".
        Assert.Equal(["e2"], await EntryIdsAsync(client, "staff", "s1", "?managedBy=s2"));
        Assert.Equal(["e1"], await EntryIdsAsync(client, "staff", "s1", "?mine=s3"));
        // Someone with no rows and nothing granted sees nothing, and so does a caller with no subject id.
        Assert.Equal(Array.Empty<string>(), await EntryIdsAsync(client, "staff", "s9"));
        Assert.Equal(Array.Empty<string>(), await EntryIdsAsync(client, "staff", null));
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_route_fails_closed_when_the_host_has_not_wired_the_hooks()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapTimeEntriesRoute(resolveOwnRole: ResolveOwnRole);"));
        var client = host.Client;

        // No resolveSubjectId: a restricted caller is refused, not shown every row.
        using var staff = await SendAsync(client, "staff", "s1");
        Assert.Equal(HttpStatusCode.Forbidden, staff.StatusCode);
        // A manager needs no subject id, unless they ask for their own rows.
        Assert.Equal(["e1", "e2", "e3", "e4"], await EntryIdsAsync(client, "manager", "m1"));
        using var mine = await SendAsync(client, "manager", "m1", "?mine=1");
        Assert.Equal(HttpStatusCode.Forbidden, mine.StatusCode);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_route_fails_closed_when_no_role_resolver_is_wired()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapTimeEntriesRoute();"));
        using var response = await SendAsync(host.Client, "manager", "m1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
