using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A message names the system that sent it by that system's document id, never by the name a view gives it
/// (coordinating document 0.3.1); a sender publishes from its own document alone; a system may be named by release.
/// The same documents as pocketcqrs's sendername_test.go, so the two generators are held to one meaning.
/// </summary>
public class SenderNameTests
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

    private const string ByPath = """
        { "people": { "name": "Members", "document": { "path": "members.eventmodel.json" } },
          "loans": { "name": "Lending", "document": { "path": "lending.eventmodel.json" } } }
        """;

    private const string ByRelease = """
        { "people": { "name": "Members", "release": { "system": "members", "tag": "v1.0.0" } },
          "loans": { "name": "Lending", "release": { "system": "lending", "tag": "v2.0.0" } } }
        """;

    private static string MembersWithFields(string fields) => ArchitectureEdgesTests.Members
        .Replace("\"public\": { \"version\": 1 }", $"\"public\": {{ \"version\": 1, \"fields\": [{fields}] }}")
        .Replace("\"eventModelingSchemaVersion\": \"6.1.0\"", "\"eventModelingSchemaVersion\": \"6.2.0\"");

    private static (string Arch, string Dir) View(string members, string systems, string edge)
    {
        var dir = Directory.CreateTempSubdirectory("sender-name-").FullName;
        File.WriteAllText(Path.Combine(dir, "members.eventmodel.json"), members);
        File.WriteAllText(Path.Combine(dir, "lending.eventmodel.json"), ArchitectureEdgesTests.Lending);
        var arch = Path.Combine(dir, "library.architecture.json");
        File.WriteAllText(arch, $$"""
            { "architectureSchemaVersion": "0.3.1", "id": "library", "name": "Library",
              "systems": {{systems}}, "edges": [{{edge}}] }
            """);
        return (arch, dir);
    }

    private static SystemEdges EdgesIn(string arch, string key, string docPath) =>
        ArchitectureEdges.For(arch, key, DocumentLoader.LoadFromFile(docPath), docPath);

    [Fact]
    public void A_message_names_its_sender_by_its_document_id()
    {
        var (arch, dir) = View(ArchitectureEdgesTests.Members, ByPath, PeopleToLoans);
        var sender = EdgesIn(arch, "people", Path.Combine(dir, "members.eventmodel.json"));
        Assert.Equal("members", sender.System);
        Assert.Equal("members", Assert.Single(sender.Out).System);
        Assert.Contains("public const string System = \"members\";", EdgesGenerator.Generate(sender).Source);

        var receiver = EdgesIn(arch, "loans", Path.Combine(dir, "lending.eventmodel.json"));
        Assert.Equal("members", Assert.Single(receiver.In).FromSystem);
        Assert.True(Assert.Single(receiver.Examples).Passed);

        // what the sender sends is what the receiver finds
        var ev = new Event(1, "ev-1", "member", "m-1", 1, "MemberJoined",
            """{"memberId":"m-1","branchId":"north","email":"reader@example.org"}""", "{}", "2026-11-25T00:00:00Z");
        var m = DotnetCqrs.Edges.Edges.Publish(sender.Out, ev)!;
        Assert.True(Assert.Single(receiver.In).Matches(m));
    }

    [Fact]
    public void An_edge_from_a_placeholder_is_refused()
    {
        var systems = """
            { "people": { "name": "Members", "placeholder": { "publishes": [{ "event": "member-joined", "version": 1,
                "fields": [{ "name": "memberId", "type": "string" }, { "name": "branchId", "type": "string" }] }] } },
              "loans": { "name": "Lending", "document": { "path": "lending.eventmodel.json" } } }
            """;
        var (arch, dir) = View(ArchitectureEdgesTests.Members, systems, PeopleToLoans);
        var ex = Assert.Throws<DocumentMappingException>(() => EdgesIn(arch, "loans", Path.Combine(dir, "lending.eventmodel.json")));
        Assert.Contains("placeholder", ex.Message);
    }

    [Fact]
    public void Systems_named_by_release_are_named_by_their_document_id()
    {
        var (arch, dir) = View(MembersWithFields("\"memberId\", \"branchId\""), ByRelease, PeopleToLoans);
        var receiver = EdgesIn(arch, "loans", Path.Combine(dir, "lending.eventmodel.json"));
        Assert.Equal("members", Assert.Single(receiver.In).FromSystem);
        Assert.True(Assert.Single(receiver.Examples).Passed);
        var sender = EdgesIn(arch, "people", Path.Combine(dir, "members.eventmodel.json"));
        Assert.Equal("members", sender.System);
        Assert.Single(sender.Out);
    }

    [Fact]
    public void A_document_that_is_not_the_released_system_is_refused()
    {
        var (arch, dir) = View(MembersWithFields("\"memberId\", \"branchId\""), ByRelease, PeopleToLoans);
        var ex = Assert.Throws<InvalidOperationException>(() => EdgesIn(arch, "people", Path.Combine(dir, "lending.eventmodel.json")));
        Assert.Contains("\"members\"", ex.Message);
    }

    private static DotnetCqrs.Codegen.Model.Document Doc(string json)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sender-alone-").FullName, "members.eventmodel.json");
        File.WriteAllText(path, json);
        return DocumentLoader.LoadFromFile(path);
    }

    [Fact]
    public void A_sender_publishes_from_its_own_document_alone()
    {
        var edges = ArchitectureEdges.Published(Doc(MembersWithFields("\"memberId\", \"branchId\"")));
        Assert.NotNull(edges);
        Assert.Equal("members", edges!.System);
        Assert.Empty(edges.In);
        var o = Assert.Single(edges.Out);
        Assert.Equal(("members", "MemberJoined"), (o.System, o.EventType));
        Assert.Equal(["branchId", "memberId"], o.Fields);
    }

    [Fact]
    public void An_event_without_a_list_is_not_published_alone() =>
        Assert.Null(ArchitectureEdges.Published(Doc(ArchitectureEdgesTests.Members)));

    [Fact]
    public void A_listed_personal_field_is_refused_alone()
    {
        var ex = Assert.Throws<DocumentMappingException>(() =>
            ArchitectureEdges.Published(Doc(MembersWithFields("\"memberId\", \"email\""))));
        Assert.Contains("\"email\" is personal data", ex.Message);
    }
}
