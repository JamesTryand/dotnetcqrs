using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 6.2.0: a scenario whose <c>then</c> lists no events (accepted, nothing recorded), a system's
/// <c>expects</c>, and the fields a public event publishes. The documents are the schema's own two-system example
/// (<c>examples/links/</c>), as pocketcqrs's schema620_test.go reads them, so the two generators are held to one
/// meaning.
/// </summary>
public class Schema620Tests
{
    private static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", name)))!;

    private static JsonObject RegisterSlice(JsonNode lending) =>
        lending["slices"]!.AsArray().Select(s => s!.AsObject()).Single(s => (string?)s["id"] == "register");

    [Fact]
    public async Task An_empty_then_passes_only_when_the_command_is_accepted_and_records_nothing()
    {
        var lending = Fixture("links-lending.json");
        var scenarios = RegisterSlice(lending)["scenarios"]!.AsArray();
        var nothing = scenarios.Single(s => (string?)s!["id"] == "the-reading-room-lends-nothing")!;
        var records = nothing.DeepClone();
        records["id"] = "records-but-says-nothing";
        records["when"]!["data"]!["branchId"] = "north";
        scenarios.Add(records);
        var doc = DocumentLoader.Parse(lending.ToJsonString());

        var results = (await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc))).ToDictionary(r => r.ScenarioId);

        Assert.True(results["the-reading-room-lends-nothing"].Passed, results["the-reading-room-lends-nothing"].Detail);
        Assert.Equal("accepted, nothing recorded", results["the-reading-room-lends-nothing"].Detail);
        Assert.False(results["records-but-says-nothing"].Passed);
        Assert.Contains("BorrowerRegistered", results["records-but-says-nothing"].Detail);
    }

    [Fact]
    public void A_then_without_events_is_refused()
    {
        var lending = Fixture("links-lending.json");
        var nothing = RegisterSlice(lending)["scenarios"]!.AsArray().Single(s => (string?)s!["id"] == "the-reading-room-lends-nothing")!;
        nothing["then"] = new JsonObject();

        Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(lending.ToJsonString()));
    }

    [Fact]
    public void Expects_loads_and_generates_what_the_document_would_without_it()
    {
        var lending = Fixture("links-lending.json");
        var without = lending.DeepClone();
        without.AsObject().Remove("expects");

        var mapped = DocumentMapper.Map(DocumentLoader.Parse(lending.ToJsonString()));
        var plain = DocumentMapper.Map(DocumentLoader.Parse(without.ToJsonString()));

        Assert.Equal(plain.Domains.SelectMany(CSharpGenerator.Generate), mapped.Domains.SelectMany(CSharpGenerator.Generate));
    }

    [Fact]
    public void A_system_expecting_from_itself_is_refused()
    {
        var lending = Fixture("links-lending.json");
        var expects = lending["expects"]!.AsObject();
        var other = expects["library-members"]!.DeepClone();
        expects["library-lending"] = other;
        var doc = DocumentLoader.Parse(lending.ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(ex.Report.Errors, e => e.Contains("expects from itself"));
    }

    [Fact]
    public void A_public_event_lists_its_fields_and_each_must_be_its_own()
    {
        var members = Fixture("links-members.json");
        var doc = DocumentLoader.Parse(members.ToJsonString());
        Assert.Equal(["memberId", "branchId"], doc.Events!["member-joined"].Public!.Fields);
        DocumentMapper.Map(doc);

        members["events"]!["member-joined"]!["public"]!["fields"]!.AsArray().Add("colour");
        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(DocumentLoader.Parse(members.ToJsonString())));
        Assert.Contains(ex.Report.Errors, e => e.Contains("\"colour\""));
    }

    [Fact]
    public void A_commands_marker_lists_no_fields()
    {
        var lending = Fixture("links-lending.json");
        lending["commands"]!["register-borrower"]!["public"]!["fields"] = new JsonArray("borrowerId");

        Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(lending.ToJsonString()));
    }

    [Fact]
    public async Task The_links_examples_load_and_verify()
    {
        foreach (var name in new[] { "links-lending.json", "links-members.json" })
        {
            var doc = DocumentLoader.Parse(Fixture(name).ToJsonString());
            foreach (var r in await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc)))
                Assert.True(r.Passed, $"{name}: {r.ScenarioId}: {r.Detail}");
        }
    }
}
