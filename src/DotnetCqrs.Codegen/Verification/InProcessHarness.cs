using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// Compiles the scenario harness together with a document's generated code in memory (Roslyn)
/// and runs it in this process, in a collectible <see cref="AssemblyLoadContext"/> that is
/// unloaded afterwards. It replaces a scratch project built and run with
/// <c>dotnet build</c>/<c>dotnet run</c>, which cost minutes per verification (MSBuild start,
/// restore, a child process) where this costs seconds.
///
/// <para>The compiled code runs against the dotnetcqrs assemblies already loaded here, the
/// ones this tool ships with: the load context resolves nothing itself, so every reference
/// falls through to the default context and the generated types share the library's own.
/// <c>Type.GetType</c> in the harness still finds the generated types by name, because the
/// harness and the generated code are one assembly.</para>
/// </summary>
internal static class InProcessHarness
{
    // What `<ImplicitUsings>enable</ImplicitUsings>` gives a Microsoft.NET.Sdk project: the
    // scratch project had it, and the generated code and the harness rely on it.
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    /// <summary>Compiles <paramref name="sources"/> (name, text) as one console program and runs
    /// its entry point with <paramref name="args"/>, returning when it completes. Throws with
    /// the compiler's errors if it does not compile, or with the program's own exception.</summary>
    public static async Task RunAsync(IEnumerable<(string Name, string Source)> sources, string[] args, CancellationToken ct)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources
            .Append((Name: "ImplicitUsings.g.cs", Source: ImplicitUsings))
            .Select(s => CSharpSyntaxTree.ParseText(s.Source, parseOptions, path: s.Name, cancellationToken: ct))
            .ToList();
        var compilation = CSharpCompilation.Create(
            $"DotnetCqrsVerify_{Guid.NewGuid():N}",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: ct);
        if (!emitted.Success)
            throw new InvalidOperationException("scenario verification harness did not compile:\n"
                + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        image.Position = 0;

        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try
        {
            var entryPoint = context.LoadFromStream(image).EntryPoint
                ?? throw new InvalidOperationException("scenario verification harness has no entry point");
            // Off the caller's thread: the entry point of an async top-level program blocks
            // until the program completes.
            await Task.Run(async () =>
            {
                var returned = entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : [args]);
                if (returned is Task task)
                    await task;
            }, ct);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException($"scenario verification harness failed:\n{ex.InnerException}", ex.InnerException);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>The runtime's own assemblies, plus dotnetcqrs and everything it references, by
    /// the paths they were loaded from in this process: the same assemblies the compiled code
    /// then runs against.</summary>
    private static IReadOnlyList<MetadataReference> LoadReferences()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in platform.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);

        var pending = new Stack<Assembly>([
            typeof(InProcessHarness).Assembly,                            // DotnetCqrs.Codegen
            typeof(DotnetCqrs.EventStore.SqliteEventStore).Assembly,      // DotnetCqrs
            typeof(DotnetCqrs.Deciders.DeciderRegistry).Assembly,         // DotnetCqrs.Abstractions
            typeof(DotnetCqrs.Crypto.InMemoryKmsClient).Assembly,         // DotnetCqrs.Crypto
        ]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var assembly))
        {
            var name = assembly.GetName().Name!;
            if (!seen.Add(name))
                continue;
            if (!string.IsNullOrEmpty(assembly.Location))
                paths[name] = assembly.Location;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    pending.Push(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // Referenced but not deployed (e.g. only used by a code path that never
                    // runs here); the compiler reports it if the harness actually needs it.
                }
            }
        }

        return paths.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }
}
