using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Edges;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A coordinating document's edges (eventmodelschema architecture 0.2.0, the edges addendum, hub L17): the edges into
/// a system and the events it publishes are read, each edge's examples run against <see cref="EdgeIn.Translate"/>,
/// and the generated tables compile and translate as the example says. The same documents as pocketcqrs's
/// architecture_test.go, so the two generators are held to one meaning.
/// </summary>
public class ArchitectureEdgesTests
{
    private const string Members = """
        {
          "eventModelingSchemaVersion": "6.1.0", "id": "members", "name": "Members",
          "swimlanes": [{ "id": "members", "name": "Members", "kind": "system" }],
          "events": {
            "member-joined": { "name": "Member Joined", "swimlaneId": "members", "aggregate": "member", "public": { "version": 1 },
              "fields": [{ "name": "memberId", "type": "string", "idAttribute": true }, { "name": "branchId", "type": "string" },
                         { "name": "email", "type": "string", "pii": true, "piiSubject": "memberId" }] }
          },
          "commands": {
            "join-member": { "name": "Join Member", "aggregate": "member",
              "fields": [{ "name": "memberId", "type": "string", "idAttribute": true }, { "name": "branchId", "type": "string" },
                         { "name": "email", "type": "string", "pii": true, "piiSubject": "memberId" }] }
          },
          "dataSubjects": { "erasure": { "roles": ["librarian"] } },
          "screens": { "desk": { "name": "Desk" } },
          "slices": [{ "id": "join", "name": "Join", "pattern": "stateChange", "swimlaneId": "members", "status": "created",
            "screenId": "desk", "commandId": "join-member", "eventIds": ["member-joined"], "scenarios": [] }]
        }
        """;

    private const string Lending = """
        {
          "eventModelingSchemaVersion": "6.1.0", "id": "lending", "name": "Lending",
          "swimlanes": [{ "id": "lending", "name": "Lending", "kind": "system" }],
          "events": {
            "borrower-registered": { "name": "Borrower Registered", "swimlaneId": "lending", "aggregate": "borrower",
              "fields": [{ "name": "borrowerId", "type": "string", "idAttribute": true }, { "name": "branchId", "type": "string" }] }
          },
          "commands": {
            "register-borrower": { "name": "Register Borrower", "aggregate": "borrower", "public": { "version": 1 },
              "fields": [{ "name": "borrowerId", "type": "string", "idAttribute": true }, { "name": "branchId", "type": "string" }] }
          },
          "slices": [{ "id": "register", "name": "Register a borrower", "pattern": "stateChange", "swimlaneId": "lending",
            "status": "created", "commandId": "register-borrower", "eventIds": ["borrower-registered"], "scenarios": [] }]
        }
        """;

    private const string Edge = """
        { "id": "member-becomes-borrower", "kind": "internal",
          "from": { "system": "members", "event": "member-joined", "version": 1 },
          "to": { "system": "lending", "command": "register-borrower" },
          "targetId": "memberId", "fields": { "borrowerId": "memberId", "branchId": "branchId" },
          "examples": [{ "name": "a member becomes a borrower, their email stays",
            "given": { "memberId": "m-1", "branchId": "north", "email": "reader@example.org" },
            "then": { "targetId": "m-1", "fields": { "borrowerId": "m-1", "branchId": "north" } } }] }
        """;

    private static SystemEdges EdgesOf(string edge, string system, string? members = null)
    {
        var dir = Directory.CreateTempSubdirectory("architecture-edges-").FullName;
        File.WriteAllText(Path.Combine(dir, "members.eventmodel.json"), members ?? Members);
        File.WriteAllText(Path.Combine(dir, "lending.eventmodel.json"), Lending);
        var arch = Path.Combine(dir, "library.architecture.json");
        File.WriteAllText(arch, $$"""
            { "architectureSchemaVersion": "0.2.0", "id": "library", "name": "Library",
              "systems": { "members": { "name": "Members", "document": { "path": "members.eventmodel.json" } },
                           "lending": { "name": "Lending", "document": { "path": "lending.eventmodel.json" } } },
              "edges": [{{edge}}] }
            """);
        var docPath = Path.Combine(dir, system + ".eventmodel.json");
        return ArchitectureEdges.For(arch, system, DocumentLoader.LoadFromFile(docPath), docPath);
    }

    [Fact]
    public void The_edge_into_a_system_is_read_and_its_examples_hold()
    {
        var edges = EdgesOf(Edge, "lending");

        var e = Assert.Single(edges.In);
        Assert.Equal(("RegisterBorrower", "borrower", "memberId"), (e.Command, e.Aggregate, e.TargetField));
        Assert.Empty(edges.Out);
        Assert.True(Assert.Single(edges.Examples).Passed);
    }

