using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Model;

namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// Runs a document's own given/when/then scenarios against the generated code —
/// ports pocketcqrs's <c>emschema.Verify</c>. This is what turns a scenario from
/// documentation into a check: "given these events, when this command, then these
/// events" is exactly the shape of a decide dry run.
///
/// Nothing is appended and nothing is registered against live state; every run
/// happens in a fresh scratch project's own in-memory stores. A FAILING scenario is
/// not a verification error — the generated code is a starting point whose rules are
/// the author's job, so a failure usually means the document describes behavior
/// nobody has written yet. The useful output is the list, not a verdict.
///
/// Architecturally this differs from pocketcqrs's own <c>Verify</c> out of necessity:
/// pocketcqrs dry-runs JS *source text* directly in an embedded interpreter (goja), so
/// nothing needs compiling first. C# has no equivalent — generated code must be
/// compiled before it can run at all. So this compiles a fixed, reusable harness
/// (<c>HarnessProgram.txt</c>, never generated per document) together with the actual
/// generated files in one real scratch project (the same "shell out to dotnet build/
/// run" infrastructure Milestone 3's own tests proved out), feeds it every scenario as
/// JSON, and parses its JSON result back — one compile for the whole document's
/// scenarios, not one per scenario.
/// </summary>
public static class ScenarioVerifier
{
    /// <param name="dotnetCqrsProjectPath">Path to <c>DotnetCqrs.csproj</c> — the
    /// scratch harness project references it directly, since dotnetcqrs isn't
    /// published as a NuGet package (yet).</param>
    public static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(
        Document document, MappingResult mapped, string dotnetCqrsProjectPath, CancellationToken ct = default)
    {
        var index = GeneratedIndex.Build(document, mapped.Domains);

        var results = new List<ScenarioResult>();
        var commandScenarios = new List<CommandScenarioInput>();
        var viewScenarios = new List<ViewScenarioInput>();

        foreach (var slice in document.Slices)
        {
            foreach (var scenario in slice.Scenarios)
            {
                switch (scenario)
                {
                    case StateChangeScenario or ErrorScenario:
                        BuildCommandScenario(document, index, slice, scenario, results, commandScenarios);
                        break;
                    case StateViewScenario viewScenario:
                        BuildViewScenario(document, index, slice, viewScenario, results, viewScenarios);
                        break;
                }
            }
        }

        if (commandScenarios.Count == 0 && viewScenarios.Count == 0)
            return results;

        var harnessResults = await RunHarnessAsync(commandScenarios, viewScenarios, dotnetCqrsProjectPath, mapped.Domains, ct);
        results.AddRange(harnessResults);
        return results;
    }

    private static string KindOf(Scenario s) => s switch
    {
        StateChangeScenario => "stateChange",
        ErrorScenario => "error",
        StateViewScenario => "stateView",
        _ => "unknown",
    };

    private static void BuildCommandScenario(
        Document document, GeneratedIndex index, Slice slice, Scenario scenario,
        List<ScenarioResult> results, List<CommandScenarioInput> commandScenarios)
    {
        var (commandId, payload) = scenario switch
        {
            StateChangeScenario s => (s.When.CommandId, s.When.Data),
            ErrorScenario s => (s.When.CommandId, s.When.Data),
            _ => throw new InvalidOperationException("unreachable"),
        };

        if (!index.CommandAggregate.TryGetValue(commandId, out var aggregate) || !index.CommandName.TryGetValue(commandId, out var generatedName))
        {
            results.Add(new ScenarioResult(slice.Id, scenario.Id, scenario.Name, KindOf(scenario), Passed: false, Skipped: true,
                $"command \"{commandId}\" was not generated (its slice may have been skipped)"));
            return;
        }

        var aggregatePascal = GenerationSupport.ExportName(aggregate);
        var deciderTypeName = $"Generated.{aggregatePascal}.{aggregatePascal}Decider";

        // `given` may name events from ANOTHER aggregate: an automation scenario's
        // history is the trigger event, which lives on the stream that caused the
        // reaction, not on the one the command targets. Seeding those here would make
        // the decider fold an event it does not handle -- a failure about the fixture,
        // not the domain. Only "own" events seed the fixture; "foreign" ones are
        // simply not replayed (the harness doesn't need to know they existed).
        var (own, _) = SplitGiven(index, aggregate, scenario.Given);
        var streamId = StreamId(document, scenario.Given);

        var expectedEventTypes = scenario is StateChangeScenario stateChange
            ? stateChange.Then.Events.Select(e => index.EventType.GetValueOrDefault(e.EventId, e.EventId)).ToList()
            : [];

        commandScenarios.Add(new CommandScenarioInput(
            slice.Id, scenario.Id, scenario.Name, KindOf(scenario),
            deciderTypeName, aggregate, streamId, own,
            generatedName, payload?.GetRawText() ?? "{}", expectedEventTypes));
    }

