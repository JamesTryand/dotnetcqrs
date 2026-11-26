using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A system linked in several coordinating documents (views) gets its edges from all of them; views may overlap but
/// not contradict. The same documents as pocketcqrs's views_test.go, so the two generators are held to one meaning.
/// </summary>
public class ViewsTests
{
    private const string PeopleToLoans = """
        { "id": "member-becomes-borrower", "kind": "internal",
          "from": { "system": "people", "event": "member-joined", "version": 1 },
          "to": { "system": "loans", "command": "register-borrower" },
          "targetId": "memberId", "fields": { "borrowerId": "memberId", "branchId": "branchId" },
          "examples": [{ "name": "a member becomes a borrower",
            "given": { "memberId": "m-1", "branchId": "north", "email": "reader@example.org" },
            "then": { "targetId": "m-1", "fields": { "borrowerId": "m-1", "branchId": "north" } } }] }
        """;

    private static string Dir()
    {
        var dir = Directory.CreateTempSubdirectory("views-").FullName;
        File.WriteAllText(Path.Combine(dir, "members.eventmodel.json"), ArchitectureEdgesTests.Members);
        File.WriteAllText(Path.Combine(dir, "lending.eventmodel.json"), ArchitectureEdgesTests.Lending);
        return dir;
    }

    private static string ViewIn(string dir, string id, string sender, string receiver, string edge)
    {
        var p = Path.Combine(dir, id + ".architecture.json");
        File.WriteAllText(p, $$"""
            { "architectureSchemaVersion": "0.3.1", "id": "{{id}}", "name": "A view",
              "systems": { "{{sender}}": { "name": "Members", "document": { "path": "members.eventmodel.json" } },
                           "{{receiver}}": { "name": "Lending", "document": { "path": "lending.eventmodel.json" } } },
              "edges": [{{edge}}] }
            """);
        return p;
    }

    private static string Renamed(string edge) => edge.Replace("\"people\"", "\"readers\"").Replace("\"loans\"", "\"lending\"");

    private static SystemEdges EdgesIn(string dir, params string[] views)
    {
        var doc = Path.Combine(dir, "lending.eventmodel.json");
        return ArchitectureEdges.ForViews(views, [], DocumentLoader.LoadFromFile(doc), doc);
    }

    [Fact]
    public void A_link_in_two_views_is_received_once()
    {
        var dir = Dir();
        var se = EdgesIn(dir, ViewIn(dir, "library", "people", "loans", PeopleToLoans),
            ViewIn(dir, "branches", "readers", "lending", Renamed(PeopleToLoans)));
        var e = Assert.Single(se.In);
        Assert.Equal("member-becomes-borrower", e.Id);
        Assert.Equal("members", e.FromSystem);
        Assert.Equal("library, branches", se.Architecture);
    }

    [Fact]
    public void A_link_in_one_view_of_two_is_received()
    {
        var dir = Dir();
        var se = EdgesIn(dir, ViewIn(dir, "branches", "readers", "lending", ""),
            ViewIn(dir, "library", "people", "loans", PeopleToLoans));
        Assert.Single(se.In);
    }

    [Fact]
    public void Views_that_contradict_are_refused()
    {
        var dir = Dir();
        var changed = Renamed(PeopleToLoans)
            .Replace(", \"branchId\": \"branchId\" }", " }")
            .Replace("\"fields\": { \"borrowerId\": \"m-1\", \"branchId\": \"north\" }", "\"fields\": { \"borrowerId\": \"m-1\" }");
        var ex = Assert.Throws<DocumentMappingException>(() => EdgesIn(dir,
            ViewIn(dir, "library", "people", "loans", PeopleToLoans), ViewIn(dir, "branches", "readers", "lending", changed)));
        Assert.Contains("edge \"member-becomes-borrower\" is one link in \"library\" and another in \"branches\"", ex.Message);
    }

    [Fact]
    public void A_view_not_naming_the_document_is_refused()
    {
        var dir = Dir();
        var stray = Path.Combine(dir, "stray.architecture.json");
        File.WriteAllText(stray, """
            { "architectureSchemaVersion": "0.3.1", "id": "stray", "name": "Stray",
              "systems": { "elsewhere": { "name": "Elsewhere" } }, "edges": [] }
            """);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EdgesIn(dir, ViewIn(dir, "library", "people", "loans", PeopleToLoans), stray));
        Assert.Contains("\"stray\" names no system that is this document", ex.Message);
    }
}
