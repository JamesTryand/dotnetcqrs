using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

// Hand-rolled, not System.CommandLine: one subcommand, three flags (one repeatable) is
// small enough that a dependency buys nothing here -- see README.md's "CLI" section,
// which left this decision open on purpose.
if (args.Length == 0 || args[0] != "generate")
{
    Console.Error.WriteLine("usage: dotnetcqrs-codegen generate --input <path> --output <dir> [--aggregate-override <id>=<aggregate>]...");
    return 1;
}

string? input = null;
string? output = null;
var overrides = new Dictionary<string, string>();

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--input":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--input requires a value"); return 1; }
            input = args[++i];
            break;
        case "--output":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--output requires a value"); return 1; }
            output = args[++i];
            break;
        case "--aggregate-override":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--aggregate-override requires a value"); return 1; }
            var raw = args[++i];
            var parts = raw.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                Console.Error.WriteLine($"--aggregate-override must be <id>=<aggregate>, got \"{raw}\"");
                return 1;
            }
            overrides[parts[0]] = parts[1];
            break;
        default:
            Console.Error.WriteLine($"unrecognized argument: {args[i]}");
            return 1;
    }
}

if (input is null || output is null)
{
    Console.Error.WriteLine("--input and --output are required");
    return 1;
}

try
{
    var document = DocumentLoader.LoadFromFile(input);
    var result = DocumentMapper.Map(document, new MappingOptions { AggregateOverrides = overrides });

    // Warnings are decisions taken on the document's behalf, not failures -- every
    // milestone so far has printed and continued, never refused, on a warning alone.
    foreach (var warning in result.Report.Warnings)
        Console.WriteLine($"warning: {warning}");

    Directory.CreateDirectory(output);
    var written = 0;
    foreach (var domain in result.Domains)
        foreach (var file in CSharpGenerator.Generate(domain))
        {
            File.WriteAllText(Path.Combine(output, file.Name), file.Source);
            written++;
        }

    Console.WriteLine($"generated {written} file(s) to {output}");
    return 0;
}
catch (DocumentValidationException ex)
{
    foreach (var error in ex.Errors)
        Console.Error.WriteLine($"{error.InstanceLocation}: {error.Message}");
    return 1;
}
catch (DocumentMappingException ex)
{
    foreach (var error in ex.Report.Errors)
        Console.Error.WriteLine(error);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
