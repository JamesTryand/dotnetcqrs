using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

// Hand-rolled, not System.CommandLine: two subcommands, six flags (one repeatable,
// one verify-only, two host-only) is still small enough that a dependency buys nothing
// here -- see README.md's Milestone 7 note, which reconfirmed Milestone 6's original
// call; Milestone 9 rechecked it again when adding --host/--project-name.
if (args.Length == 0 || (args[0] != "generate" && args[0] != "verify"))
{
    Console.Error.WriteLine("usage: dotnetcqrs-codegen generate --input <path> --output <dir> [--host --dotnetcqrs-project <path> [--project-name <name>]] [--aggregate-override <id>=<aggregate>]...");
    Console.Error.WriteLine("       dotnetcqrs-codegen verify --input <path> --dotnetcqrs-project <path> [--aggregate-override <id>=<aggregate>]...");
    return 1;
}

var command = args[0];
string? input = null;
string? output = null;
string? dotnetCqrsProject = null;
string? projectName = null;
var host = false;
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
        case "--dotnetcqrs-project":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--dotnetcqrs-project requires a value"); return 1; }
            dotnetCqrsProject = args[++i];
            break;
        case "--host":
            host = true;
            break;
        case "--project-name":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--project-name requires a value"); return 1; }
            projectName = args[++i];
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

if (command == "generate" && (input is null || output is null))
{
    Console.Error.WriteLine("--input and --output are required");
    return 1;
}
if (command == "generate" && host && dotnetCqrsProject is null)
{
    Console.Error.WriteLine("--host requires --dotnetcqrs-project");
    return 1;
}
if (command == "verify" && (input is null || dotnetCqrsProject is null))
{
    Console.Error.WriteLine("--input and --dotnetcqrs-project are required");
    return 1;
}

try
{
    var document = DocumentLoader.LoadFromFile(input!);
    var result = DocumentMapper.Map(document, new MappingOptions { AggregateOverrides = overrides });

    // Warnings are decisions taken on the document's behalf, not failures -- every
    // milestone so far has printed and continued, never refused, on a warning alone.
    foreach (var warning in result.Report.Warnings)
        Console.WriteLine($"warning: {warning}");

    if (command == "generate")
    {
        Directory.CreateDirectory(output!);
        var written = 0;

        if (host)
        {
            var name = projectName ?? Names.TypeName(document.Name, document.Id);
            foreach (var file in HostProjectGenerator.Generate(document, result, name, dotnetCqrsProject!, overrides))
            {
                File.WriteAllText(Path.Combine(output!, file.Name), file.Source);
                written++;
            }
            File.Copy(input!, Path.Combine(output!, "document.json"), overwrite: true);
            written++;

            Console.WriteLine($"generated {written} file(s) to {output}");
            return 0;
        }

        foreach (var domain in result.Domains)
            foreach (var file in CSharpGenerator.Generate(domain))
            {
                File.WriteAllText(Path.Combine(output!, file.Name), file.Source);
                written++;
            }

        Console.WriteLine($"generated {written} file(s) to {output}");
        return 0;
    }

    var scenarioResults = await ScenarioVerifier.VerifyAsync(document, result, dotnetCqrsProject!);
    return ScenarioReport.Print(scenarioResults, Console.Out);
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