    private static void BuildViewScenario(
        Document document, GeneratedIndex index, Slice slice, StateViewScenario scenario,
        List<ScenarioResult> results, List<ViewScenarioInput> viewScenarios)
    {
        var readModelId = scenario.When.ReadModelId;
        if (!index.ReadModel.TryGetValue(readModelId, out var info))
        {
            results.Add(new ScenarioResult(slice.Id, scenario.Id, scenario.Name, "stateView", Passed: false, Skipped: true,
                $"read model \"{readModelId}\" was not generated"));
            return;
        }

        var aggregatePascal = GenerationSupport.ExportName(info.Aggregate);
        var projectionTypeName = $"Generated.{aggregatePascal}.{GenerationSupport.ExportName(info.Collection)}Projection";

        // Unlike a command scenario, a view scenario's `given` is NOT split by
        // aggregate: a projection is explicitly allowed to be cross-cutting (fold
        // events from several aggregates), so every given event is real fixture
        // history for it, whichever stream it actually came from.
        var given = scenario.Given
            .Select(g => new GivenEventInput(index.EventType.GetValueOrDefault(g.EventId, g.EventId), g.Data?.GetRawText() ?? "{}"))
            .ToList();
        var streamId = StreamId(document, scenario.Given);

        viewScenarios.Add(new ViewScenarioInput(
            slice.Id, scenario.Id, scenario.Name, projectionTypeName, info.Aggregate,
            info.Collection, info.KeyColumn, streamId, given,
            scenario.When.QueryParams?.GetRawText(), scenario.Then.Result.GetRawText()));
    }

    private static (List<GivenEventInput> Own, List<GivenEventInput> Foreign) SplitGiven(
        GeneratedIndex index, string aggregate, IReadOnlyList<EventRef> given)
    {
        var own = new List<GivenEventInput>();
        var foreign = new List<GivenEventInput>();
        foreach (var g in given)
        {
            var type = index.EventType.GetValueOrDefault(g.EventId, g.EventId);
            var input = new GivenEventInput(type, g.Data?.GetRawText() ?? "{}");
            if (index.EventAggregate.TryGetValue(g.EventId, out var owner) && owner != aggregate)
                foreign.Add(input);
            else
                own.Add(input);
        }
        return (own, foreign);
    }

    /// <summary><c>idAttribute: true</c> names the field carrying instance identity,
    /// so when a `given` event's data supplies it, the scenario runs against THAT id
    /// — a stateView scenario queries by the same value, and a fixed placeholder id
    /// would make such a query miss for a reason unrelated to the projection under
    /// test.</summary>
    private static string StreamId(Document document, IReadOnlyList<EventRef> given)
    {
        foreach (var g in given)
        {
            if (document.Events is null || !document.Events.TryGetValue(g.EventId, out var ev)) continue;
            if (g.Data is not { } data) continue;
            foreach (var field in ev.Fields ?? [])
            {
                if (field.IdAttribute != true) continue;
                if (data.TryGetProperty(field.Name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var str = value.GetString();
                    if (!string.IsNullOrEmpty(str)) return str;
                }
            }
        }
        return "scenario";
    }

    private static async Task<IReadOnlyList<ScenarioResult>> RunHarnessAsync(
        List<CommandScenarioInput> commandScenarios, List<ViewScenarioInput> viewScenarios,
        string dotnetCqrsProjectPath, IReadOnlyList<Domain.Domain> domains, CancellationToken ct)
    {
        var scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        try
        {
            var csproj = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{dotnetCqrsProjectPath}" />
                  </ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(Path.Combine(scratchDir, "Scratch.csproj"), csproj, ct);

            foreach (var domain in domains)
                foreach (var file in CSharpGenerator.Generate(domain))
                    await File.WriteAllTextAsync(Path.Combine(scratchDir, file.Name), file.Source, ct);

            await File.WriteAllTextAsync(Path.Combine(scratchDir, "Program.cs"), ReadEmbeddedHarness(), ct);

            var inputPath = Path.Combine(scratchDir, "input.json");
            var outputPath = Path.Combine(scratchDir, "output.json");
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new HarnessInput(commandScenarios, viewScenarios)), ct);

            var psi = new ProcessStartInfo("dotnet", $"run --project \"{scratchDir}\" -- \"{inputPath}\" \"{outputPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException($"scenario verification harness failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");

            var dtos = JsonSerializer.Deserialize<List<HarnessResultDto>>(await File.ReadAllTextAsync(outputPath, ct))!;
            return dtos.Select(r => new ScenarioResult(r.SliceId, r.ScenarioId, r.Name, r.Kind, r.Passed, r.Skipped, r.Detail)).ToList();
        }
        finally
        {
            if (Directory.Exists(scratchDir))
                Directory.Delete(scratchDir, recursive: true);
        }
    }

