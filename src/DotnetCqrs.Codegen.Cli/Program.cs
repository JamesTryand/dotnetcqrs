using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

// Hand-rolled, not System.CommandLine: two subcommands, seven flags (one repeatable,
// three host-only) is still small enough that a dependency buys nothing
// here, a call rechecked when --host/--project-name were added.
if (args.Length == 0 || (args[0] != "generate" && args[0] != "verify"))
{
    Console.Error.WriteLine("usage: dotnetcqrs-codegen generate --input <path> --output <dir> [--host [--project-name <name>] [--dotnetcqrs-version <v> | --dotnetcqrs-project <path>]] [--aggregate-override <id>=<aggregate>]...");
    Console.Error.WriteLine("       dotnetcqrs-codegen verify --input <path> [--aggregate-override <id>=<aggregate>]...");
    return 1;
}

var command = args[0];
string? input = null;
string? output = null;
string? dotnetCqrsProject = null;
string? dotnetCqrsVersion = null;
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
        case "--dotnetcqrs-version":
            if (i + 1 >= args.Length) { Console.Error.WriteLine("--dotnetcqrs-version requires a value"); return 1; }
            dotnetCqrsVersion = args[++i];
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
// --host references the dotnetcqrs packages at this tool's own version (or --dotnetcqrs-version) and pins this
// tool at that version in a tool manifest; --dotnetcqrs-project <checkout's DotnetCqrs.csproj> references a
// checkout's projects instead, for trying an unreleased version only.
if (command == "generate" && dotnetCqrsProject is not null && dotnetCqrsVersion is not null)
{
    Console.Error.WriteLine("--dotnetcqrs-version and --dotnetcqrs-project cannot be used together");
    return 1;
}
// verify runs against the dotnetcqrs this tool ships with; --dotnetcqrs-project is accepted
// and ignored, so existing scripts keep working.
if (command == "verify" && input is null)
{
    Console.Error.WriteLine("--input is required");
    return 1;
}

try
{
    var document = DocumentLoader.LoadFromFile(input!);
    var result = DocumentMapper.Map(document, new MappingOptions { AggregateOverrides = overrides });

    // Warnings are decisions taken on the document's behalf, not failures -- the
    // CLI prints and continues, never refuses, on a warning alone.
    foreach (var warning in result.Report.Warnings)
        Console.WriteLine($"warning: {warning}");

    if (command == "generate")
    {
        Directory.CreateDirectory(output!);
        var written = 0;

        if (host)
        {
            var name = projectName ?? Names.TypeName(document.Name, document.Id);
            string? manifestWritten = null;
            foreach (var file in HostProjectGenerator.Generate(document, result, name, dotnetCqrsProject, overrides, dotnetCqrsVersion))
            {
                var path = Path.Combine(output!, file.Name);
                var source = file.Source;
                if (file.Name == HostProjectGenerator.ToolManifestPath)
                {
                    // the manifest `dotnet tool` itself would find keeps its other tools: only this tool's entry
                    // changes; with none at or above the project, the project gets its own
                    var existing = HostProjectGenerator.FindToolManifest(output!);
                    if (existing is not null)
                    {
                        path = existing;
                        source = HostProjectGenerator.ToolManifest(File.ReadAllText(existing), dotnetCqrsVersion ?? HostProjectGenerator.GeneratorVersion);
                    }
                    manifestWritten = path;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, source);
                written++;
            }
            File.Copy(input!, Path.Combine(output!, "document.json"), overwrite: true);
            written++;

            Console.WriteLine($"generated {written} file(s) to {output}");
            Console.WriteLine(dotnetCqrsProject is null
                ? $"dotnetcqrs {dotnetCqrsVersion ?? HostProjectGenerator.GeneratorVersion}: package references, and this tool pinned at that version in {manifestWritten}"
                : $"dotnetcqrs from the checkout at {dotnetCqrsProject} (project references; for an unreleased version only)");
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

    var scenarioResults = await ScenarioVerifier.VerifyAsync(document, result);
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
