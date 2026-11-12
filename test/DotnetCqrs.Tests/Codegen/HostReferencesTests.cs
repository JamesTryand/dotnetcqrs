using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// A generated host lives in its own repository and depends on dotnetcqrs as packages, never its source: by default
/// its project references the dotnetcqrs packages at the generator's own version, and a tool manifest pins the
/// generator at that same version. Only when given a checkout does it reference the checkout's projects, for trying
/// an unreleased version (and for this repository's own tests, which build against the code under test).
/// </summary>
public class HostReferencesTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static IReadOnlyList<GeneratedFile> Generate(string? checkout, string? version = null)
    {
        var overrides = new Dictionary<string, string>();
        var doc = DocumentLoader.LoadFromFile(TestDataPath("read-access.json"));
        var mapped = DocumentMapper.Map(doc, new MappingOptions { AggregateOverrides = overrides });
        return HostProjectGenerator.Generate(doc, mapped, "Minimal", checkout, overrides, version);
    }

    [Fact]
    public void By_default_the_project_references_the_packages_at_the_generators_own_version()
    {
        var files = Generate(checkout: null);
        var csproj = files.Single(f => f.Name == "Minimal.csproj").Source;
        var version = HostProjectGenerator.GeneratorVersion;

        Assert.DoesNotContain("+", version);
        foreach (var id in HostProjectGenerator.PackageIds)
            Assert.Contains($"<PackageReference Include=\"{id}\" Version=\"{version}\" />", csproj);
        Assert.Equal(6, HostProjectGenerator.PackageIds.Count);
        Assert.DoesNotContain("ProjectReference", csproj);
        Assert.DoesNotContain(".csproj\"", csproj);
    }

    [Fact]
    public void By_default_a_tool_manifest_pins_the_generator_at_the_same_version()
    {
        var files = Generate(checkout: null, version: "9.8.7");
        Assert.Contains("Version=\"9.8.7\"", files.Single(f => f.Name == "Minimal.csproj").Source);

        var manifest = JsonNode.Parse(files.Single(f => f.Name == HostProjectGenerator.ToolManifestPath).Source)!;
        var tool = manifest["tools"]![HostProjectGenerator.ToolPackageId]!;
        Assert.Equal("9.8.7", (string?)tool["version"]);
        Assert.Equal("dotnetcqrs-codegen", (string?)tool["commands"]![0]);
        Assert.True((bool)manifest["isRoot"]!);
    }

    [Fact]
    public void Given_a_checkout_it_references_the_checkouts_projects_and_writes_no_manifest()
    {
        var checkout = Path.Combine("repo", "src", "DotnetCqrs", "DotnetCqrs.csproj");
        var files = Generate(checkout);
        var csproj = files.Single(f => f.Name == "Minimal.csproj").Source;

        Assert.Contains($"<ProjectReference Include=\"{checkout}\" />", csproj);
        Assert.Contains(Path.Combine("repo", "src", "DotnetCqrs.Host", "DotnetCqrs.Host.csproj"), csproj);
        Assert.Equal(6, csproj.Split("<ProjectReference").Length - 1);
        Assert.DoesNotContain("PackageReference", csproj);
        Assert.DoesNotContain(files, f => f.Name == HostProjectGenerator.ToolManifestPath);
    }

    [Fact]
    public void The_manifest_found_is_the_nearest_at_or_above_the_project_as_dotnet_tool_finds_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-manifest-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "hosts", "Orders");
        Directory.CreateDirectory(project);
        try
        {
            var top = Path.Combine(root, "dotnet-tools.json");
            File.WriteAllText(top, "{}");
            Assert.Equal(top, HostProjectGenerator.FindToolManifest(project));

            var nearer = Path.Combine(root, "hosts", ".config", "dotnet-tools.json");
            Directory.CreateDirectory(Path.GetDirectoryName(nearer)!);
            File.WriteAllText(nearer, "{}");
            Assert.Equal(nearer, HostProjectGenerator.FindToolManifest(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_existing_manifest_keeps_its_other_tools_and_only_this_tools_entry_changes()
    {
        const string existing = """
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-ef": { "version": "10.0.0", "commands": ["dotnet-ef"], "rollForward": false },
                "jamestryand.dotnetcqrs.codegen.cli": { "version": "0.1.0", "commands": ["dotnetcqrs-codegen"], "rollForward": false }
              }
            }
            """;
        var merged = JsonNode.Parse(HostProjectGenerator.ToolManifest(existing, "0.37.0"))!;

        Assert.Equal("10.0.0", (string?)merged["tools"]!["dotnet-ef"]!["version"]);
        Assert.Equal("0.37.0", (string?)merged["tools"]![HostProjectGenerator.ToolPackageId]!["version"]);
    }
}
