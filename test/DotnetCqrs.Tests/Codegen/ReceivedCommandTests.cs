using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 6.1.0: a stateChange slice may name neither a screen nor an ingress when its command is public: another
/// system sends it, over a coordinating document's internal edge (hub QUESTIONS L14). Its command is generated as any
/// other's and the report says where it comes from; neither, with a command that is not public, is refused.
/// </summary>
public class ReceivedCommandTests
{
    private static JsonNode Received(bool isPublic)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "state-ownership.json")))!;
        node["eventModelingSchemaVersion"] = "6.1.0";
        foreach (var slice in node["slices"]!.AsArray())
            if ((string?)slice!["id"] == "request-loan-slice")
                slice.AsObject().Remove("screenId");
        if (isPublic)
            node["commands"]!["request-loan"]!["public"] = new JsonObject { ["version"] = 1 };
        return node;
    }

    [Fact]
    public void A_public_command_with_neither_a_screen_nor_an_ingress_is_generated_and_named_as_received()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(Received(isPublic: true).ToJsonString()));

        Assert.Contains("RequestLoan", mapped.Domains.SelectMany(d => d.Commands).Select(c => c.Name));
        Assert.Contains(mapped.Report.Warnings, w => w.Contains("slice \"request-loan-slice\": request-loan is sent by another system"));
    }

    [Fact]
    public void A_command_nothing_starts_is_refused()
    {
        var doc = DocumentLoader.Parse(Received(isPublic: false).ToJsonString());

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(ex.Report.Errors, e => e.Contains("has no screenId or ingressId, and its command \"request-loan\" is not public"));
    }
}
