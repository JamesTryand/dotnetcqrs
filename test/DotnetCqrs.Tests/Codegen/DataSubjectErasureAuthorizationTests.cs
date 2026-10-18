using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// <c>dataSubjects.erasure</c> (schema 3.2.0, 3.7.1) generated into <c>CommandAuthorization</c>. It admits
/// <c>RequestErasure</c> only: <c>self</c> for the caller's own subject id (or, with <c>via</c>, the subject ids a
/// read model says they own), <c>roles</c> for any subject. Every other <c>dataSubject</c> command is refused, and
/// so is every <c>dataSubject</c> command when the document declares nothing: erasure cannot be undone, so the
/// built-in aggregate is the one place "no policy" does not mean "allowed". Compiled and run against a real
/// in-memory store, as <see cref="CommandAuthorizationCombinedTests"/> does.
/// </summary>
[Trait("Category", "Compiles")]
public class DataSubjectErasureAuthorizationTests
{
    private static string Document(string dataSubjects) => """
        {
          "eventModelingSchemaVersion": "4.0.0", "id": "erasure-auth-test", "name": "Erasure Auth Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "loginId", "type": "string"},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]}
          },
          "commands": {
            "register-customer": {"name": "Register Customer", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "loginId", "type": "string"},
                {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
              ]}
          },
          "readModels": {
            "customers": {"name": "Customers", "builtFromEventIds": ["customer-registered"],
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "loginId", "type": "string"}
              ]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "register-customer-slice", "name": "Register Customer", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "register-customer", "eventIds": ["customer-registered"], "scenarios": []}
          ]__DATA_SUBJECTS__
        }
        """.Replace("__DATA_SUBJECTS__", dataSubjects.Length == 0 ? "" : $",\n  \"dataSubjects\": {dataSubjects}");

    // Customers c-old and c-new both belong to login-1 (a returning person gets a new subject id); c-other to login-2.
    private const string Prelude = """"
        using System.Security.Claims;
        using System.Text.Json;
        using DotnetCqrs.ReadModels;
        using Generated;

        var store = await SqliteReadModelStore.OpenAsync(":memory:");
        await using (var create = store.Connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE customers (customer_id TEXT, login_id TEXT);" +
                "INSERT INTO customers VALUES ('c-old', 'login-1'), ('c-new', 'login-1'), ('c-other', 'login-2');";
            await create.ExecuteNonQueryAsync();
        }

        ClaimsPrincipal Actor(string role, string subjectId) =>
            new(new ClaimsIdentity([new Claim("role", role), new Claim("subject", subjectId)], "test"));
        string ResolveRole(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "";
        string ResolveSubjectId(ClaimsPrincipal u) => u.Claims.FirstOrDefault(c => c.Type == "subject")?.Value ?? "";
        var empty = JsonDocument.Parse("{}").RootElement;

        var failures = 0;
        async Task CheckAsync(string name, bool expected, string role, string caller, string aggregate, string command, string id)
        {
            var actual = await CommandAuthorization.AuthorizeAsync(Actor(role, caller), aggregate, command, id, empty, store, ResolveRole, ResolveSubjectId);
            var ok = actual == expected;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} (expected {expected}, got {actual})");
            if (!ok) failures++;
        }

        """";

    private const string Epilogue = """
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
        """;

    private static async Task RunAsync(string dataSubjects, string checks)
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(Document(dataSubjects)));
        var (success, output) = await InMemoryProgram.RunAsync(
            [CommandAuthorizationGenerator.Generate(mapped.Domains, mapped.ErasureRequest)], Prelude + checks + Epilogue);
        Assert.True(success, $"generated CommandAuthorization.cs did not compile, or the program reported failures:{Environment.NewLine}{output}");
        Assert.Contains("ALL PASS", output);
    }

    [Fact(Timeout = 120000)]
    public Task Self_and_roles_admit_a_request_and_nothing_else() => RunAsync(
        """{"erasure": {"self": true, "roles": ["support"]}}""",
        """
        await CheckAsync("self: a customer requests their own erasure", true, "customer", "c-old", "dataSubject", "RequestErasure", "c-old");
        await CheckAsync("self: another customer's is refused", false, "customer", "c-old", "dataSubject", "RequestErasure", "c-other");
        await CheckAsync("self: a caller with no subject id is refused", false, "customer", "", "dataSubject", "RequestErasure", "");
        await CheckAsync("roles: support requests anyone's", true, "support", "agent-1", "dataSubject", "RequestErasure", "c-other");
        await CheckAsync("the aggregate name is matched as the gateway matches it", true, "support", "agent-1", "DataSubject", "RequestErasure", "c-other");
        await CheckAsync("never the erasure itself, even for a listed role", false, "support", "agent-1", "dataSubject", "EraseSubject", "c-other");
        await CheckAsync("never the erasure itself, even for oneself", false, "customer", "c-old", "dataSubject", "EraseSubject", "c-old");
        await CheckAsync("never a governance command", false, "support", "agent-1", "dataSubject", "ApproveErasure", "c-other");
        await CheckAsync("a document command with no policy is still allowed", true, "customer", "c-old", "customer", "RegisterCustomer", "c-old");

        """);

    [Fact(Timeout = 120000)]
    public Task Self_through_via_admits_every_subject_the_caller_owns() => RunAsync(
        """{"erasure": {"self": true, "via": {"readModelId": "customers", "keyField": "customerId", "ownerField": "loginId"}}}""",
        """
        await CheckAsync("via: login-1 requests its earlier subject's erasure", true, "customer", "login-1", "dataSubject", "RequestErasure", "c-old");
        await CheckAsync("via: and its current one's", true, "customer", "login-1", "dataSubject", "RequestErasure", "c-new");
        await CheckAsync("via: not another login's", false, "customer", "login-1", "dataSubject", "RequestErasure", "c-other");
        await CheckAsync("via: the login id itself is not a subject id", false, "customer", "login-1", "dataSubject", "RequestErasure", "login-1");
        await CheckAsync("via: no roles were declared, so a role admits nothing", false, "support", "agent-1", "dataSubject", "RequestErasure", "c-old");

        """);

    [Fact(Timeout = 120000)]
    public Task Without_a_declaration_no_one_may_request_erasure() => RunAsync(
        "",
        """
        await CheckAsync("no declaration: not oneself", false, "customer", "c-old", "dataSubject", "RequestErasure", "c-old");
        await CheckAsync("no declaration: not any role", false, "administrator", "admin-1", "dataSubject", "RequestErasure", "c-old");
        await CheckAsync("no declaration: never the erasure", false, "administrator", "admin-1", "dataSubject", "EraseSubject", "c-old");

        """);

    [Fact]
    public void A_via_naming_a_missing_read_model_is_a_mapping_error()
    {
        var document = DocumentLoader.Parse(Document(
            """{"erasure": {"self": true, "via": {"readModelId": "accounts", "keyField": "customerId", "ownerField": "loginId"}}}"""));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(document));
        Assert.Contains(ex.Report.Errors, e => e.Contains("dataSubjects.erasure.via") && e.Contains("\"accounts\""));
    }

    [Fact]
    public void A_declaration_in_a_document_without_personal_data_is_warned_about()
    {
        var json = Document("""{"erasure": {"roles": ["support"]}}""").Replace(""", "pii": true, "piiSubject": "customerId"}""", "}");

        var mapped = DocumentMapper.Map(DocumentLoader.Parse(json));

        Assert.Contains(mapped.Report.Warnings, w => w.Contains("dataSubjects.erasure") && w.Contains("pii"));
        Assert.NotNull(mapped.ErasureRequest);
    }
}
