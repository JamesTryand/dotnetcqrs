using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 4.2.0 <c>public</c>: an event or command marked part of its system's public contract, at a version, so
/// another system may use it through a coordinating document (eventmodelschema's architecture.schema.json). It says
/// nothing a single host acts on, so a document carrying it loads and generates exactly what it does without it. The
/// schema still refuses a malformed marker or a key beside it.
/// </summary>
public class PublicMarkerTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static JsonNode Example() => JsonNode.Parse(File.ReadAllText(TestDataPath("state-ownership.json")))!;

    private static JsonNode Marked(JsonNode? marker = null)
    {
        var node = Example();
        node["eventModelingSchemaVersion"] = "4.2.0";
        node["events"]!["loan-requested"]!["public"] = marker ?? new JsonObject { ["version"] = 1 };
        node["commands"]!["request-loan"]!["public"] = new JsonObject { ["version"] = 2 };
        return node;
    }

    private static List<GeneratedFile> Generate(JsonNode node) =>
        DocumentMapper.Map(DocumentLoader.Parse(node.ToJsonString())).Domains.SelectMany(CSharpGenerator.Generate).ToList();

    [Fact]
    public void A_public_event_and_command_load_and_generate_what_they_would_without_the_marker()
    {
        var plain = Example();
        plain["eventModelingSchemaVersion"] = "4.2.0";

        var marked = Generate(Marked());

        Assert.NotEmpty(marked);
        Assert.Equal(Generate(plain), marked);
    }

    [Fact]
    public void A_key_beside_the_version_is_refused()
    {
        // The edge between systems never names its carrier, and nor does the marker.
        var ex = Assert.Throws<DocumentValidationException>(() =>
            DocumentLoader.Parse(Marked(new JsonObject { ["version"] = 1, ["topic"] = "loans" }).ToJsonString()));
        Assert.Contains(ex.Errors, e => e.InstanceLocation.Contains("/events/loan-requested/public"));
    }

    [Fact]
    public void A_marker_without_a_version_or_with_version_0_is_refused()
    {
        Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(Marked(new JsonObject()).ToJsonString()));
        Assert.Throws<DocumentValidationException>(() =>
            DocumentLoader.Parse(Marked(new JsonObject { ["version"] = 0 }).ToJsonString()));
    }
}
