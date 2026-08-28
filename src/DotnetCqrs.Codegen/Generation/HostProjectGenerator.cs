using System.Text;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Model;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Emits a complete standalone runnable project from a WHOLE document's mapping —
/// unlike <see cref="CSharpGenerator"/> (one <see cref="Domain.Domain"/>'s library
/// code), this wires every aggregate's generated code into one process: a
/// <c>.csproj</c>, and a <c>Program.cs</c> that builds a <c>DeciderRegistry</c> (one
/// decider per aggregate), one projection per read model, a combined
/// <c>WriteGuard.InstallAsync</c>, a <c>ConsumerEngine</c> (every projection plus one
/// <c>ReactorConsumer</c> per reactor), and an unauthenticated <c>MapCqrsGateway()</c> —
/// the exact pattern <c>samples/OrderFulfillment/Program.cs</c> hand-wires. See
/// README.md's "Host scaffolding" milestone for the resolved design questions
/// (two SQLite files, no auth/file-storage, a baked-in <c>--verify</c> mode).
///
/// The generated <c>--verify</c> mode re-runs <see cref="DocumentMapper.Map"/> against
/// a copy of the source document (written alongside this project by the caller) using
/// the SAME aggregate overrides supplied at generation time, baked in here as a literal
/// — there is nowhere else for them to come from at verify time, since the document
/// itself may leave some elements untagged. It then calls
/// <see cref="Verification.ScenarioVerifier.VerifyAsync"/> and
/// <see cref="Verification.ScenarioReport.Print"/> exactly as the CLI's own <c>verify</c>
/// command does — reusing that print/exit-code helper is the entire reason it lives in
/// this project rather than DotnetCqrs.Codegen.Cli (see README's Milestone 7 note).
/// </summary>
public static class HostProjectGenerator
{
    public static IReadOnlyList<GeneratedFile> Generate(
        Document document, MappingResult mapped, string projectName, string dotnetCqrsProjectPath,
        IReadOnlyDictionary<string, string> aggregateOverrides)
    {
        var files = new List<GeneratedFile>();
        foreach (var domain in mapped.Domains)
            files.AddRange(CSharpGenerator.Generate(domain));

        files.Add(GenerateCsproj(projectName, dotnetCqrsProjectPath));
        files.Add(GenerateProgram(mapped, dotnetCqrsProjectPath, aggregateOverrides));
        return files;
    }

