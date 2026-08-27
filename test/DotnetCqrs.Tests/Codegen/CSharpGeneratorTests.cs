using System.Diagnostics;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves the generator's output is real, compilable C# against the actual
/// <c>DotnetCqrs</c> library -- not just "no exception while building the string". A
/// generator's only real deliverable is text that compiles (and, for the wiring it
/// claims to get right, runs correctly), so this shells out to a real
/// <c>dotnet build</c>/<c>dotnet run</c> against a scratch project rather than
/// asserting anything about the generated text itself.
/// </summary>
public class CSharpGeneratorTests : IDisposable
{
    private readonly string _scratchDir;

    public CSharpGeneratorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-codegen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
            Directory.Delete(_scratchDir, recursive: true);
    }

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    /// <summary>Walks up from the test's own output directory to find the repo root
    /// (marked by dotnetcqrs.slnx), rather than a fixed number of parent hops that
    /// would silently break if the build output path ever changes shape.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private async Task<(bool Success, string Output)> BuildAsync(IEnumerable<GeneratedFile> files, string? programCs = null)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>{(programCs is null ? "Library" : "Exe")}</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj")}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Scratch.csproj"), csproj);

        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, file.Name), file.Source);

        if (programCs is not null)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Program.cs"), programCs);

        var arguments = programCs is null ? $"build \"{_scratchDir}\" -v quiet" : $"run --project \"{_scratchDir}\"";
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, stdout + stderr);
    }

    [Fact]
    public async Task Generated_code_for_the_full_order_fulfillment_domain_compiles()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var result = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });

        var files = result.Domains.SelectMany(CSharpGenerator.Generate).ToList();
        Assert.NotEmpty(files); // sanity: the generator actually produced something

        var (success, output) = await BuildAsync(files);
        Assert.True(success, $"generated code did not compile:\n{output}");
    }

    [Fact]
    public async Task Generated_decider_decides_and_evolves_correctly_at_runtime()
    {
        // A minimal document with an explicit create scenario (empty `given`) so
        // IsCreate marks PlaceOrder a create (Once=true, RequiresExisting=false) --
        // minimal.json itself declares no scenarios at all, so its PlaceOrder is
        // faithfully mapped as requiring an EXISTING stream instead (see
        // DocumentMapperTests' own note on this same distinction). No fields on
        // either the command or the event, so this proves the WIRING (registration,
        // command dispatch, the create-guard, event append, stream contents) in
        // isolation from field-shape concerns, which are a document-content question,
        // not a generator correctness one.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "runtime-test", "name": "Runtime Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [{
                  "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                  "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                }]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var result = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });
        var domain = Assert.Single(result.Domains);
        var files = CSharpGenerator.Generate(domain);

        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Order;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(OrderDecider.Aggregate, OrderDecider.Create());

            await registry.HandleAsync("order", "o1", new Command("PlaceOrder", "{}"));
            var stream = await store.LoadStreamAsync("order", "o1");

            if (stream.Count != 1 || stream[0].Type != OrderEvents.OrderPlaced)
            {
                Console.WriteLine($"FAIL: stream = [{string.Join(",", stream.Select(e => e.Type))}]");
                return 1;
            }

            try
            {
                // the create-guard should also be real, not just present in source
                await registry.HandleAsync("order", "o1", new Command("PlaceOrder", "{}"));
                Console.WriteLine("FAIL: expected rejection of a duplicate PlaceOrder");
                return 1;
            }
            catch (InvalidOperationException)
            {
                // expected
            }

            Console.WriteLine("PASS");
            return 0;
            """;

        var (success, output) = await BuildAsync(files, programCs);
        Assert.True(success, $"generated decider did not build/run correctly:\n{output}");
        Assert.Contains("PASS", output);
    }
}
