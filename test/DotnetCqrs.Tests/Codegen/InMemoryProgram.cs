using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>Compiles generated code in memory and, with a test program, runs it in this
/// process (<see cref="InProcessHarness"/>, the same path <c>verify</c> uses), instead of a
/// scratch project built and run with <c>dotnet build</c>/<c>dotnet run</c>: seconds rather
/// than minutes. The generated code is compiled against the real dotnetcqrs assemblies this
/// test process has loaded, so it proves the same thing: the text compiles against the actual
/// library and, for the wiring a test checks, runs correctly.</summary>
internal static class InMemoryProgram
{
    // Test programs report through the console, which is one per process: runs take turns.
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    /// <summary>Compiles <paramref name="files"/> as a library. Success, or the compiler's
    /// errors.</summary>
    public static (bool Success, string Output) Compile(IEnumerable<GeneratedFile> files)
    {
        var errors = InProcessHarness.CompileErrors(files.Select(f => (f.Name, f.Source)));
        return (errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>Compiles <paramref name="files"/> with <paramref name="programCs"/> as the
    /// program and runs it. Success means it compiled and exited 0; the output is what it wrote
    /// to the console, or the compiler's errors, or the exception it threw.</summary>
    public static async Task<(bool Success, string Output)> RunAsync(IEnumerable<GeneratedFile> files, string programCs)
    {
        var sources = files.Select(f => (f.Name, f.Source)).Append(("Program.cs", programCs)).ToList();
        await ConsoleGate.WaitAsync();
        var (stdout, stderr) = (Console.Out, Console.Error);
        var text = new StringWriter();
        var captured = TextWriter.Synchronized(text);
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            var exitCode = await InProcessHarness.RunAsync(sources, [], CancellationToken.None);
            return (exitCode == 0, text.ToString());
        }
        catch (InvalidOperationException ex)
        {
            return (false, text + Environment.NewLine + ex.Message);
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            ConsoleGate.Release();
        }
    }
}
