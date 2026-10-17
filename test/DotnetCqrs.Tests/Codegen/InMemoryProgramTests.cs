using DotnetCqrs.Codegen.Generation;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>The generator tests trust <see cref="InMemoryProgram"/> to report failure, not
/// just success: a program that exits non-zero, code that does not compile, and a program that
/// throws must each come back unsuccessful, with something to read.</summary>
public class InMemoryProgramTests
{
    private static readonly GeneratedFile[] NoFiles = [];

    [Fact]
    public async Task A_program_that_prints_and_exits_zero_succeeds_with_its_output()
    {
        var (success, output) = await InMemoryProgram.RunAsync(NoFiles, """Console.WriteLine("PASS"); return 0;""");

        Assert.True(success, output);
        Assert.Contains("PASS", output);
    }

    [Fact]
    public async Task A_program_that_exits_non_zero_fails()
    {
        var (success, output) = await InMemoryProgram.RunAsync(NoFiles, """Console.WriteLine("FAIL: nope"); return 1;""");

        Assert.False(success);
        Assert.Contains("FAIL: nope", output);
    }

    [Fact]
    public async Task A_program_that_throws_fails_with_the_exception()
    {
        var (success, output) = await InMemoryProgram.RunAsync(NoFiles, """throw new InvalidTimeZoneException("boom");""");

        Assert.False(success);
        Assert.Contains("boom", output);
    }

    [Fact]
    public async Task Code_that_does_not_compile_fails_with_the_compilers_errors()
    {
        var (runSuccess, runOutput) = await InMemoryProgram.RunAsync(NoFiles, "this is not C#;");
        var (compileSuccess, compileOutput) = InMemoryProgram.Compile([new GeneratedFile("Broken.cs", "class Broken { int x = \"no\"; }")]);

        Assert.False(runSuccess);
        Assert.Contains("error CS", runOutput);
        Assert.False(compileSuccess);
        Assert.Contains("error CS", compileOutput);
    }

    // A run's output is only what its own program wrote. The console is one per process and tests run in
    // parallel: RowsPerListItemTests failed under load with another test host's "Now listening" lines in what it
    // had captured, because the capture took the whole process's console for the length of the run.
    [Fact(Timeout = 120000)]
    public async Task Another_tests_console_output_is_not_captured()
    {
        const string program = """
            for (var i = 0; i < 5; i++)
            {
                Console.WriteLine($"program line {i}");
                await Task.Delay(20);
            }
            return 0;
            """;
        using var done = new CancellationTokenSource();
        var intruder = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                Console.WriteLine("another test's line");
                await Task.Delay(1);
            }
        });

        var (success, output) = await InMemoryProgram.RunAsync(NoFiles, program);
        await done.CancelAsync();
        await intruder;

        Assert.True(success, output);
        Assert.DoesNotContain("another test's line", output);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"program line {i}"),
            output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }
}
