using System.Runtime.CompilerServices;

namespace DotnetCqrs.Tests;

/// <summary>Several suites launch <c>dotnet build</c>/<c>dotnet run</c> with redirected
/// output and read it to the end. With MSBuild node reuse on, <c>dotnet run</c> leaves a
/// reusable build node alive that inherits those pipe handles, so the read only finishes
/// when the node idles out (~10 minutes). The test passes, but only after that wait. Child
/// processes inherit this process's environment, so disabling reuse once here covers
/// every launch site.</summary>
internal static class ChildProcessEnvironment
{
    [ModuleInitializer]
    internal static void DisableMsBuildNodeReuse() =>
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
}
