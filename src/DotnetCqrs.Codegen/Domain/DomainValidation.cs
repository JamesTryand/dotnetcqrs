namespace DotnetCqrs.Codegen.Domain;

/// <summary>Thrown by <see cref="DomainValidation.Validate"/> when a <see cref="Domain"/>
/// cannot possibly generate — a hard gate, ports pocketcqrs's <c>scaffold.Domain.Validate</c>.</summary>
public sealed class DomainValidationException(IReadOnlyList<string> problems)
    : Exception($"domain: {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// <see cref="Validate"/> is a hard gate: reports every problem with a
/// <see cref="Domain"/> at once, so generating from it produces files that would fail
/// at save time points at the description that caused it, not at the generator.
/// <see cref="Warnings"/> reports what's merely UNFINISHED — a document routinely
/// specifies neither payloads nor outcome rules, and refusing those would make real
/// documents unimportable — named rather than buried in a generated comment.
/// </summary>
public static class DomainValidation
{
    private static readonly HashSet<string> SchemaTypes = ["text", "number", "bool", "date", "json"];

    public static void Validate(this Domain domain)
    {
        var problems = new List<string>();
        void Add(string message) => problems.Add(message);

        if (!Names.IsValidIdentifier(domain.Aggregate))
            Add($"aggregate name \"{domain.Aggregate}\" must be a letter followed by letters, digits or underscores");
        if (domain.Commands.Count == 0)
            Add("declare at least one command: an aggregate that accepts nothing can never produce an event");

        var seenCommands = new HashSet<string>();
        var creates = 0;
        foreach (var command in domain.Commands)
        {
            if (!Names.IsValidIdentifier(command.Name))
                Add($"command name \"{command.Name}\" must be a letter followed by letters, digits or underscores");
            if (!seenCommands.Add(command.Name))
                Add($"command \"{command.Name}\" is declared twice");
            if (command.Events.Count == 0)
                Add($"command \"{command.Name}\" records no event; a command that changes nothing is a query");

            foreach (var @event in command.Events)
            {
                if (!Names.IsValidIdentifier(@event.Name))
                    Add($"command \"{command.Name}\": event name \"{@event.Name}\" must be a letter followed by letters, digits or underscores");
                foreach (var field in @event.Fields)
                    ValidateField(Add, $"command \"{command.Name}\", event \"{@event.Name}\"", field);
            }

            if (command.Once) creates++;
            if (command.Once && command.RequiresExisting)
                Add($"command \"{command.Name}\" cannot be both the create and require an existing aggregate");

            foreach (var field in command.Fields)
                ValidateField(Add, $"command \"{command.Name}\"", field);
        }
        if (creates > 1)
            Add("more than one command is marked as the create; a stream has one beginning");

        // NOTE: an event produced by more than one command is NOT an error -- a
        // document may legitimately reference one event id from several slices.

        var seenCollections = new HashSet<string>();
        foreach (var readModel in domain.ReadModels)
        {
            if (!Names.IsValidIdentifier(readModel.Collection))
                Add($"read model collection \"{readModel.Collection}\" must be a letter followed by letters, digits or underscores");
            if (!seenCollections.Add(readModel.Collection))
                Add($"read model collection \"{readModel.Collection}\" is declared twice");
            if (!Names.IsValidIdentifier(readModel.Key))
                Add($"read model \"{readModel.Collection}\": key \"{readModel.Key}\" is not a valid field name");
            foreach (var field in readModel.Fields)
                ValidateField(Add, $"read model \"{readModel.Collection}\"", field);
        }

        var seenReactors = new HashSet<string>();
        foreach (var reactor in domain.Reactors)
        {
            if (!Names.IsValidIdentifier(reactor.Name))
                Add($"reactor name \"{reactor.Name}\" must be a letter followed by letters, digits or underscores");
            if (!seenReactors.Add(reactor.Name))
                Add($"reactor \"{reactor.Name}\" is declared twice");
            if (reactor.On.Count == 0)
                Add($"reactor \"{reactor.Name}\" declares no trigger events, so it can never fire");
            if (!Names.IsValidIdentifier(reactor.Aggregate))
                Add($"reactor \"{reactor.Name}\": target aggregate \"{reactor.Aggregate}\" is not a valid name");
            if (!Names.IsValidIdentifier(reactor.Command))
                Add($"reactor \"{reactor.Name}\": target command \"{reactor.Command}\" is not a valid name");
        }

        if (problems.Count > 0)
            throw new DomainValidationException(problems);
    }

    private static void ValidateField(Action<string> add, string owner, Field field)
    {
        if (!Names.IsValidIdentifier(field.Name))
            add($"{owner}: field name \"{field.Name}\" is not a valid identifier");
        if (!SchemaTypes.Contains(field.Type))
            add($"{owner}: field \"{field.Name}\" has type \"{field.Type}\"; use text, number, bool, date or json");
    }

    public static IReadOnlyList<string> Warnings(this Domain domain)
    {
        var warnings = new List<string>();

        foreach (var command in domain.Commands)
        {
            foreach (var @event in command.Events)
            {
                // "carries nothing" and "nobody said what it carries" must not look
                // alike -- an explicit NoFields is what tells them apart
                if (@event.Fields.Count == 0 && !@event.NoFields)
                    warnings.Add($"event \"{@event.Name}\" of command \"{command.Name}\" does not say what its payload is: " +
                                 "declare fields, or mark it as carrying none");
            }
            if (command.Events.Count > 1)
            {
                var names = string.Join(", ", command.Events.Select(e => e.Name));
                warnings.Add($"command \"{command.Name}\" can result in {command.Events.Count} different events " +
                             $"({names}) and needs an outcome rule written in the generated Decide; the generator emits the first");
            }
        }

        // a projection over events this aggregate does not produce is legitimate (read
        // models are cross-cutting) and is also what a typo looks like, so it is
        // named rather than refused or ignored
        var produced = domain.Events().ToHashSet();
        foreach (var readModel in domain.ReadModels)
            foreach (var on in readModel.On)
                if (!produced.Contains(on))
                    warnings.Add($"read model \"{readModel.Collection}\" listens for \"{on}\", which no command of " +
                                 $"\"{domain.Aggregate}\" produces: intended if it folds another aggregate's events, a typo otherwise");

        return warnings;
    }
}