    private sealed record HarnessResultDto(string SliceId, string ScenarioId, string Name, string Kind, bool Passed, bool Skipped, string Detail);

    private static string ReadEmbeddedHarness()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "DotnetCqrs.Codegen.Verification.HarnessProgram.txt";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reverse lookups from schema element id to what the mapper/generator
    /// actually produced for it — computed by re-deriving the same generated names
    /// <see cref="DocumentMapper"/> and the <see cref="Generation"/> types use, rather
    /// than requiring either to expose internal state.</summary>
    private sealed class GeneratedIndex
    {
        public required Dictionary<string, string> CommandAggregate; // schema command id -> aggregate
        public required Dictionary<string, string> CommandName; // schema command id -> generated command name
        public required Dictionary<string, string> EventAggregate; // schema event id -> aggregate
        public required Dictionary<string, string> EventType; // schema event id -> generated event type
        public required Dictionary<string, (string Aggregate, string Collection, string KeyColumn)> ReadModel; // schema read model id -> info

        public static GeneratedIndex Build(Document document, IReadOnlyList<Domain.Domain> domains)
        {
            var generatedCommandToAggregate = new Dictionary<string, string>();
            var generatedEventToAggregate = new Dictionary<string, string>();
            foreach (var d in domains)
            {
                foreach (var c in d.Commands)
                    generatedCommandToAggregate[c.Name] = d.Aggregate;
                foreach (var e in d.Events())
                    generatedEventToAggregate.TryAdd(e, d.Aggregate); // first wins -- an event produced by more than one command/aggregate is legitimate, not an error
            }

            var commandAggregate = new Dictionary<string, string>();
            var commandName = new Dictionary<string, string>();
            if (document.Commands is not null)
            {
                foreach (var (id, cmd) in document.Commands)
                {
                    var name = Names.TypeName(cmd.Name, id);
                    if (generatedCommandToAggregate.TryGetValue(name, out var agg))
                    {
                        commandAggregate[id] = agg;
                        commandName[id] = name;
                    }
                }
            }

            var eventAggregate = new Dictionary<string, string>();
            var eventType = new Dictionary<string, string>();
            if (document.Events is not null)
            {
                foreach (var (id, ev) in document.Events)
                {
                    var type = Names.TypeName(ev.Name, id);
                    eventType[id] = type;
                    if (generatedEventToAggregate.TryGetValue(type, out var agg))
                        eventAggregate[id] = agg;
                }
            }

            var readModel = new Dictionary<string, (string, string, string)>();
            if (document.ReadModels is not null)
            {
                foreach (var (id, rm) in document.ReadModels)
                {
                    var collection = Names.LowerFirst(Names.TypeName(rm.Name, id));
                    foreach (var d in domains)
                    {
                        var match = d.ReadModels.FirstOrDefault(r => r.Collection == collection);
                        if (match is null) continue;
                        readModel[id] = (d.Aggregate, collection, ToSnakeCase(match.Key));
                        break;
                    }
                }
            }

            return new GeneratedIndex
            {
                CommandAggregate = commandAggregate,
                CommandName = commandName,
                EventAggregate = eventAggregate,
                EventType = eventType,
                ReadModel = readModel,
            };
        }
    }

    // Matches ProjectionGenerator's own column-naming transform exactly -- duplicated
    // rather than shared, since a shared helper would only save a few lines at the
    // cost of coupling this orchestrator to Generation's internals.
    private static string ToSnakeCase(string name)
    {
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) b.Append('_');
                b.Append(char.ToLowerInvariant(c));
            }
            else
            {
                b.Append(c);
            }
        }
        return b.ToString();
    }
}
