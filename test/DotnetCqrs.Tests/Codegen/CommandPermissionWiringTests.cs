using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A document that says who may send a command gets that enforced by the generated host with nothing to wire: its
/// Program.cs passes the generated CommandAuthorization to MapCqrsGateway, reading the caller's role and own subject
/// id from claims named in configuration. A document that says nothing generates the gateway exactly as before.
/// </summary>
public class CommandPermissionWiringTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (dotnetcqrs.slnx)");
    }

    private static string ProgramFor(string fixture, IReadOnlyDictionary<string, string>? overrides = null)
    {
        overrides ??= new Dictionary<string, string>();
        var doc = DocumentLoader.LoadFromFile(TestDataPath(fixture));
        var mapped = DocumentMapper.Map(doc, new MappingOptions { AggregateOverrides = overrides });
        return HostProjectGenerator
            .Generate(doc, mapped, "Host", Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj"), overrides)
            .Single(f => f.Name == "Program.cs").Source;
    }

    [Fact]
    public void A_document_with_command_permissions_wires_them_into_the_gateway()
    {
        var program = ProgramFor("state-ownership.json");

        Assert.Contains("var roleClaim = builder.Configuration[\"Cqrs:RoleClaim\"] ?? \"role\";", program);
        Assert.Contains("builder.Configuration[\"Cqrs:SubjectClaim\"] ?? System.Security.Claims.ClaimTypes.NameIdentifier;", program);
        Assert.Contains("app.MapCqrsGateway(authorize: (user, aggregate, command, aggregateId, payload, ct) =>", program);
        Assert.Contains("Generated.CommandAuthorization.AuthorizeAsync(user, aggregate, command, aggregateId, payload,", program);
        Assert.Contains("readModelDb, ResolveOwnRole, ResolveOwnSubjectId, ct));", program);
        // dataSubject keeps the refusal it had with no hook (no personal data here, so no throwaway switch).
        Assert.Contains("? (false ? Task.FromResult(true) : throw new CommandForbiddenException(", program);
        Assert.DoesNotContain("app.MapCqrsGateway();", program);
    }

    [Fact]
    public void A_document_without_command_permissions_generates_the_gateway_as_before()
    {
        var program = ProgramFor("order-fulfillment.json",
            new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" });

        Assert.DoesNotContain("Cqrs:RoleClaim", program);
        Assert.DoesNotContain("readModelDb, ResolveOwnRole, ResolveOwnSubjectId, ct));", program);
        // its personal data keeps the erasure switch exactly as it was
        Assert.Contains("app.MapCqrsGateway(authorize: allowUnauthorizedErasure ? (_, _, _, _, _, _) => Task.FromResult(true) : null);", program);
    }

    private const string RoleProgram = """
        using System.Security.Claims;
        using System.Text.Json;

        ClaimsPrincipal As(string role) => new(new ClaimsIdentity(new[] { new Claim("role", role) }, "test"));
        string Role(ClaimsPrincipal u) => u.FindFirst("role")?.Value ?? "";
        var payload = JsonDocument.Parse("{}").RootElement;
        async Task Check(string label, ClaimsPrincipal user, string aggregate, string command) =>
            Console.WriteLine($"{label} {await Generated.CommandAuthorization.AuthorizeAsync(user, aggregate, command, "l1", payload, null!, Role, _ => "")}");

        await Check("coordinator", As("coordinator"), "loanRequest", "RequestLoan");
        await Check("member", As("member"), "loanRequest", "RequestLoan");
        await Check("member-lowercase", As("member"), "loanrequest", "requestloan");
        await Check("member-undeclared", As("member"), "branch", "JoinBranch");
        await Check("anonymous", new ClaimsPrincipal(new ClaimsIdentity()), "loanRequest", "RequestLoan");
        Console.WriteLine("DONE");
        """;

    [Fact(Timeout = 180000)]
    [Trait("Category", "Compiles")]
    public async Task The_generated_table_checks_the_role_whatever_the_spelling_and_leaves_undeclared_commands_open()
    {
        var node = JsonNode.Parse(File.ReadAllText(TestDataPath("state-ownership.json")))!;
        node["commands"]!["request-loan"]!["requiredRole"] = "coordinator";
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString()));
        var files = mapped.Domains.SelectMany(CSharpGenerator.Generate).Append(CommandAuthorizationGenerator.Generate(mapped.Domains));

        var (success, output) = await InMemoryProgram.RunAsync(files, RoleProgram);

        Assert.True(success, output);
        Assert.Contains("coordinator True", output);
        Assert.Contains("member False", output);
        Assert.Contains("member-lowercase False", output);
        Assert.Contains("member-undeclared True", output);
        Assert.Contains("anonymous False", output);
        Assert.Contains("DONE", output);
    }
}
