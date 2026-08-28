using System.Diagnostics;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves the `generate` command for real -- shells out to `dotnet run` against the
/// actual CLI project (same approach as CSharpGeneratorTests/ScenarioVerifierTests use
/// for the library itself), checks the exit code, and compiles the files it wrote
/// against the real DotnetCqrs library. A CLI's only real deliverable is "the process
/// exits right and the files it wrote are real code", not "the C# didn't throw".
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _scratchDir;

    public CliTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-codegen-cli-{Guid.NewGuid():N}");
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
    /// (marked by dotnetcqrs.slnx) -- same approach as CSharpGeneratorTests.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private static string CliProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs.Codegen.Cli", "DotnetCqrs.Codegen.Cli.csproj");
    private static string DotnetCqrsProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj");

    private static async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(CliProjectPath());
        psi.ArgumentList.Add("--");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }

    [Fact(Timeout = 120000)]
    public async Task Generate_writes_compilable_files_for_the_order_fulfillment_document()
    {
        var (exitCode, output) = await RunCliAsync(
            "generate",
            "--input", TestDataPath("order-fulfillment.json"),
            "--output", _scratchDir,
            "--aggregate-override", "notify-shipping-partner=ShippingNotification");

        Assert.True(exitCode == 0, $"CLI exited {exitCode}:\n{output}");

        var writtenNames = Directory.GetFiles(_scratchDir).Select(Path.GetFileName).ToList();
        Assert.Contains("OrderDecider.cs", writtenNames);
        Assert.Contains("ShippingNotificationDecider.cs", writtenNames);

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>Library</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{DotnetCqrsProjectPath()}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Scratch.csproj"), csproj);

        var buildPsi = new ProcessStartInfo("dotnet", $"build \"{_scratchDir}\" -v quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var build = Process.Start(buildPsi)!;
        var buildOutput = await build.StandardOutput.ReadToEndAsync() + await build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync();
        Assert.True(build.ExitCode == 0, $"generated files did not compile:\n{buildOutput}");
    }

    [Fact(Timeout = 60000)]
    public async Task Generate_reports_a_missing_aggregate_override_as_a_readable_error_not_a_stack_trace()
    {
        // notify-shipping-partner has no `aggregate` tag in the document and no
        // override is supplied here -- DocumentMapper.Map throws
        // DocumentMappingException, and the CLI's whole job is printing its
        // Report.Errors one per line, not letting the exception surface raw.
        var (exitCode, output) = await RunCliAsync(
            "generate",
            "--input", TestDataPath("order-fulfillment.json"),
            "--output", _scratchDir);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("at DotnetCqrs.Codegen", output);
        Assert.Contains("notify-shipping-partner", output);
    }

    [Fact(Timeout = 60000)]
    public async Task Generate_requires_input_and_output_flags()
    {
        var (exitCode, output) = await RunCliAsync("generate");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--input", output);
    }

    /// <summary>Same document ScenarioVerifierTests proves the library against: one
    /// scenario per slice, and "view-order-summary-after-placement" is EXPECTED to
    /// fail (see that test's own comment -- "status" isn't literally carried by any
    /// event's JSON, so the generic field-merge projection can't produce it without an
    /// author writing the real rule). Milestone 7's whole point is reporting that
    /// failure, not hiding it, so a non-zero exit here IS the passing assertion.</summary>
    [Fact(Timeout = 120000)]
    public async Task Verify_prints_every_scenario_result_and_exits_non_zero_on_a_real_failure()
    {
        var (exitCode, output) = await RunCliAsync(
            "verify",
            "--input", TestDataPath("order-fulfillment.json"),
            "--dotnetcqrs-project", DotnetCqrsProjectPath(),
            "--aggregate-override", "notify-shipping-partner=ShippingNotification");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("PASS [stateChange]", output);
        Assert.Contains("place-order-slice/place-order-happy-path", output);
        Assert.Contains("FAIL [stateView]", output);
        Assert.Contains("view-order-summary-after-placement", output);
        Assert.DoesNotContain("at DotnetCqrs.Codegen", output);
    }

    [Fact(Timeout = 120000)]
    public async Task Verify_exits_zero_when_every_scenario_passes()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "error-test", "name": "Error Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [
                  {
                    "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                    "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                  },
                  {
                    "id": "duplicate-rejected", "name": "A second PlaceOrder is refused", "kind": "error",
                    "given": [{"eventId": "order-placed"}], "when": {"commandId": "place-order"},
                    "then": {"error": {"message": "order already exists"}}
                  }
                ]
              }]
            }
            """;
        var inputPath = Path.Combine(_scratchDir, "error-test.json");
        await File.WriteAllTextAsync(inputPath, json);

        var (exitCode, output) = await RunCliAsync(
            "verify",
            "--input", inputPath,
            "--dotnetcqrs-project", DotnetCqrsProjectPath(),
            "--aggregate-override", "place-order=Order");

        Assert.Equal(0, exitCode);
        Assert.Contains("PASS [stateChange]", output);
        Assert.Contains("PASS [error]", output);
    }

    [Fact(Timeout = 60000)]
    public async Task Verify_requires_input_and_dotnetcqrs_project_flags()
    {
        var (exitCode, output) = await RunCliAsync("verify", "--input", TestDataPath("order-fulfillment.json"));

        Assert.NotEqual(0, exitCode);
        Assert.Contains("--dotnetcqrs-project", output);
    }
}
