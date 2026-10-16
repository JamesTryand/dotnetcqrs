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
/// scenarios, and the generated route over real HTTP. Hand-written routes used to scope a
/// caller only when the request carried the scope param.</summary>
public class ReadAccessTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static string ReadAccessJson => File.ReadAllText(TestDataPath("read-access.json"));

    private static readonly ReadAccess.Policy WithRules = new(["supervisor"], "agent_id", "mine",
        [new ReadAccess.Grant("ledBy", "teamLeads", "agent_id", "team_id", "team_id")]);

    private static readonly ReadAccess.Policy RoleOnly = new(["supervisor"], null, null, []);

    [Theory]
    [InlineData("supervisor", ReadAccess.Standing.Unrestricted)]
    [InlineData("SUPERVISOR", ReadAccess.Standing.Unrestricted)]
    [InlineData("agent", ReadAccess.Standing.Restricted)]
    [InlineData("", ReadAccess.Standing.Restricted)]
    public void A_role_holder_sees_everything_and_anyone_else_is_restricted(string role, ReadAccess.Standing expected) =>
        Assert.Equal(expected, ReadAccess.Decide(WithRules, roleResolverWired: true, role));

    [Fact]
    public void Without_access_rules_a_non_holder_is_refused_and_an_unwired_role_resolver_stays_open_as_in_2_7_0()
    {
        Assert.Equal(ReadAccess.Standing.Refused, ReadAccess.Decide(RoleOnly, roleResolverWired: true, "agent"));
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
        Assert.Equal(ReadAccess.Standing.Restricted, ReadAccess.Decide(WithRules with { RequiredRole = null }, roleResolverWired: true, "supervisor"));
        Assert.Equal(ReadAccess.Standing.Unrestricted, ReadAccess.Decide(new ReadAccess.Policy(null, null, null, []), roleResolverWired: false, null));
    }

    [Fact]
    public void A_restricted_caller_gets_their_own_rows_or_their_granted_rows_and_nothing_without_a_subject()
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var next = ReadAccess.Restrict(WithRules, "a1", clauses, parameters, 0);
        Assert.Equal(1, next);
        Assert.Equal("(agent_id = @p0 OR team_id IN (SELECT team_id FROM teamLeads WHERE agent_id = @p0))", Assert.Single(clauses));
        Assert.Equal("a1", parameters["@p0"]);

        clauses.Clear();
        ReadAccess.Restrict(WithRules, null, clauses, parameters, 0);
        Assert.Equal("1 = 0", Assert.Single(clauses));
    }

    [Fact]
    public void The_loader_refuses_features_the_generator_does_not_implement()
    {
        var json = ReadAccessJson
            .Replace("\"selfAccess\": { \"subjectField\": \"agentId\", \"param\": \"mine\" }",
                "\"selfAccess\": { \"subjectField\": \"agentId\", \"param\": \"mine\", \"via\": { \"readModelId\": \"team-leads\", \"keyField\": \"agentId\", \"ownerField\": \"agentId\" } }")
            .Replace("\"swimlanes\": [", "\"partitioning\": { \"key\": \"tenantId\" },\n  \"swimlanes\": [");
        Assert.Contains("\"via\"", json);
        Assert.Contains("\"partitioning\"", json);

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(json));
        Assert.Contains(ex.Errors, e => e.InstanceLocation == "/readModels/tickets/selfAccess/via" && e.Message.Contains("not supported"));
        Assert.Contains(ex.Errors, e => e.InstanceLocation == "/partitioning");
    }

    [Theory]
    [InlineData("\"subjectField\": \"agentId\", \"param\": \"mine\"", "\"subjectField\": \"nobody\", \"param\": \"mine\"", "is not one of its fields")]
    [InlineData("\"subjectField\": \"agentId\", \"param\": \"mine\"", "\"subjectField\": \"agentId\", \"param\": \"ledBy\"", "is also a filter or scope param")]
    public void The_mapper_refuses_access_declarations_it_cannot_apply_as_written(string from, string to, string message)
    {
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace(from, to));
        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("tickets") && e.Contains(message));
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
        // The same document with grantsAccess switched off: the team lead's scenario must
        // now fail, which shows the passing run above is checking the rule, not passing vacuously.
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace("\"grantsAccess\": true", "\"grantsAccess\": false"));
        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.False(results.Single(r => r.ScenarioId == "team-lead-also-sees-led-teams").Passed);
        Assert.True(results.Single(r => r.ScenarioId == "agent-sees-only-own-tickets").Passed);
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Compiles")]
    public async Task Removing_the_row_that_granted_access_takes_the_access_away_and_without_removedBy_it_would_not()
    {
        // Schema 3.9.0: team-leads declares removedByEventIds, so unassigning a team
        // lead deletes their row, and the grant through it goes too.
        var doc = DocumentLoader.Parse(ReadAccessJson);
        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));
        Assert.True(results.Single(r => r.ScenarioId == "an-unassigned-team-lead-loses-the-grant").Passed);

        // Without the declaration the generated projection keeps the row, so the same
        // scenario must fail.
        var keeps = DocumentLoader.Parse(ReadAccessJson.Replace("\"removedByEventIds\": [\"team-lead-unassigned\"],", ""));
        var keepResults = await ScenarioVerifier.VerifyAsync(keeps, DocumentMapper.Map(keeps));
        Assert.False(keepResults.Single(r => r.ScenarioId == "an-unassigned-team-lead-loses-the-grant").Passed);
    }

    [Fact(Timeout = 120000)]
    [Trait("Category", "Compiles")]
    public async Task The_harness_refuses_a_caller_without_the_role_on_a_read_model_that_declares_only_requiredRole()
    {
        // team-leads declares requiredRole and nothing else, so the generated route answers a
        // caller without the role 403 (its rows are no one's own). The harness
        // used to read such a caller as a role holder.
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReadAccessJson)!;
        System.Text.Json.Nodes.JsonNode Scenario(string id, string subject, string role, string expected) =>
            System.Text.Json.Nodes.JsonNode.Parse(
                "{\"id\": \"" + id + "\", \"name\": \"" + id + "\", \"kind\": \"stateView\", " +
                "\"given\": [{\"eventId\": \"team-lead-assigned\", \"data\": {\"assignmentId\": \"g1:a1\", \"teamId\": \"g1\", \"agentId\": \"a1\"}}], " +
                "\"when\": {\"readModelId\": \"team-leads\", \"caller\": {\"subjectId\": \"" + subject + "\", \"role\": \"" + role + "\"}}, " +
                "\"then\": {\"result\": {\"leads\": " + expected + "}}}")!;
        node["slices"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""
            {"id": "view-leads-slice", "name": "View Leads", "pattern": "stateView", "swimlaneId": "desk",
             "status": "created", "screenId": "my-tickets-screen", "readModelId": "team-leads", "scenarios": []}
            """));
        var scenarios = node["slices"]!.AsArray().Last()!["scenarios"]!.AsArray();
        scenarios.Add(Scenario("a-supervisor-sees-the-rows", "sup1", "supervisor", """[{"teamId": "g1", "agentId": "a1"}]"""));
        scenarios.Add(Scenario("agents-are-refused", "a1", "agent", "[]"));
        var doc = DocumentLoader.Parse(node.ToJsonString());

        var results = await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc));

        Assert.True(results.Single(r => r.ScenarioId == "a-supervisor-sees-the-rows").Passed,
            results.Single(r => r.ScenarioId == "a-supervisor-sees-the-rows").Detail);
        Assert.True(results.Single(r => r.ScenarioId == "agents-are-refused").Passed,
            results.Single(r => r.ScenarioId == "agents-are-refused").Detail);
    }

    [Theory]
    [InlineData("\"removedByEventIds\": [\"team-lead-unassigned\"]", "\"removedByEventIds\": [\"ticket-opened\"]", "not in its builtFromEventIds")]
    public void The_mapper_refuses_a_removal_it_cannot_apply_as_written(string from, string to, string message)
    {
        var doc = DocumentLoader.Parse(ReadAccessJson.Replace(from, to));
        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("team-leads") && e.Contains(message));
    }

    // The generated routes for both read models, served for real. X-Test-Role and X-Test-Subject
    // stand in for what a host resolves from the signed-in user.
    private const string ProgramCsTemplate = """
        using System.Security.Claims;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Team;
        using Generated.Ticket;

        IReadModelStore store = await SqliteReadModelStore.OpenAsync(":memory:");
        var tickets = new TicketsProjection(store);
        await tickets.InitAsync();
        var leads = new TeamLeadsProjection(store);
        await leads.InitAsync();

        long position = 0;
        async Task SeedAsync(DotnetCqrs.Projections.IProjection projection, string aggregate, string id, string type, object data)
        {
            position++;
            var ev = new Event(position, $"seed-{position}", aggregate, id, position, type, System.Text.Json.JsonSerializer.Serialize(data), "{}", "1970-01-01T00:00:00.000Z");
            await projection.ApplyAsync(ev, CancellationToken.None);
        }

        // a1 leads g2. Tickets: a1 on g1, a2 on g2, a3 on g3, sup1 on g1.
        await SeedAsync(leads, "team", "g2:a1", "TeamLeadAssigned", new { assignmentId = "g2:a1", teamId = "g2", agentId = "a1" });
        await SeedAsync(tickets, "ticket", "t1", "TicketOpened", new { ticketId = "t1", teamId = "g1", agentId = "a1", estimatedEffort = 1 });
        await SeedAsync(tickets, "ticket", "t2", "TicketOpened", new { ticketId = "t2", teamId = "g2", agentId = "a2", estimatedEffort = 2 });
        await SeedAsync(tickets, "ticket", "t3", "TicketOpened", new { ticketId = "t3", teamId = "g3", agentId = "a3", estimatedEffort = 3 });
        await SeedAsync(tickets, "ticket", "t4", "TicketOpened", new { ticketId = "t4", teamId = "g1", agentId = "sup1", estimatedEffort = 4 });

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

    private static async Task<string[]> TicketIdsAsync(HttpClient client, string? role, string? subject, string query = "")
    {
        using var response = await SendAsync(client, role, subject, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        return rows.EnumerateArray().Select(r => r.GetProperty("ticket_id").GetString()!).Order().ToArray();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? role, string? subject, string query = "")
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/query/tickets" + query);
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
            "app.MapTicketsRoute(resolveOwnRole: ResolveOwnRole, resolveSubjectId: ResolveSubjectId);"));
        var client = host.Client;

        // A supervisor holds requiredRole: every row, and the selfAccess param narrows them to their own.
        Assert.Equal(["t1", "t2", "t3", "t4"], await TicketIdsAsync(client, "supervisor", "sup1"));
        Assert.Equal(["t4"], await TicketIdsAsync(client, "supervisor", "sup1", "?mine=anything"));

        // An agent with no params: their own row, plus the rows of the team they lead. Never t3.
        Assert.Equal(["t1", "t2"], await TicketIdsAsync(client, "agent", "a1"));
        // A plain column filter cannot widen it.
        Assert.Equal(Array.Empty<string>(), await TicketIdsAsync(client, "agent", "a1", "?agentId=a3"));
        // A bound param ignores the value sent: ledBy=a2 still means "led by me".
        Assert.Equal(["t2"], await TicketIdsAsync(client, "agent", "a1", "?ledBy=s2"));
        Assert.Equal(["t1"], await TicketIdsAsync(client, "agent", "a1", "?mine=a3"));
        // Someone with no rows and nothing granted sees nothing, and so does a caller with no subject id.
        Assert.Equal(Array.Empty<string>(), await TicketIdsAsync(client, "agent", "a9"));
        Assert.Equal(Array.Empty<string>(), await TicketIdsAsync(client, "agent", null));
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_route_fails_closed_when_the_host_has_not_wired_the_hooks()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapTicketsRoute(resolveOwnRole: ResolveOwnRole);"));
        var client = host.Client;

        // No resolveSubjectId: a restricted caller is refused, not shown every row.
        using var agent = await SendAsync(client, "agent", "a1");
        Assert.Equal(HttpStatusCode.Forbidden, agent.StatusCode);
        // A supervisor needs no subject id, unless they ask for their own rows.
        Assert.Equal(["t1", "t2", "t3", "t4"], await TicketIdsAsync(client, "supervisor", "sup1"));
        using var mine = await SendAsync(client, "supervisor", "sup1", "?mine=1");
        Assert.Equal(HttpStatusCode.Forbidden, mine.StatusCode);
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_route_fails_closed_when_no_role_resolver_is_wired()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            "app.MapTicketsRoute();"));
        using var response = await SendAsync(host.Client, "supervisor", "sup1");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
