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
/// generated files in memory and runs it in this process (<see cref="InProcessHarness"/>),
/// against the dotnetcqrs assemblies this tool ships with, feeds it every scenario as JSON,
/// and parses its JSON result back — one compile for the whole document's scenarios, not
/// one per scenario, and no scratch project, <c>dotnet build</c> or child process.
/// </summary>
public static class ScenarioVerifier
{
    /// <summary>Kept for source compatibility: verification no longer builds a project, so
    /// <paramref name="dotnetCqrsProjectPath"/> is ignored and the generated code runs against
    /// the dotnetcqrs this tool ships with.</summary>
    [Obsolete("dotnetCqrsProjectPath is no longer used; call VerifyAsync(document, mapped, ct).")]
    public static Task<IReadOnlyList<ScenarioResult>> VerifyAsync(
        Document document, MappingResult mapped, string dotnetCqrsProjectPath, CancellationToken ct = default) =>
        VerifyAsync(document, mapped, ct);

    public static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(
        Document document, MappingResult mapped, CancellationToken ct = default)
    {
        var index = GeneratedIndex.Build(document, mapped.Domains);
        var handOutcomes = HandOutcomes(mapped.Domains);

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
                        BuildCommandScenario(document, index, slice, scenario, results, commandScenarios, handOutcomes);
                        break;
                    case StateViewScenario viewScenario when IsHandBuilt(document, viewScenario.When.ReadModelId):
                        // Schema 3.17.0: a hand-written projection builds it, which only a host check can run.
                        results.Add(new ScenarioResult(slice.Id, viewScenario.Id, viewScenario.Name, "stateView", Passed: false, Skipped: false,
                            $"read model \"{viewScenario.When.ReadModelId}\" is built by a hand-written projection (projection: hand); a host check runs it",
                            ByHand: true));
                        break;
                    case StateViewScenario viewScenario:
                        BuildViewScenario(document, index, slice, viewScenario, results, viewScenarios);
                        break;
                }
            }
        }

        if (commandScenarios.Count == 0 && viewScenarios.Count == 0)
            return results;

        var harnessResults = await RunHarnessAsync(commandScenarios, viewScenarios, mapped.Domains, ct);
        results.AddRange(harnessResults);
        return results;
    }

    /// <summary>Verifies a document against a host's own deciders: command scenarios
    /// run in-process against what <paramref name="host"/> registers, so the values a scenario
    /// declares check the host's real arithmetic and rules, not a generated stand-in that only
    /// copies the command's payload. View scenarios run against the generated projections, or the host's own
    /// where it gives one (<see cref="HostDeciders.Projections"/>, schema 3.17.0).</summary>
    public static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(
        Document document, MappingResult mapped, HostDeciders host, CancellationToken ct = default)
    {
        var index = GeneratedIndex.Build(document, mapped.Domains);
        var results = new List<ScenarioResult>();
        var commandScenarios = new List<CommandScenarioInput>();
        var viewScenarios = new List<ViewScenarioInput>();
        foreach (var slice in document.Slices)
            foreach (var scenario in slice.Scenarios)
            {
                if (scenario is StateChangeScenario or ErrorScenario)
                    BuildCommandScenario(document, index, slice, scenario, results, commandScenarios);
                else if (scenario is StateViewScenario viewScenario)
                {
                    var readModelId = viewScenario.When.ReadModelId;
                    // Schema 3.17.0: the host's own projection, where it gives one; a hand-built read model needs one.
                    if (host.Projections.TryGetValue(readModelId, out var hostProjection))
                        BuildViewScenario(document, index, slice, viewScenario, results, viewScenarios, hostProjection);
                    else if (IsHandBuilt(document, readModelId))
                        results.Add(new ScenarioResult(slice.Id, viewScenario.Id, viewScenario.Name, "stateView", Passed: false, Skipped: false,
                            $"read model \"{readModelId}\" declares projection: hand, but the host gives no projection for it (HostDeciders.Projections)"));
                    else
                        BuildViewScenario(document, index, slice, viewScenario, results, viewScenarios);
                }
            }

        foreach (var scenario in commandScenarios)
            results.Add(await HostScenarioRunner.RunAsync(scenario, host, ct));
        if (viewScenarios.Count > 0)
            results.AddRange(await RunHarnessAsync([], viewScenarios, mapped.Domains, ct));
        return results;
    }

    private static string KindOf(Scenario s) => s switch
    {
        StateChangeScenario => "stateChange",
        ErrorScenario => "error",
        StateViewScenario => "stateView",
        _ => "unknown",
    };

    /// <summary>Schema 3.12.0: for each command with a hand-written outcome condition (or, 3.13.0,
    /// repeated events), keyed by (aggregate, generated command name), its alternatives as event
    /// types, each marked when it repeats. A generated stand-in can't make that choice; the harness
    /// reports a scenario reaching it as made by hand.</summary>
    private static Dictionary<(string Aggregate, string Command), IReadOnlyList<IReadOnlyList<HandOutcomeEvent>>> HandOutcomes(IReadOnlyList<Domain.Domain> domains) =>
        domains.SelectMany(d => d.Commands.Where(c => c.Outcomes.Any(o => o.NeedsHand)).Select(c => (d.Aggregate, c)))
            .ToDictionary(x => (x.Aggregate, x.c.Name), x => (IReadOnlyList<IReadOnlyList<HandOutcomeEvent>>)x.c.Outcomes
                .Select(o => (IReadOnlyList<HandOutcomeEvent>)o.EventNames.Select(n => new HandOutcomeEvent(n, o.Repeated.Contains(n))).ToList()).ToList());

    /// <summary>A stand-in for each hand-written outcome condition, so the generated deciders
    /// compile in the harness: it throws, and the harness reports the scenario as a choice made by
    /// hand rather than guessing.</summary>
    private static string? HandOutcomeStandIns(IReadOnlyList<Domain.Domain> domains)
    {
        var b = new System.Text.StringBuilder();
        foreach (var domain in domains)
        {
            var hooks = domain.Commands.SelectMany(c => c.Outcomes.Select((o, i) => (c, o, i))).Where(x => x.o.NeedsHand).ToList();
            if (hooks.Count == 0) continue;
            var aggregate = GenerationSupport.ExportName(domain.Aggregate);
            b.AppendLine($"namespace Generated.{aggregate}");
            b.AppendLine("{");
            b.AppendLine($"    public static partial class {aggregate}Decider");
            b.AppendLine("    {");
            foreach (var (command, outcome, i) in hooks)
            {
                if (outcome.ByHand)
                    b.AppendLine($"        private static partial bool {command.Name}Outcome{i}({aggregate}State state, DotnetCqrs.Deciders.Command cmd) => throw new global::ChoiceByHandException();");
                foreach (var name in outcome.Repeated)
                    b.AppendLine($"        private static partial System.Collections.Generic.IEnumerable<{GenerationSupport.ExportName(name)}Payload> {command.Name}Outcome{i}{name}({aggregate}State state, DotnetCqrs.Deciders.Command cmd) => throw new global::ChoiceByHandException();");
            }
            b.AppendLine("    }");
            b.AppendLine("}");
        }
        return b.Length == 0 ? null : b.ToString();
    }

    private static void BuildCommandScenario(
        Document document, GeneratedIndex index, Slice slice, Scenario scenario,
        List<ScenarioResult> results, List<CommandScenarioInput> commandScenarios,
        IReadOnlyDictionary<(string Aggregate, string Command), IReadOnlyList<IReadOnlyList<HandOutcomeEvent>>>? handOutcomes = null)
    {
        var when = scenario switch
        {
            StateChangeScenario s => s.When,
            ErrorScenario s => s.When,
            _ => throw new InvalidOperationException("unreachable"),
        };
        var (commandId, payload) = (when.CommandId, when.Data);

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

        // The read models the command's authorization reads, built from every given event (a read model is
        // cross-cutting), each keyed for its own projection, as a view scenario's are.
        var authorizationReadModels = new List<AuthorizationReadModelInput>();
        foreach (var collection in index.CommandPolicyCollections.GetValueOrDefault((aggregate, generatedName)) ?? [])
        {
            if (!index.ReadModelByCollection.TryGetValue(collection, out var rm))
            {
                results.Add(new ScenarioResult(slice.Id, scenario.Id, scenario.Name, KindOf(scenario), Passed: false, Skipped: true,
                    $"the command's authorization reads collection \"{collection}\", which was not generated"));
                return;
            }
            var idField = index.ReadModelIdByCollection.TryGetValue(collection, out var rmId) ? ReadModelIdFieldName(document, rmId) : null;
            authorizationReadModels.Add(new AuthorizationReadModelInput(
                $"Generated.{GenerationSupport.ExportName(rm.Aggregate)}.{GenerationSupport.ExportName(rm.Collection)}Projection",
                rm.Aggregate,
                scenario.Given.Select(g => new ViewGivenEventInput(
                    index.EventType.GetValueOrDefault(g.EventId, g.EventId),
                    g.Data?.GetRawText() ?? "{}",
                    RowKey(idField, g.Data, streamId),
                    new Dictionary<string, string>(),
                    index.EventPiiFields.GetValueOrDefault(index.EventType.GetValueOrDefault(g.EventId, g.EventId)) ?? EmptyPiiFields)).ToList()));
        }

        var expectedEventTypes = scenario is StateChangeScenario stateChange
            ? stateChange.Then.Events.Select(e => index.EventType.GetValueOrDefault(e.EventId, e.EventId)).ToList()
            : [];

        commandScenarios.Add(new CommandScenarioInput(
            slice.Id, scenario.Id, scenario.Name, KindOf(scenario),
            deciderTypeName, aggregate, streamId, own,
            generatedName, payload?.GetRawText() ?? "{}", expectedEventTypes,
            scenario is ErrorScenario error ? error.Then.Error.Message : null,
            scenario is StateChangeScenario values ? values.Then.Events.Select(e => e.Data?.GetRawText()).ToList() : null,
            when.Actor, when.At,
            handOutcomes?.GetValueOrDefault((aggregate, generatedName)),
            authorizationReadModels.Count == 0 ? null : authorizationReadModels));
    }

    private static bool IsHandBuilt(Document document, string readModelId) =>
        document.ReadModels?.GetValueOrDefault(readModelId)?.Projection == "hand";

    private static void BuildViewScenario(
        Document document, GeneratedIndex index, Slice slice, StateViewScenario scenario,
        List<ScenarioResult> results, List<ViewScenarioInput> viewScenarios, Type? hostProjection = null)
    {
        var readModelId = scenario.When.ReadModelId;
        if (!index.ReadModel.TryGetValue(readModelId, out var info))
        {
            results.Add(new ScenarioResult(slice.Id, scenario.Id, scenario.Name, "stateView", Passed: false, Skipped: true,
                $"read model \"{readModelId}\" was not generated"));
            return;
        }

        var aggregatePascal = GenerationSupport.ExportName(info.Aggregate);
        // A host's own projection is named in full, so the harness loads it from the host's assembly.
        var projectionTypeName = hostProjection?.AssemblyQualifiedName
            ?? $"Generated.{aggregatePascal}.{GenerationSupport.ExportName(info.Collection)}Projection";

        var queryParamNames = new HashSet<string>();
        if (scenario.When.QueryParams is { ValueKind: JsonValueKind.Object } qp)
            foreach (var prop in qp.EnumerateObject())
                queryParamNames.Add(prop.Name);

        // Only the scopes this scenario's OWN queryParams actually invoke -- an
        // unscoped view of the same read model (a role holder's own query, say) passes
        // none of these params and gets no semi-join, exactly as the runtime query
        // would.
        var scopes = new List<ViewScopeInput>();
        var viaIdFieldNames = new Dictionary<string, string?>(); // via projection type name -> that read model's own idAttribute field name
        foreach (var scope in info.Scopes)
        {
            // A granting scope (schema 3.8.0) can widen what a restricted caller sees whether or
            // not the scenario sends its param, so its via table is always seeded.
            if (!queryParamNames.Contains(scope.Param) && !scope.GrantsAccess) continue;
            if (!index.ReadModelByCollection.TryGetValue(scope.ViaCollection, out var via))
            {
                results.Add(new ScenarioResult(slice.Id, scenario.Id, scenario.Name, "stateView", Passed: false, Skipped: true,
                    $"read model \"{readModelId}\" scope on param \"{scope.Param}\" resolves to collection " +
                    $"\"{scope.ViaCollection}\", which was not generated"));
                return;
            }
            var viaProjectionTypeName = $"Generated.{GenerationSupport.ExportName(via.Aggregate)}.{GenerationSupport.ExportName(via.Collection)}Projection";
            scopes.Add(new ViewScopeInput(scope.Param, viaProjectionTypeName, via.Collection,
                ToSnakeCase(scope.MatchParamToField), ToSnakeCase(scope.SelectField), ToSnakeCase(scope.FilterLocalField)));
            viaIdFieldNames[viaProjectionTypeName] = index.ReadModelIdByCollection.TryGetValue(scope.ViaCollection, out var viaId)
                ? ReadModelIdFieldName(document, viaId)
                : null;
        }

        // Unlike a command scenario, a view scenario's `given` is NOT split by
        // aggregate: a projection is explicitly allowed to be cross-cutting (fold
        // events from several aggregates), so every given event is real fixture
        // history for it, whichever stream it actually came from.
        //
        // Each given event needs its OWN row key per projection it's replayed into --
        // NOT one key shared by the whole scenario (the pre-Finding-3 design): an
        // unscoped "a role holder sees every order" scenario deliberately seeds two
        // DIFFERENT entries and expects two DIFFERENT rows out, which one shared
        // synthesized id can never produce (every given event would collapse onto the
        // same row). See ReadModelIdFieldName's doc comment for the resolution rule.
        var sharedDefault = StreamId(document, scenario.Given);
        var mainIdFieldName = ReadModelIdFieldName(document, readModelId);
        // Schema 3.16.0: a rowsPerListItem read model's rows are groups of a stream's items, so each given
        // event is keyed by its own stream (its idAttribute field), and two streams stay two.
        var rowsPerListItem = document.ReadModels?.GetValueOrDefault(readModelId)?.RowsPerListItem is not null;
        var given = scenario.Given
            .Select(g => new ViewGivenEventInput(
                index.EventType.GetValueOrDefault(g.EventId, g.EventId),
                g.Data?.GetRawText() ?? "{}",
                rowsPerListItem ? StreamId(document, [g], sharedDefault) : RowKey(mainIdFieldName, g.Data, sharedDefault),
                scopes.ToDictionary(s => s.ViaProjectionTypeName, s => RowKey(viaIdFieldNames.GetValueOrDefault(s.ViaProjectionTypeName), g.Data, sharedDefault)),
                index.EventPiiFields.GetValueOrDefault(index.EventType.GetValueOrDefault(g.EventId, g.EventId)) ?? EmptyPiiFields))
            .ToList();

        var readModelDomain = index.ReadModelDomain[readModelId];
        var filters = info.Filters
            .Select(f => f.IsMatch
                ? new ViewFilterInput(f.Param, ToSnakeCase(f.Field), f.Kind, f.Mode, f.Normalize, f.MinPrefixLength,
                    GenerationSupport.MatchClause(readModelDomain, f, "{0}"), GenerationSupport.IsHashedMatch(readModelDomain, f))
                : new ViewFilterInput(f.Param, ToSnakeCase(f.Field), f.Kind))
            .ToList();
        var searchIndexTypeName = GenerationSupport.IndexedMatchFilters(readModelDomain).Any()
            ? $"Generated.{aggregatePascal}.{GenerationSupport.ExportName(info.Collection)}SearchIndex"
            : null;
        var hashedIndexTypeName = GenerationSupport.HashedMatchFilters(readModelDomain).Any()
            ? $"Generated.{aggregatePascal}.{GenerationSupport.ExportName(info.Collection)}HashedIndex"
            : null;

        // A read model declaring only requiredRole has a rule too: the generated route refuses a caller
        // without the role (the harness used to read such a caller as a role holder).
        var access = readModelDomain.HasAccessRules || readModelDomain.RequiredRole is { Count: > 0 }
            ? new ViewAccessInput(readModelDomain.RequiredRole,
                readModelDomain.SelfAccess is { } selfAccess ? ToSnakeCase(selfAccess.SubjectField) : null,
                readModelDomain.SelfAccess?.Param,
                readModelDomain.Scopes.Where(s => s.GrantsAccess).Select(s => s.Param).ToList())
            : null;
        var caller = scenario.When.Caller is { } c ? new ViewCallerInput(c.SubjectId, c.Role) : null;

        viewScenarios.Add(new ViewScenarioInput(
            slice.Id, scenario.Id, scenario.Name, projectionTypeName, info.Aggregate,
            info.Collection, given,
            scenario.When.QueryParams?.GetRawText(), scenario.When.AsOf,
            scenario.Then.Result.GetRawText(), scopes, filters,
            index.ReadModelPiiColumns.GetValueOrDefault(readModelId) ?? [], searchIndexTypeName, hashedIndexTypeName,
            access, caller));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyPiiFields = new Dictionary<string, string>();

    /// <summary>The schema field a read model itself declares <c>idAttribute: true</c>
    /// on -- its own key/id column, as opposed to <see cref="StreamId"/>'s EVENT-level
    /// idAttribute (only present on an aggregate's own creation event).</summary>
    private static string? ReadModelIdFieldName(Document document, string readModelId) =>
        document.ReadModels?.GetValueOrDefault(readModelId)?.Fields?.FirstOrDefault(f => f.IdAttribute == true)?.Name;

    /// <summary>Resolves ONE given event's row key for ONE target projection: if the
    /// event's own payload names that read model's own id field, use that value (lets
    /// two given events about two different entities land on two different rows);
    /// otherwise fall back to the scenario-wide default (keeps every given event on the
    /// SAME row when the scenario doesn't distinguish them -- the common case, and the
    /// pre-Finding-3 behaviour every existing flat-result scenario already relies
    /// on).</summary>
    private static string RowKey(string? idFieldName, JsonElement? data, string fallback)
    {
        if (idFieldName is null || data is not { } d) return fallback;
        if (d.TryGetProperty(idFieldName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var str = value.GetString();
            if (!string.IsNullOrEmpty(str)) return str;
        }
        return fallback;
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
    private static string StreamId(Document document, IReadOnlyList<EventRef> given, string fallback = "scenario")
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
        return fallback;
    }

    private static async Task<IReadOnlyList<ScenarioResult>> RunHarnessAsync(
        List<CommandScenarioInput> commandScenarios, List<ViewScenarioInput> viewScenarios,
        IReadOnlyList<Domain.Domain> domains, CancellationToken ct)
    {
        // The harness still reads its input and writes its results as files (args[0], args[1]),
        // as it did as a standalone program; only these two files touch the disk.
        var workDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var inputPath = Path.Combine(workDir, "input.json");
            var outputPath = Path.Combine(workDir, "output.json");
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new HarnessInput(commandScenarios, viewScenarios)), ct);

            // The command authorization too: a command scenario runs it before the decider.
            var authorization = CommandAuthorizationGenerator.Generate(domains);
            var sources = domains
                .SelectMany(CSharpGenerator.Generate)
                .Select(file => (file.Name, file.Source))
                .Append((authorization.Name, authorization.Source))
                .Append(("Program.cs", ReadEmbeddedHarness()));
            if (HandOutcomeStandIns(domains) is { } standIns)
                sources = sources.Append(("HandOutcomes.cs", standIns));
            await InProcessHarness.RunAsync(sources, [inputPath, outputPath], ct);

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("scenario verification harness wrote no results");
            var dtos = JsonSerializer.Deserialize<List<HarnessResultDto>>(await File.ReadAllTextAsync(outputPath, ct))!;
            return dtos.Select(r => new ScenarioResult(r.SliceId, r.ScenarioId, r.Name, r.Kind, r.Passed, r.Skipped, r.Detail, r.ByHand)).ToList();
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    private sealed record HarnessResultDto(string SliceId, string ScenarioId, string Name, string Kind, bool Passed, bool Skipped, string Detail, bool ByHand = false);

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
        public required Dictionary<string, (string Aggregate, string Collection, string KeyColumn, IReadOnlyList<Domain.ReadModelScope> Scopes, IReadOnlyList<Domain.ReadModelFilter> Filters)> ReadModel; // schema read model id -> info
        public required Dictionary<string, (string Aggregate, string Collection)> ReadModelByCollection; // physical collection name -> info, for resolving a scope's `via`
        public required Dictionary<string, string> ReadModelIdByCollection; // physical collection name -> schema read model id, for looking a via-model's own idAttribute field back up in the document
        public required Dictionary<string, IReadOnlyDictionary<string, string>> EventPiiFields; // generated event type -> (pii field -> its piiSubject field)
        public required Dictionary<string, IReadOnlyList<string>> ReadModelPiiColumns; // schema read model id -> snake_case pii columns
        public required Dictionary<string, Domain.ReadModel> ReadModelDomain; // schema read model id -> the mapped read model (match clauses need it)
        public required Dictionary<(string Aggregate, string Command), IReadOnlyList<string>> CommandPolicyCollections; // (aggregate, generated command name) -> the collections its authorization reads

        public static GeneratedIndex Build(Document document, IReadOnlyList<Domain.Domain> domains)
        {
            var generatedCommandToAggregate = new Dictionary<string, string>();
            var generatedEventToAggregate = new Dictionary<string, string>();
            var eventPiiFields = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            var commandPolicyCollections = new Dictionary<(string, string), IReadOnlyList<string>>();
            foreach (var d in domains)
            {
                foreach (var c in d.Commands)
                {
                    generatedCommandToAggregate[c.Name] = d.Aggregate;
                    var collections = new List<string>();
                    if (c.RequiredOwnership is { } ownership) collections.Add(ownership.ViaCollection);
                    if (c.Scope is { } scope) collections.AddRange([scope.ResolveViaCollection, scope.MemberOfViaCollection]);
                    if (collections.Count > 0) commandPolicyCollections[(d.Aggregate, c.Name)] = collections.Distinct().ToList();
                    foreach (var ev in c.Events)
                    {
                        var pii = ev.Fields.Where(f => f.Pii && f.PiiSubject is not null).ToDictionary(f => f.Name, f => f.PiiSubject!);
                        if (pii.Count > 0) eventPiiFields.TryAdd(ev.Name, pii);
                    }
                }
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

            var readModel = new Dictionary<string, (string, string, string, IReadOnlyList<Domain.ReadModelScope>, IReadOnlyList<Domain.ReadModelFilter>)>();
            var readModelByCollection = new Dictionary<string, (string, string)>();
            var readModelIdByCollection = new Dictionary<string, string>();
            var readModelPiiColumns = new Dictionary<string, IReadOnlyList<string>>();
            var readModelDomain = new Dictionary<string, Domain.ReadModel>();
            if (document.ReadModels is not null)
            {
                foreach (var (id, rm) in document.ReadModels)
                {
                    var collection = Names.LowerFirst(Names.TypeName(rm.Name, id));
                    foreach (var d in domains)
                    {
                        var match = d.ReadModels.FirstOrDefault(r => r.Collection == collection);
                        if (match is null) continue;
                        readModel[id] = (d.Aggregate, collection, ToSnakeCase(match.Key), match.Scopes, match.Filters);
                        readModelDomain[id] = match;
                        readModelByCollection[collection] = (d.Aggregate, collection);
                        readModelIdByCollection[collection] = id;
                        readModelPiiColumns[id] = [.. match.Fields.Where(f => f.Pii).Select(f => ToSnakeCase(f.Name))];
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
                ReadModelByCollection = readModelByCollection,
                ReadModelIdByCollection = readModelIdByCollection,
                EventPiiFields = eventPiiFields,
                ReadModelPiiColumns = readModelPiiColumns,
                ReadModelDomain = readModelDomain,
                CommandPolicyCollections = commandPolicyCollections,
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
