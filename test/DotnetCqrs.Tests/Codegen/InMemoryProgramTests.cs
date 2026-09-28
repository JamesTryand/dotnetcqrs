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
}
