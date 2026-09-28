using System.Runtime.CompilerServices;

namespace DotnetCqrs.Tests;

/// <summary>Several suites launch <c>dotnet build</c>/<c>dotnet run</c> with redirected
/// output and read it to the end. Child processes inherit this process's environment, and
/// MSBuild reads environment variables as properties, so setting them once here covers
/// every launch site -- including <c>ScenarioVerifier</c>'s, which is product code.
///
/// <list type="bullet">
/// <item><c>MSBUILDDISABLENODEREUSE</c>: with node reuse on, <c>dotnet run</c> leaves a
/// reusable build node alive that inherits those pipe handles, so the read only finishes
/// when the node idles out (~10 minutes). The test passes, but only after that wait.</item>
/// <item><c>BuildProjectReferences=false</c>: every scratch project (generated hosts,
/// generated deciders, the verifier's harness, the CLI) references this repo's
/// <c>src/</c> projects, which the test build has already built. Without it each child
/// build walks and re-checks that whole graph in a fresh MSBuild process, and two child
/// builds at once write to the same <c>obj/</c> and <c>bin/</c> folders -- the source of
/// the "file in use" failures.</item>
/// <item><c>Configuration</c>: this assembly's own configuration, so the prebuilt
/// outputs those references resolve to are the ones that exist.</item>
/// </list></summary>
internal static class ChildProcessEnvironment
{
    [ModuleInitializer]
    internal static void Configure()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("BuildProjectReferences", "false");
        Environment.SetEnvironmentVariable("Configuration", TestConfiguration());
    }

    /// <summary>bin/&lt;Configuration&gt;/&lt;TFM&gt;/ -- the directory above the TFM one.</summary>
    private static string TestConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Parent!.Name;
}