    [Fact]
    public void The_sender_publishes_only_mapped_fields_and_the_address_stays()
    {
        var edges = EdgesOf(Edge, "members");

        var o = Assert.Single(edges.Out);
        Assert.Equal("MemberJoined", o.EventType);
        Assert.Equal(["branchId", "memberId"], o.Fields);
        var ev = new Event(1, "ev-1", "member", "m-1", 1, "MemberJoined",
            """{"memberId":"m-1","branchId":"north","email":"reader@example.org"}""", "{}", "2026-11-18T00:00:00Z");
        var m = DotnetCqrs.Edges.Edges.Publish(edges.Out, ev)!;
        Assert.Equal("""{"branchId":"north","memberId":"m-1"}""", m.Data.ToJsonString());
    }

    [Fact]
    public void An_example_that_does_not_hold_is_refused()
    {
        var wrong = Edge.Replace("\"branchId\": \"north\" } } }]", "\"branchId\": \"north\", \"email\": \"reader@example.org\" } } }]");

        var ex = Assert.Throws<DocumentMappingException>(() => EdgesOf(wrong, "lending"));

        Assert.Contains(ex.Report.Errors, e => e.Contains("""the command's fields are {"borrowerId":"m-1","branchId":"north"}"""));
    }

    [Fact]
    public void A_personal_field_on_an_edge_is_refused()
    {
        var pii = Edge.Replace("\"branchId\": \"branchId\" }", "\"branchId\": \"email\" }");

        var ex = Assert.Throws<DocumentMappingException>(() => EdgesOf(pii, "members"));

        Assert.Contains(ex.Report.Errors, e => e.Contains("\"email\" is personal data"));
    }

    /// <summary>Members with member-joined's marker listing the fields it publishes (schema 6.2.0).</summary>
    private static string MembersListing(string fields) => Members
        .Replace("\"public\": { \"version\": 1 }", $"\"public\": {{ \"version\": 1, \"fields\": [{fields}] }}")
        .Replace("\"eventModelingSchemaVersion\": \"6.1.0\"", "\"eventModelingSchemaVersion\": \"6.2.0\"");

    [Fact]
    public void With_a_list_the_sender_publishes_what_its_marker_lists()
    {
        // another view's link may read what this document's edges do not
        var onlyId = Edge.Replace("\"fields\": { \"borrowerId\": \"memberId\", \"branchId\": \"branchId\" }", "\"fields\": { \"borrowerId\": \"memberId\" }")
            .Replace("\"fields\": { \"borrowerId\": \"m-1\", \"branchId\": \"north\" }", "\"fields\": { \"borrowerId\": \"m-1\" }");

        var edges = EdgesOf(onlyId, "members", MembersListing("\"memberId\", \"branchId\""));

        Assert.Equal(["branchId", "memberId"], Assert.Single(edges.Out).Fields);
    }

    [Fact]
    public void An_edge_reading_a_field_its_event_does_not_list_is_refused()
    {
        var ex = Assert.Throws<DocumentMappingException>(() => EdgesOf(Edge, "members", MembersListing("\"memberId\"")));

        Assert.Contains(ex.Report.Errors, e => e.Contains("does not publish \"branchId\""));
    }

    [Fact]
    public void A_listed_personal_field_is_refused()
    {
        var ex = Assert.Throws<DocumentMappingException>(() =>
            EdgesOf(Edge, "members", MembersListing("\"memberId\", \"branchId\", \"email\"")));

        Assert.Contains(ex.Report.Errors, e => e.Contains("\"email\" is personal data"));
    }

    [Fact(Timeout = 120000)]
    public async Task The_generated_tables_compile_and_translate_as_the_example_says()
    {
        var file = EdgesGenerator.Generate(EdgesOf(Edge, "lending"));
        const string program = """
            using System.Text.Json.Nodes;
            using DotnetCqrs.Edges;
            var m = new EdgeMessage("ev-1", "members", "member-joined", 1,
                (JsonObject)JsonNode.Parse("{\"memberId\":\"m-1\",\"branchId\":\"north\"}")!);
            var c = Edges.Find(Generated.SystemEdgeTables.In, m)[0].Translate(m)!;
            Console.Write($"{c.Aggregate} {c.Id} {c.Command.Name} {c.Command.Payload}");
            """;

        var (ok, output) = await InMemoryProgram.RunAsync([file], program);

        Assert.True(ok, output);
        Assert.Equal("""borrower m-1 RegisterBorrower {"borrowerId":"m-1","branchId":"north"}""", output.Trim());
    }
}
