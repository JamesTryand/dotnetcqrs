using System.Text;
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
    // Test programs report through the console, which is one per process, while other tests (a host's
    // "Now listening", say) write to it too. The console is routed once, for good: a write goes to the
    // capture of the run whose flow it comes from, and anything else to the real console. A run's own
    // program runs inside that flow (InProcessHarness awaits it), so its output, and only its, is captured.
    private static readonly AsyncLocal<TextWriter?> Capture = new();
    private static readonly Lazy<bool> Routed = new(() =>
    {
        Console.SetOut(new RoutingWriter(Console.Out));
        Console.SetError(new RoutingWriter(Console.Error));
        return true;
    });

    // Runs still take turns, as they did when the console was swapped per run: each compiles and loads an
    // assembly, and running them all at once only slows a small machine down.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

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
        _ = Routed.Value;
        await RunGate.WaitAsync();
        var text = new StringWriter();
        Capture.Value = TextWriter.Synchronized(text);
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
            Capture.Value = null;
            RunGate.Release();
        }
    }

    private sealed class RoutingWriter(TextWriter console) : TextWriter
    {
        private TextWriter Target => Capture.Value ?? console;
        public override Encoding Encoding => console.Encoding;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
        public override void WriteLine(string? value) => Target.WriteLine(value);
        public override void Flush() => Target.Flush();
    }
}
