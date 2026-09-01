using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates one decider file per aggregate — ports pocketcqrs's <c>deciderGo</c>,
/// adapted to dotnetcqrs's actual shapes rather than a transliteration: immutable
/// record <c>State</c> with <c>with</c>-expression evolution (dotnetcqrs's
/// <c>Decider&lt;TState&gt;</c> is functional, unlike pocketcqrs's mutable-struct Go
/// decider), and a per-event payload record used to reshape a command's payload into
/// that event's own declared fields (the same "pick just these fields" trick pocketcqrs
/// gets from an anonymous struct + unmarshal/remarshal). THE SHAPE IS RIGHT, THE RULES
/// ARE YOURS: a multi-event command emits the first event as a runnable placeholder,
/// same as pocketcqrs — see the generated file's own comment at that call site.
/// </summary>
internal static class DeciderGenerator
{
    public static GeneratedFile Generate(Domain.Domain domain)
    {
        var aggregate = GenerationSupport.ExportName(domain.Aggregate);
        var events = domain.Events();
        var (fields, _) = GenerationSupport.CollectEventFields(domain);

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        b.AppendLine("using DotnetCqrs.Deciders;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{aggregate};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Event types \"{domain.Aggregate}\" produces.</summary>");
        b.AppendLine($"public static class {aggregate}Events");
        b.AppendLine("{");
        foreach (var name in events)
            b.AppendLine($"    public const string {name} = \"{name}\";");
        b.AppendLine("}");
        b.AppendLine();
        b.AppendLine("/// <summary>");
        b.AppendLine($"/// State is \"{domain.Aggregate}\"'s generated read side for Decide/Evolve. THE SHAPE IS");
        b.AppendLine("/// RIGHT, THE RULES ARE YOURS: fields come straight from the declared event payloads,");
        b.AppendLine("/// unioned across every event this aggregate produces. Dry-run and test this against");
        b.AppendLine("/// real history before relying on it.");
        b.AppendLine("/// </summary>");
        var stateFields = string.Join(", ", fields.Select(f => $"{GenerationSupport.CSharpType(f.Type)}? {GenerationSupport.ExportName(f.Name)}"));
        b.AppendLine(fields.Count == 0
            ? $"public sealed record {aggregate}State(bool Exists);"
            : $"public sealed record {aggregate}State(bool Exists, {stateFields});");
        b.AppendLine();
        b.AppendLine($"public static class {aggregate}Decider");
        b.AppendLine("{");
        b.AppendLine($"    public const string Aggregate = \"{domain.Aggregate}\";");
        b.AppendLine();
        b.AppendLine("    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };");
        b.AppendLine();
        b.AppendLine("    /// <summary>Builds the generated decider. Register at bootstrap:");
        b.AppendLine($"    /// <c>registry.Register({aggregate}Decider.Aggregate, {aggregate}Decider.Create());</c></summary>");
        b.AppendLine($"    public static Decider<{aggregate}State> Create() => new()");
        b.AppendLine("    {");
        var initArgs = string.Join(", ", Enumerable.Repeat("false", 1).Concat(fields.Select(_ => "null")));
        b.AppendLine($"        InitialState = () => new {aggregate}State({initArgs}),");
        b.AppendLine("        Decide = (state, cmd) =>");
        b.AppendLine("        {");
        b.AppendLine("            switch (cmd.Name)");
        b.AppendLine("            {");
        foreach (var command in domain.Commands)
        {
            b.AppendLine($"                case \"{command.Name}\":");
            b.AppendLine("                {");
            if (command.Once)
                b.AppendLine($"                    if (state.Exists) throw new InvalidOperationException(\"{domain.Aggregate} already exists\");");
            if (command.RequiresExisting)
                b.AppendLine($"                    if (!state.Exists) throw new InvalidOperationException(\"{domain.Aggregate} does not exist\");");
            if (command.Events.Count > 1)
            {
                b.AppendLine("                    // THIS COMMAND CAN RESULT IN MORE THAN ONE EVENT:");
                b.AppendLine($"                    //   {string.Join(", ", command.Events.Select(e => e.Name))}");
                b.AppendLine("                    // Which one applies is the domain rule, and only you can write it.");
                b.AppendLine("                    // The code below returns the first as a placeholder so the slice runs;");
                b.AppendLine("                    // replace it with the real decision.");
            }
            var chosen = command.Events[0]; // Validate() guarantees at least one
            if (chosen.Fields.Count == 0)
            {
                b.AppendLine($"                    return [new NewEvent({aggregate}Events.{chosen.Name}, \"{{}}\")];");
            }
            else
            {
                var payloadType = GenerationSupport.ExportName(chosen.Name) + "Payload";
                b.AppendLine($"                    var payload = JsonSerializer.Deserialize<{payloadType}>(cmd.Payload, JsonOptions)!;");
                b.AppendLine($"                    return [new NewEvent({aggregate}Events.{chosen.Name}, JsonSerializer.Serialize(payload, JsonOptions))];");
            }
            b.AppendLine("                }");
        }
        b.AppendLine("                default:");
        b.AppendLine("                    throw new InvalidOperationException($\"unknown command: {cmd.Name}\");");
        b.AppendLine("            }");
        b.AppendLine("        },");
        b.AppendLine("        Evolve = (state, ev) =>");
        b.AppendLine("        {");
        b.AppendLine("            switch (ev.Type)");
        b.AppendLine("            {");
        foreach (var eventName in events)
        {
            var eventFields = EventFields(domain, eventName);
            // A terminal event (endsStream) resets Exists to false instead of setting
            // it true, so Once/RequiresExisting keep working across a full
            // assign -> unassign -> re-assign lifecycle -- see DocumentMapper's
            // ScenarioNetExists, which folds the same rule at mapping time.
            var existsLiteral = EventEndsStream(domain, eventName) ? "false" : "true";
            b.AppendLine($"                case {aggregate}Events.{eventName}:");
            if (eventFields.Count == 0)
            {
                b.AppendLine($"                    return state with {{ Exists = {existsLiteral} }};");
            }
            else
            {
                var payloadType = GenerationSupport.ExportName(eventName) + "Payload";
                b.AppendLine("                {");
                b.AppendLine($"                    var data = JsonSerializer.Deserialize<{payloadType}>(ev.Data, JsonOptions)!;");
                var withFields = string.Join(", ", eventFields.Select(f => $"{GenerationSupport.ExportName(f.Name)} = data.{GenerationSupport.ExportName(f.Name)}"));
                b.AppendLine($"                    return state with {{ Exists = {existsLiteral}, {withFields} }};");
                b.AppendLine("                }");
            }
        }
        b.AppendLine("                default:");
        b.AppendLine("                    return state;");
        b.AppendLine("            }");
        b.AppendLine("        },");
        b.AppendLine("    };");

        var emittedPayloadTypes = new HashSet<string>();
        foreach (var command in domain.Commands)
        {
            foreach (var @event in command.Events)
            {
                if (@event.Fields.Count == 0) continue;
                var typeName = GenerationSupport.ExportName(@event.Name) + "Payload";
                if (!emittedPayloadTypes.Add(typeName)) continue;
                var ctorFields = string.Join(", ", @event.Fields.Select(f => $"{GenerationSupport.CSharpType(f.Type)} {GenerationSupport.ExportName(f.Name)}"));
                b.AppendLine();
                b.AppendLine($"    private sealed record {typeName}({ctorFields});");
            }
        }

        b.AppendLine("}");
        return new GeneratedFile($"{aggregate}Decider.cs", b.ToString());
    }

    /// <summary>Fields the FIRST event named <paramref name="eventName"/> declares
    /// (across every command) — events sharing a name are expected to share a shape.</summary>
    private static IReadOnlyList<Domain.Field> EventFields(Domain.Domain domain, string eventName)
    {
        foreach (var command in domain.Commands)
            foreach (var @event in command.Events)
                if (@event.Name == eventName)
                    return @event.Fields;
        return [];
    }

    /// <summary>Whether the FIRST event named <paramref name="eventName"/> declares
    /// (across every command) is marked <c>endsStream</c> — mirrors <see cref="EventFields"/>.</summary>
    private static bool EventEndsStream(Domain.Domain domain, string eventName)
    {
        foreach (var command in domain.Commands)
            foreach (var @event in command.Events)
                if (@event.Name == eventName)
                    return @event.EndsStream;
        return false;
    }
}
