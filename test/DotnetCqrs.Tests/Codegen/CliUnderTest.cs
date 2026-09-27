using System.Diagnostics;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>The codegen CLI, built once per test run and then run from its DLL. Running it
/// with <c>dotnet run --project</c> on every call re-evaluated, restored and up-to-date
/// checked the project each time. It builds in place (its own <c>bin/</c>), so later runs
/// are incremental; building with <c>-o</c> elsewhere breaks the SDK's deps-file step when
/// project references are not rebuilt (ChildProcessEnvironment).</summary>
internal static class CliUnderTest
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _dll;

    /// <summary>The CLI's DLL: pass it to <c>dotnet</c>, followed by the CLI's own
    /// arguments.</summary>
    public static async Task<string> DllAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_dll is not null)
                return _dll;

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var project = ProjectPath();
            foreach (var arg in new[] { "build", project, "-v", "quiet", "-nologo" })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"building the codegen CLI failed (exit {process.ExitCode}):\n{await stdout}\n{await stderr}");

            // Same configuration as this test assembly (ChildProcessEnvironment sets it).
            var configuration = Environment.GetEnvironmentVariable("Configuration") ?? "Debug";
            return _dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, "net10.0", "DotnetCqrs.Codegen.Cli.dll");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Walks up from the test's own output directory to the repo root (marked by
    /// dotnetcqrs.slnx), as CliTests and HostGenerationTests do.</summary>
    private static string ProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName
            ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
        return Path.Combine(root, "src", "DotnetCqrs.Codegen.Cli", "DotnetCqrs.Codegen.Cli.csproj");
    }
}

/// <summary>Builds the CLI before a class's first test, so that one-time build does not count
/// against any single test's timeout.</summary>
public sealed class CliUnderTestFixture
{
    public CliUnderTestFixture() => CliUnderTest.DllAsync().GetAwaiter().GetResult();
}
