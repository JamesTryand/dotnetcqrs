using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A document the schema refuses is refused naming what to fix, and nothing else. A view scenario's caller without
/// its <c>subjectId</c> (required since 3.8.0) used to come back as hundreds of lines: every node of the validation
/// tree that failed, including alternatives of a oneOf the document never meant and the "false schema" echoes of
/// unevaluatedProperties beside the real problem. Six callers gave 628 lines; now they give six.
/// </summary>
public class SchemaRefusalTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static JsonNode ReadAccess() => JsonNode.Parse(File.ReadAllText(TestDataPath("read-access.json")))!;

    private static JsonArray ViewScenarios(JsonNode doc) => doc["slices"]![3]!["scenarios"]!.AsArray();

    [Fact]
    public void A_caller_without_subjectId_is_refused_naming_each_caller_and_nothing_else()
    {
        var doc = ReadAccess();
        DocumentLoader.Parse(doc.ToJsonString()); // the unbroken example is valid
        foreach (var i in new[] { 0, 2 })
            ViewScenarios(doc)[i]!["when"]!["caller"]!.AsObject().Remove("subjectId");

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(doc.ToJsonString()));

        Assert.Equal(
            [
                new ValidationError("/slices/3/scenarios/0/when/caller", "Required properties [\"subjectId\"] are not present"),
                new ValidationError("/slices/3/scenarios/2/when/caller", "Required properties [\"subjectId\"] are not present"),
            ],
            ex.Errors);
    }

    [Fact]
    public void A_missing_property_is_named_without_the_false_schema_echoes_of_its_siblings()
    {
        var doc = ReadAccess();
        doc["slices"]![3]!.AsObject().Remove("status");

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(doc.ToJsonString()));

        var only = Assert.Single(ex.Errors);
        Assert.Equal("/slices/3", only.InstanceLocation);
        Assert.Contains("status", only.Message);
    }

    [Fact]
    public void An_unknown_key_is_still_named()
    {
        var doc = ReadAccess();
        doc["slices"]![3]!["carrier"] = "a queue";

        var ex = Assert.Throws<DocumentValidationException>(() => DocumentLoader.Parse(doc.ToJsonString()));

        Assert.Contains(ex.Errors, e => e.InstanceLocation == "/slices/3/carrier");
    }
}