    private static GeneratedFile GenerateCsproj(string projectName, string dotnetCqrsProjectPath)
    {
        // DotnetCqrs.Host and DotnetCqrs.Codegen are siblings of DotnetCqrs under the
        // same src/ directory in every environment this generator has been used against
        // (see README's "Repo structure") -- derived rather than asked for separately,
        // since asking the operator for three paths to name one repo would be needless.
        var srcDir = Path.GetDirectoryName(Path.GetDirectoryName(dotnetCqrsProjectPath))!;
        var hostProjectPath = Path.Combine(srcDir, "DotnetCqrs.Host", "DotnetCqrs.Host.csproj");
        var codegenProjectPath = Path.Combine(srcDir, "DotnetCqrs.Codegen", "DotnetCqrs.Codegen.csproj");

        var source = $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <RootNamespace>{projectName}</RootNamespace>
              </PropertyGroup>

              <ItemGroup>
                <ProjectReference Include="{dotnetCqrsProjectPath}" />
                <ProjectReference Include="{hostProjectPath}" />
                <ProjectReference Include="{codegenProjectPath}" />
              </ItemGroup>

              <ItemGroup>
                <None Include="document.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>

            </Project>
            """;
        return new GeneratedFile($"{projectName}.csproj", source);
    }

    private static GeneratedFile GenerateProgram(
        MappingResult mapped, string dotnetCqrsProjectPath, IReadOnlyDictionary<string, string> aggregateOverrides)
    {
        var b = new StringBuilder();
        b.AppendLine("using DotnetCqrs.Codegen;");
        b.AppendLine("using DotnetCqrs.Codegen.Mapping;");
        b.AppendLine("using DotnetCqrs.Codegen.Verification;");
        b.AppendLine("using DotnetCqrs.Consumers;");
        b.AppendLine("using DotnetCqrs.Deciders;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.Host;");
        b.AppendLine("using DotnetCqrs.ReadModels;");
        b.AppendLine("using DotnetCqrs.Reactors;");
        b.AppendLine("using DotnetCqrs.WriteGuards;");
        foreach (var ns in mapped.Domains.Select(d => GenerationSupport.ExportName(d.Aggregate)).Distinct())
            b.AppendLine($"using Generated.{ns};");
        b.AppendLine();

        // A baked-in self-test: `dotnet run -- --verify` re-maps the copied source
        // document (never the running host's own live state) and reports the document's
        // declared scenarios against the code this same generation run just wrote.
        b.AppendLine("if (args.Length > 0 && args[0] == \"--verify\")");
        b.AppendLine("{");
        b.AppendLine("    var verifyDocument = DocumentLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, \"document.json\"));");
        b.AppendLine("    var verifyOptions = new MappingOptions");
        b.AppendLine("    {");
        b.AppendLine("        AggregateOverrides = new Dictionary<string, string>");
        b.AppendLine("        {");
        foreach (var (id, aggregate) in aggregateOverrides)
            b.AppendLine($"            [\"{id}\"] = \"{aggregate}\",");
        b.AppendLine("        },");
        b.AppendLine("    };");
        b.AppendLine("    var verifyMapped = DocumentMapper.Map(verifyDocument, verifyOptions);");
        b.AppendLine($"    var verifyResults = await ScenarioVerifier.VerifyAsync(verifyDocument, verifyMapped, @\"{dotnetCqrsProjectPath}\");");
        b.AppendLine("    return ScenarioReport.Print(verifyResults, Console.Out);");
        b.AppendLine("}");
        b.AppendLine();

        b.AppendLine("var dataDir = Path.Combine(AppContext.BaseDirectory, \"data\");");
        b.AppendLine("Directory.CreateDirectory(dataDir);");
        b.AppendLine("var eventsPath = Path.Combine(dataDir, \"events.db\");");
        b.AppendLine("var readModelPath = Path.Combine(dataDir, \"readmodel.db\");");
        b.AppendLine();
        b.AppendLine("var builder = WebApplication.CreateBuilder(args);");
        b.AppendLine();
        b.AppendLine("var eventStore = await SqliteEventStore.OpenAsync(eventsPath);");
        b.AppendLine("var readModelDb = await ReadModelDb.OpenAsync(readModelPath);");
        b.AppendLine();
        b.AppendLine("var registry = new DeciderRegistry(eventStore);");
        foreach (var domain in mapped.Domains)
        {
            var aggregate = GenerationSupport.ExportName(domain.Aggregate);
            b.AppendLine($"registry.Register({aggregate}Decider.Aggregate, {aggregate}Decider.Create());");
        }
        b.AppendLine("builder.Services.AddSingleton(registry);");
        b.AppendLine();

        var projectionVars = new List<string>();
        foreach (var domain in mapped.Domains)
        {
            foreach (var readModel in domain.ReadModels)
            {
                var typeName = GenerationSupport.ExportName(readModel.Collection) + "Projection";
                var varName = readModel.Collection + "Projection";
                b.AppendLine($"var {varName} = new {typeName}(readModelDb);");
                b.AppendLine($"await {varName}.InitAsync();");
                projectionVars.Add(varName);
            }
        }
        b.AppendLine();

        // The tables must exist before the guard's triggers can be created on them, so
        // every projection's InitAsync runs first -- see samples/OrderFulfillment/
        // Program.cs:39-41's identical ordering comment.
        var tablesExpr = projectionVars.Count == 0
            ? "[]"
            : "[" + string.Join(", ", projectionVars.Select(v => $".. {v}.Tables")) + "]";
        b.AppendLine($"await WriteGuard.InstallAsync(readModelDb, {tablesExpr});");
        b.AppendLine();

        b.AppendLine("var engine = new ConsumerEngine(eventStore, eventStore);");
        foreach (var v in projectionVars)
            b.AppendLine($"engine.Register({v});");
        foreach (var domain in mapped.Domains)
        {
            foreach (var reactor in domain.Reactors)
            {
                var typeName = GenerationSupport.ExportName(reactor.Name) + "Reactor";
                b.AppendLine($"engine.Register(new ReactorConsumer(new {typeName}(), registry));");
            }
        }
        b.AppendLine();

        b.AppendLine("var app = builder.Build();");
        b.AppendLine("_ = engine.StartAsync(app.Lifetime.ApplicationStopping);");
        b.AppendLine("app.MapCqrsGateway();");
        b.AppendLine();
        b.AppendLine("await app.RunAsync();");
        b.AppendLine("return 0;");

        return new GeneratedFile("Program.cs", b.ToString());
    }
}
