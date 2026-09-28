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
    /// its entry point with <paramref name="args"/>, returning its exit code when it completes
    /// (0 for a program that returns nothing). Throws with the compiler's errors if it does not
    /// compile, or with the program's own exception.</summary>
    public static async Task<int> RunAsync(IEnumerable<(string Name, string Source)> sources, string[] args, CancellationToken ct)
    {
        var compilation = Compile(sources, OutputKind.ConsoleApplication, ct);
        var errors = Errors(compilation);
        using var image = new MemoryStream();
        var emitted = errors.Count == 0 ? compilation.Emit(image, cancellationToken: ct) : null;
        if (emitted is null || !emitted.Success)
            throw new InvalidOperationException("scenario verification harness did not compile:" + Environment.NewLine
                + string.Join(Environment.NewLine, emitted is null ? errors : emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        image.Position = 0;

        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        try
        {
            var entryPoint = context.LoadFromStream(image).EntryPoint
                ?? throw new InvalidOperationException("scenario verification harness has no entry point");
            // Off the caller's thread: the entry point of an async top-level program blocks
            // until the program completes.
            return await Task.Run(async () =>
            {
                var returned = entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : [args]);
                return returned switch
                {
                    Task<int> exit => await exit,
                    Task task => await CompletedAsZero(task),
                    int exit => exit,
                    _ => 0,
                };
            }, ct);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("scenario verification harness failed:" + Environment.NewLine + ex.InnerException, ex.InnerException);
        }
        finally
        {
            context.Unload();
        }
    }

    private static async Task<int> CompletedAsZero(Task task)
    {
        await task;
        return 0;
    }

    /// <summary>Compiles <paramref name="sources"/> as a library and returns the compiler's
    /// errors, formatted; empty when it compiles. Nothing is loaded or run.</summary>
    public static IReadOnlyList<string> CompileErrors(IEnumerable<(string Name, string Source)> sources) =>
        Errors(Compile(sources, OutputKind.DynamicallyLinkedLibrary, CancellationToken.None));

    private static CSharpCompilation Compile(IEnumerable<(string Name, string Source)> sources, OutputKind kind, CancellationToken ct)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources
            .Append((Name: "ImplicitUsings.g.cs", Source: ImplicitUsings))
            .Select(s => CSharpSyntaxTree.ParseText(s.Source, parseOptions, path: s.Name, cancellationToken: ct))
            .ToList();
        return CSharpCompilation.Create(
            $"DotnetCqrsVerify_{Guid.NewGuid():N}",
            trees,
            References.Value,
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static IReadOnlyList<string> Errors(CSharpCompilation compilation) =>
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();

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
