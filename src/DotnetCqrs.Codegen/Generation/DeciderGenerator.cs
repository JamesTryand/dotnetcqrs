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
        var hasPii = fields.Any(f => f.Pii);
        // An ended stream that no create may begin again needs its own flag: Exists alone
        // reads the same after the end as before the start. Only such aggregates carry it,
        // so every other decider generates exactly as before.
        var endsForGood = domain.Commands.SelectMany(c => c.Events).Any(e => e.EndsStream)
            && domain.Commands.Any(c => c.Once && !c.CanBeginAgain);
        var stateFlags = endsForGood ? "bool Exists, bool Ended" : "bool Exists";

        var b = new StringBuilder();
        b.AppendLine("using System.Text.Json;");
        if (hasPii) b.AppendLine("using DotnetCqrs.Crypto;");
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
        var stateFields = string.Join(", ", fields.Select(f => $"{GenerationSupport.FieldCSharpType(f.Type, f.Pii)}? {GenerationSupport.ExportName(f.Name)}"));
        b.AppendLine(fields.Count == 0
            ? $"public sealed record {aggregate}State({stateFlags});"
            : $"public sealed record {aggregate}State({stateFlags}, {stateFields});");
        b.AppendLine();
        // A hand-written outcome condition (schema 3.12.0 when: "hand") is a partial method the
        // host must implement, so the class is partial only when one exists.
        var hasHandOutcomes = domain.Commands.Any(c => c.Outcomes.Any(o => o.NeedsHand));
        b.AppendLine(hasHandOutcomes ? $"public static partial class {aggregate}Decider" : $"public static class {aggregate}Decider");
        b.AppendLine("{");
        b.AppendLine($"    public const string Aggregate = \"{domain.Aggregate}\";");
        b.AppendLine();
        b.AppendLine("    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };");
        b.AppendLine();
        b.AppendLine("    /// <summary>Builds the generated decider. Register at bootstrap:");
        if (hasPii)
        {
            b.AppendLine($"    /// <c>registry.Register({aggregate}Decider.Aggregate, {aggregate}Decider.Create(), new {aggregate}Decider.PiiProtector(kmsClient));</c>");
            b.AppendLine("    /// This aggregate carries field.pii values; without the protector the registry");
            b.AppendLine("    /// refuses to append them (fail closed) rather than storing plaintext.</summary>");
        }
        else
        {
            b.AppendLine($"    /// <c>registry.Register({aggregate}Decider.Aggregate, {aggregate}Decider.Create());</c></summary>");
        }
        b.AppendLine($"    public static Decider<{aggregate}State> Create() => new()");
        b.AppendLine("    {");
        var initArgs = string.Join(", ", Enumerable.Repeat("false", endsForGood ? 2 : 1).Concat(fields.Select(_ => "null")));
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
            if (command.Once && !command.CanBeginAgain && endsForGood)
                b.AppendLine($"                    if (state.Ended) throw new InvalidOperationException(\"{domain.Aggregate} has ended and cannot begin again\");");
            if (command.RequiresExisting)
                b.AppendLine($"                    if (!state.Exists) throw new InvalidOperationException(\"{domain.Aggregate} does not exist\");");
            if (command.OwnedBy is { } ownedBy)
            {
                // Schema 3.15.0: the aggregate decides who owns it, from its own state, never a read model.
                b.AppendLine("                    // requiredOwnership.stateField (schema 3.15.0): only the owner its own state names; refused as forbidden.");
                b.AppendLine($"                    if (!string.Equals(state.{GenerationSupport.ExportName(ownedBy.StateField)}, cmd.Actor, StringComparison.Ordinal)) throw new CommandForbiddenException({StringLiteral(ownedBy.Message)});");
            }
            AppendRefusals(b, command);
            if (command.Outcomes.Count > 0)
            {
                AppendOutcomes(b, aggregate, command);
                b.AppendLine("                }");
                continue;
            }
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
                if (chosen.Fields.Any(f => f.Pii))
                {
                    // Typed payload, serialised by the registry after PiiProtector has
                    // encrypted the fresh values -- Decide itself never touches ciphertext.
                    b.AppendLine($"                    return [NewEvent.Of({aggregate}Events.{chosen.Name}, payload)];");
                }
                else
                {
                    b.AppendLine($"                    return [new NewEvent({aggregate}Events.{chosen.Name}, JsonSerializer.Serialize(payload, JsonOptions))];");
                }
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
            // ScenarioNetExists, which folds the same rule at mapping time. Where a
            // create may not begin again, it also sets Ended, which that create refuses.
            var ends = EventEndsStream(domain, eventName);
            var existsLiteral = ends ? "false" : "true";
            if (ends && endsForGood) existsLiteral += ", Ended = true";
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
        var piiEvents = new List<(string TypeName, Domain.Event Event)>();
        foreach (var command in domain.Commands)
        {
            foreach (var @event in command.Events)
            {
                if (@event.Fields.Count == 0) continue;
                var typeName = GenerationSupport.ExportName(@event.Name) + "Payload";
                if (!emittedPayloadTypes.Add(typeName)) continue;
                // A PII payload property is nullable: an absent command field must land as null
                // (skipped by PiiProtector) rather than a default the protector would encrypt.
                // So is every other field: a json one, since a default JsonElement cannot be serialised (a
                // field the command does not carry would crash the decision), and a date, number or bool,
                // since a default would fold into state as a value (0001-01-01, 0, false) and a refusal's
                // "is set" would hold for an event that never carried it. Absent is null.
                var ctorFields = string.Join(", ", @event.Fields.Select(f => $"{GenerationSupport.FieldCSharpType(f.Type, f.Pii)}? {GenerationSupport.ExportName(f.Name)}"));
                b.AppendLine();
                b.AppendLine($"    private sealed record {typeName}({ctorFields});");
                if (@event.Fields.Any(f => f.Pii)) piiEvents.Add((typeName, @event));
            }
        }

        AppendOutcomeHooks(b, aggregate, domain);
        if (domain.Commands.Any(c => c.Refusals.Count > 0 || c.Outcomes.Any(o => o.When is not null))) AppendRefusalHelpers(b);
        if (hasPii) AppendPiiProtector(b, aggregate, fields, piiEvents);

        b.AppendLine("}");
        return new GeneratedFile($"{aggregate}Decider.cs", b.ToString());
    }

    /// <summary>Schema 3.10.0 <c>refuseWhen</c>: one <c>if</c> per rule, in declared order,
    /// after the existence checks, so the first rule that holds refuses with its own message.
    /// A rule reads the folded state and the command's raw payload, nothing else; the mapper
    /// has already checked every field it names.</summary>
    private static void AppendRefusals(StringBuilder b, Domain.Command command)
    {
        if (command.Refusals.Count == 0) return;
        b.AppendLine("                    // refuseWhen (schema 3.10.0), in order: the first rule that holds refuses.");
        if (ReadsCommand(command.Refusals.SelectMany(r => r.All)))
            b.AppendLine("                    var refusalCommand = RefusalCommand(cmd.Payload);");
        foreach (var refusal in command.Refusals)
        {
            var test = string.Join(" && ", refusal.All.Select(ConditionExpression));
            b.AppendLine($"                    if ({test}) throw new InvalidOperationException({StringLiteral(refusal.Message)});");
        }
    }

    private static bool ReadsCommand(IEnumerable<Domain.RefusalCondition> conditions) => conditions.Any(c =>
        c.Side == Domain.RefusalSide.Command || c.Operand?.Side == Domain.RefusalSide.Command);

    /// <summary>Schema 3.12.0 outcomes: after the refusals, one <c>if</c> per alternative in
    /// declared order, and the last (no condition) returned unconditionally, so the decision
    /// always has exactly one answer. A declared condition is refusal conditions (any group, all
    /// of its conditions); a hand-written one calls the hook <see cref="AppendOutcomeHooks"/>
    /// declares. Each event is filled from the command as any event is, then <c>set</c>'s
    /// values are applied to it.</summary>
    private static void AppendOutcomes(StringBuilder b, string aggregate, Domain.Command command)
    {
        b.AppendLine("                    // outcomes (schema 3.12.0), in order: the first whose condition holds is the result.");
        var conditions = command.Outcomes.SelectMany(o => o.When ?? []).SelectMany(g => g);
        if (ReadsCommand(conditions) && !ReadsCommand(command.Refusals.SelectMany(r => r.All)))
            b.AppendLine("                    var refusalCommand = RefusalCommand(cmd.Payload);");
        for (var i = 0; i < command.Outcomes.Count; i++)
        {
            var outcome = command.Outcomes[i];
            var test = outcome.ByHand
                ? $"{OutcomeHookName(command, i)}(state, cmd)"
                : outcome.When is { } groups
                    ? string.Join(" || ", groups.Select(g => "(" + string.Join(" && ", g.Select(ConditionExpression)) + ")"))
                    : null;
            var events = outcome.EventNames.Select(name => outcome.Repeated.Contains(name)
                ? RepeatedEvents(aggregate, command, i, command.Events.First(e => e.Name == name))
                : OutcomeEvent(aggregate, command.Events.First(e => e.Name == name),
                    outcome.Set.TryGetValue(name, out var values) ? values : null)).ToList();
            var indent = test is null ? "                    " : "                        ";
            var lines = events.Select(e => $"{indent}    {e},");
            if (test is not null)
                b.AppendLine($"                    if ({test})");
            if (events.Count == 0)
            {
                b.AppendLine($"{indent}return [];");
                continue;
            }
            b.AppendLine($"{indent}return");
            b.AppendLine($"{indent}[");
            foreach (var line in lines)
                b.AppendLine(line);
            b.AppendLine($"{indent}];");
        }
    }

    /// <summary>One event of an alternative: the command's payload reshaped into the event's own
    /// fields (as for a single event), with the alternative's fixed values on top.</summary>
    private static string OutcomeEvent(string aggregate, Domain.Event @event, IReadOnlyDictionary<string, object?>? set)
    {
        var type = $"{aggregate}Events.{@event.Name}";
        if (@event.Fields.Count == 0) return $"new NewEvent({type}, \"{{}}\")";
        var payload = $"JsonSerializer.Deserialize<{GenerationSupport.ExportName(@event.Name)}Payload>(cmd.Payload, JsonOptions)!";
        if (set is { Count: > 0 })
            payload = $"({payload} with {{ {string.Join(", ", set.Select(v => $"{GenerationSupport.ExportName(v.Key)} = {SetLiteral(v.Value, @event.Fields.First(f => f.Name == v.Key).Type)}"))} }})";
        return @event.Fields.Any(f => f.Pii)
            ? $"NewEvent.Of({type}, {payload})"
            : $"new NewEvent({type}, JsonSerializer.Serialize({payload}, JsonOptions))";
    }

    /// <summary>Schema 3.13.0: a repeated event, spread in its place: one per item the hook
    /// <see cref="AppendOutcomeHooks"/> declares returns, zero included.</summary>
    private static string RepeatedEvents(string aggregate, Domain.Command command, int index, Domain.Event @event)
    {
        var type = $"{aggregate}Events.{@event.Name}";
        var item = @event.Fields.Any(f => f.Pii)
            ? $"NewEvent.Of({type}, item)"
            : $"new NewEvent({type}, JsonSerializer.Serialize(item, JsonOptions))";
        return $".. {RepeatedHookName(command, index, @event.Name)}(state, cmd).Select(item => {item})";
    }

    private static string RepeatedHookName(Domain.Command command, int index, string eventName) =>
        $"{command.Name}Outcome{index}{eventName}";

    private static string SetLiteral(object? value, string fieldType) => value switch
    {
        string text => StringLiteral(text),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + (fieldType == "decimal" ? "m" : "d"),
        bool flag => flag ? "true" : "false",
        _ => "null",
    };

    private static string OutcomeHookName(Domain.Command command, int index) => $"{command.Name}Outcome{index}";

    /// <summary>A hand-written outcome condition (<c>when: "hand"</c>), and a repeated event's
    /// items (schema 3.13.0), are partial methods with no body here: the host must write them, or
    /// the decider does not compile. They see what Decide sees, the folded state and the command,
    /// and nothing else.</summary>
    private static void AppendOutcomeHooks(StringBuilder b, string aggregate, Domain.Domain domain)
    {
        foreach (var command in domain.Commands)
            for (var i = 0; i < command.Outcomes.Count; i++)
            {
                var outcome = command.Outcomes[i];
                if (outcome.ByHand)
                {
                    b.AppendLine();
                    b.AppendLine($"    /// <summary>Whether {command.Name} emits [{string.Join(", ", outcome.EventNames)}]:");
                    b.AppendLine("    /// a hand-written condition (schema 3.12.0 when: \"hand\"). Write it in a partial class beside");
                    b.AppendLine("    /// this one; it reads only the folded state and the command, like Decide.</summary>");
                    b.AppendLine($"    private static partial bool {OutcomeHookName(command, i)}({aggregate}State state, Command cmd);");
                }
                foreach (var name in outcome.Repeated)
                {
                    b.AppendLine();
                    b.AppendLine($"    /// <summary>The {name} events {command.Name} emits in outcome {i}, one per item, in order");
                    b.AppendLine("    /// (none is fine): repeated (schema 3.13.0), so hand-written. Write it in a partial class");
                    b.AppendLine("    /// beside this one; it reads only the folded state and the command, like Decide.</summary>");
                    b.AppendLine($"    private static partial IEnumerable<{GenerationSupport.ExportName(name)}Payload> {RepeatedHookName(command, i, name)}({aggregate}State state, Command cmd);");
                }
            }
    }

    private static string ConditionExpression(Domain.RefusalCondition c)
    {
        var raw = Raw(c.Side, c.Field);
        return c.Operator switch
        {
            "set" => $"{raw} is not null",
            "notSet" => $"{raw} is null",
            "equals" => $"RefusalEquals({Value(c.Side, c.Field)}, {Operand(c.Operand!)})",
            "notEquals" => $"!RefusalEquals({Value(c.Side, c.Field)}, {Operand(c.Operand!)})",
            "contains" => $"RefusalContains({raw}, {Operand(c.Operand!)})",
            // Schema 3.11.0: an unset value on either side compares as null, and null < 0 is false.
            "lessThan" => $"RefusalCompare({Value(c.Side, c.Field)}, {Operand(c.Operand!)}) < 0",
            "atMost" => $"RefusalCompare({Value(c.Side, c.Field)}, {Operand(c.Operand!)}) <= 0",
            "greaterThan" => $"RefusalCompare({Value(c.Side, c.Field)}, {Operand(c.Operand!)}) > 0",
            "atLeast" => $"RefusalCompare({Value(c.Side, c.Field)}, {Operand(c.Operand!)}) >= 0",
            _ => throw new InvalidOperationException($"unknown refusal operator \"{c.Operator}\""),
        };
    }

    /// <summary>The field as it is held: a state property, or the command payload's JSON value.</summary>
    private static string Raw(Domain.RefusalSide side, string field) => side == Domain.RefusalSide.State
        ? $"state.{GenerationSupport.ExportName(field)}"
        : $"RefusalField(refusalCommand, {StringLiteral(field)})";

    /// <summary>The field as a comparable value: a string, number (double or decimal) or bool, or
    /// null. The helpers compare numbers as decimals, whichever type each side has.</summary>
    private static string Value(Domain.RefusalSide side, string field) => side == Domain.RefusalSide.State
        ? $"(object?){Raw(side, field)}"
        : $"RefusalValue({Raw(side, field)})";

    private static string Operand(Domain.RefusalOperand operand) =>
        operand.Side is { } side ? Value(side, operand.Field!) : Literal(operand.Literal);

    private static string Literal(object? literal) => literal switch
    {
        string text => StringLiteral(text),
        double number => $"(object){number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}d",
        bool flag => flag ? "(object)true" : "(object)false",
        _ => "null",
    };

    /// <summary>A C# string literal: JSON's string escapes are all valid C# escapes.</summary>
    private static string StringLiteral(string text) => System.Text.Json.JsonSerializer.Serialize(text, LiteralOptions);

    // Relaxed, so an apostrophe or a non-ASCII letter stays readable in the generated code;
    // quotes, backslashes and control characters are still escaped.
    private static readonly System.Text.Json.JsonSerializerOptions LiteralOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void AppendRefusalHelpers(StringBuilder b)
    {
        b.AppendLine();
        b.AppendLine("    // refuseWhen (schema 3.10.0): a command field is read from the raw payload, and a");
        b.AppendLine("    // rule compares text, numbers and bools (a missing or null value is null).");
        b.AppendLine("    private static JsonElement RefusalCommand(string payload)");
        b.AppendLine("    {");
        b.AppendLine("        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? \"{}\" : payload);");
        b.AppendLine("        return document.RootElement.Clone();");
        b.AppendLine("    }");
        b.AppendLine();
        b.AppendLine("    private static JsonElement? RefusalField(JsonElement command, string name) =>");
        b.AppendLine("        command.ValueKind == JsonValueKind.Object && command.TryGetProperty(name, out var value)");
        b.AppendLine("            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;");
        b.AppendLine();
        b.AppendLine("    private static object? RefusalValue(JsonElement? value) => value?.ValueKind switch");
        b.AppendLine("    {");
        b.AppendLine("        JsonValueKind.String => value.Value.GetString(),");
        b.AppendLine("        JsonValueKind.Number => value.Value.GetDouble(),");
        b.AppendLine("        JsonValueKind.True => true,");
        b.AppendLine("        JsonValueKind.False => false,");
        b.AppendLine("        _ => null,");
        b.AppendLine("    };");
        b.AppendLine();
        b.AppendLine("    // A date (a state DateTime, or a command's text) equals another on the same calendar day.");
        b.AppendLine("    private static bool RefusalEquals(object? left, object? right) =>");
        b.AppendLine("        left is DateTime || right is DateTime ? RefusalDay(left) is { } day && RefusalDay(right) == day");
        b.AppendLine("        : RefusalNumber(left) is { } l ? Equals(l, RefusalNumber(right) ?? right)");
        b.AppendLine("        : left is null ? right is null : left.Equals(right);");
        b.AppendLine();
        b.AppendLine("    // A number on either side, a double (a number field, a literal, a command's JSON) or a decimal");
        b.AppendLine("    // (a decimal field), compares as a decimal: 56.7 equals 56.7m.");
        b.AppendLine("    private static decimal? RefusalNumber(object? value) => value switch");
        b.AppendLine("    {");
        b.AppendLine("        decimal m => m,");
        b.AppendLine("        double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,");
        b.AppendLine("        _ => null,");
        b.AppendLine("    };");
        b.AppendLine();
        b.AppendLine("    private static DateOnly? RefusalDay(object? value) => value switch");
        b.AppendLine("    {");
        b.AppendLine("        DateTime date => DateOnly.FromDateTime(date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date),");
        b.AppendLine("        string text when DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,");
        b.AppendLine("            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) => DateOnly.FromDateTime(parsed),");
        b.AppendLine("        _ => null,");
        b.AppendLine("    };");
        b.AppendLine();
        b.AppendLine("    // Schema 3.11.0: numbers compare; anything else (an unset value) compares as null, so no rule holds.");
        b.AppendLine("    private static int? RefusalCompare(object? left, object? right) =>");
        b.AppendLine("        RefusalNumber(left) is { } l && RefusalNumber(right) is { } r ? l.CompareTo(r) : null;");
        b.AppendLine();
        b.AppendLine("    private static bool RefusalContains(JsonElement? list, object? item) =>");
        b.AppendLine("        list is { ValueKind: JsonValueKind.Array } items && items.EnumerateArray().Any(e => RefusalEquals(RefusalValue(e), item));");
    }

    /// <summary>The per-aggregate <c>IPiiProtector</c>: I/O at the edges of the dispatch
    /// shell, never inside Decide/Evolve. <c>RevealAsync</c> decrypts every pending PII
    /// state field in one batched round trip (only ever called after a decision actually
    /// read one); <c>ProtectAsync</c> encrypts each fresh PII payload value under the value
    /// of its declared <c>piiSubject</c> sibling from the same payload — never the stream
    /// id, and never a default: a null subject is an error, not a fallback.</summary>
    private static void AppendPiiProtector(StringBuilder b, string aggregate,
        IReadOnlyList<(string Name, string Type, bool Pii)> stateFields, IReadOnlyList<(string TypeName, Domain.Event Event)> piiEvents)
    {
        var piiState = stateFields.Where(f => f.Pii).Select(f => GenerationSupport.ExportName(f.Name)).ToList();

        b.AppendLine();
        b.AppendLine("    /// <summary>Encrypts this aggregate's field.pii values after Decide, before they are");
        b.AppendLine("    /// appended, and reveals stored ones only when a decision reads them. Register it next");
        b.AppendLine("    /// to the decider; see <see cref=\"Create\"/>.");
        b.AppendLine("    /// <paramref name=\"subjects\"/> is optional: supply one (SubjectStatus over the event");
        b.AppendLine("    /// store) and this refuses to store new PII for an erased data subject, since a");
        b.AppendLine("    /// returning person is a new subject with a new id, never a reactivation of the old one.");
        b.AppendLine("    /// <paramref name=\"cache\"/> is optional too: the process's PiiRevealCache, so a decision");
        b.AppendLine("    /// that reads stored PII recently revealed elsewhere needs no round trip.</summary>");
        b.AppendLine("    public sealed class PiiProtector(IKmsClient kms, ISubjectStatus? subjects = null, PiiRevealCache? cache = null) : IPiiProtector");
        b.AppendLine("    {");
        b.AppendLine("        public async Task<object> RevealAsync(object state, CancellationToken ct)");
        b.AppendLine("        {");
        b.AppendLine($"            var s = ({aggregate}State)state;");
        b.AppendLine("            var buffer = new PiiRevealBuffer(kms, cache);");
        foreach (var name in piiState)
            b.AppendLine($"            var reveal{name} = s.{name}?.RevealAsync(buffer, ct);");
        b.AppendLine("            await buffer.FlushAsync(ct);");
        b.AppendLine("            return s with");
        b.AppendLine("            {");
        foreach (var name in piiState)
            b.AppendLine($"                {name} = reveal{name} is null ? null : await reveal{name},");
        b.AppendLine("            };");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        public async Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct)");
        b.AppendLine("        {");
        b.AppendLine("            var result = new List<NewEvent>(events.Count);");
        b.AppendLine("            foreach (var e in events)");
        b.AppendLine("            {");
        b.AppendLine("                switch (e.Payload)");
        b.AppendLine("                {");
        foreach (var (typeName, @event) in piiEvents)
        {
            b.AppendLine($"                    case {typeName} p:");
            b.AppendLine("                        result.Add(e with");
            b.AppendLine("                        {");
            b.AppendLine("                            Payload = p with");
            b.AppendLine("                            {");
            foreach (var field in @event.Fields.Where(f => f.Pii))
            {
                var prop = GenerationSupport.ExportName(field.Name);
                var subjectProp = GenerationSupport.ExportName(field.PiiSubject!);
                var subjectExpr = GenerationSupport.CSharpType(SubjectType(@event, field.PiiSubject!)) == "string"
                    ? $"p.{subjectProp}"
                    : $"p.{subjectProp}.ToString()";
                b.AppendLine($"                                {prop} = p.{prop} is null ? null : await p.{prop}.EncryptAsync(kms,");
                b.AppendLine($"                                    await SubjectAsync({subjectExpr}, \"{@event.Name}.{field.Name}\", \"{field.PiiSubject}\", ct), ct),");
            }
            b.AppendLine("                            },");
            b.AppendLine("                        });");
            b.AppendLine("                        break;");
        }
        b.AppendLine("                    default:");
        b.AppendLine("                        result.Add(e);");
        b.AppendLine("                        break;");
        b.AppendLine("                }");
        b.AppendLine("            }");
        b.AppendLine("            return result;");
        b.AppendLine("        }");
        b.AppendLine();
        b.AppendLine("        /// <summary>Resolves the subject id a value is encrypted under, and refuses an");
        b.AppendLine("        /// erased one. Without a guard (subjects is null) only the null check applies.</summary>");
        b.AppendLine("        private async Task<string> SubjectAsync(string? subjectId, string field, string subjectField, CancellationToken ct)");
        b.AppendLine("        {");
        b.AppendLine("            var id = subjectId ?? throw new InvalidOperationException($\"{field} is pii but its piiSubject {subjectField} is null\");");
        b.AppendLine("            if (subjects is not null && await subjects.IsErasedAsync(id, ct)) throw new SubjectErasedException(id);");
        b.AppendLine("            return id;");
        b.AppendLine("        }");
        b.AppendLine("    }");
    }

    private static string SubjectType(Domain.Event @event, string subjectName) =>
        @event.Fields.First(f => GenerationSupport.ExportName(f.Name) == GenerationSupport.ExportName(subjectName)).Type;


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
