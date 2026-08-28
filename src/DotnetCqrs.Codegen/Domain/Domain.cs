namespace DotnetCqrs.Codegen.Domain;

/// <summary>
/// The intermediate model a document maps onto: what a slice IS, independent of the
/// document format that described it and of the code a later milestone generates from
/// it. Ports pocketcqrs's <c>scaffold.Domain</c> (its own doc comment: "generating from
/// the model, not from the input format, is what keeps [multiple front-ends] from
/// growing separate opinions").
///
/// THE MODEL RECORDS WHAT CAN RESULT, NOT HOW THE RESULT IS CHOSEN: a command may list
/// several events; nothing here says which one applies when, because that's business
/// logic and belongs in the generated <c>Decide</c> body, which the author edits.
///
/// Mutable, unlike <c>Model</c>'s records: <see cref="Mapping.DocumentMapper"/> builds
/// these up incrementally while walking a document's slices, appending to one
/// aggregate's lists across multiple, non-adjacent slices.
/// </summary>
public sealed class Domain
{
    public required string Aggregate { get; init; }
    public List<Command> Commands { get; } = [];
    public List<ReadModel> ReadModels { get; } = [];
    public List<Reactor> Reactors { get; } = [];

    /// <summary>Every event the commands may produce, flattened across commands in
    /// declaration order and de-duplicated.</summary>
    public IReadOnlyList<string> Events()
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var command in Commands)
            foreach (var @event in command.Events)
                if (seen.Add(@event.Name))
                    result.Add(@event.Name);
        return result;
    }
}

/// <summary>One intent and the events it may record.</summary>
public sealed class Command
{
    public required string Name { get; init; }

    /// <summary>Events this command MAY append. More than one covers both shapes a
    /// document can describe (conjunction: all of them together; disjunction: one of
    /// them by state) — indistinguishable in the model on purpose, since choosing
    /// between them is the business logic the generated code's author writes.</summary>
    public List<Event> Events { get; } = [];

    public List<Field> Fields { get; } = [];

    /// <summary>May only succeed on a fresh stream — the "create" of the aggregate;
    /// generated code refuses a repeat.</summary>
    public bool Once { get; init; }

    /// <summary>Needs the aggregate to already exist.</summary>
    public bool RequiresExisting { get; init; }
}

/// <summary>One event a command may record. Fields are never inherited implicitly from
/// the command — "carries nothing" (<see cref="NoFields"/>) and "nobody said what it
/// carries" (empty <see cref="Fields"/>, <see cref="NoFields"/> false) must not look
/// alike.</summary>
public sealed class Event
{
    public required string Name { get; init; }
    public List<Field> Fields { get; } = [];
    public bool NoFields { get; init; }
}

/// <summary>One payload/column field. <c>Type</c> is one of a minimal, target-agnostic
/// set — <c>text</c>/<c>number</c>/<c>bool</c>/<c>date</c>/<c>json</c> — that either a
/// C# or a JS code generator maps onto its own concrete types (see
/// <see cref="FieldTypeFolding"/>).</summary>
public sealed record Field(string Name, string Type);

/// <summary>A projection's target read-model table.</summary>
public sealed class ReadModel
{
    public required string Collection { get; init; }

    /// <summary>The field carrying row identity — the aggregate id, in the generated
    /// projection, so one row per stream.</summary>
    public required string Key { get; init; }

    public List<Field> Fields { get; } = [];

    /// <summary>Event names that update the row.</summary>
    public List<string> On { get; } = [];
}

/// <summary>Maps events to a command on another aggregate — the automation shape.</summary>
public sealed class Reactor
{
    /// <summary>The generated file's basename and the durable checkpoint suffix.</summary>
    public required string Name { get; init; }

    /// <summary>Event type names that trigger it.</summary>
    public List<string> On { get; } = [];

    public required string Aggregate { get; init; }
    public required string Command { get; init; }

    /// <summary>Prefixes the source aggregate id to build the target id (e.g.
    /// <c>"fulfill-"</c> → <c>"fulfill-&lt;orderId&gt;"</c>). Deterministic on purpose: a
    /// replay then hits the target's own "already exists" rule. Null for a same-aggregate
    /// automation (e.g. auto-ship: order -> order) — the target is the trigger's own
    /// existing stream, so the id must be the bare source aggregate id, not a derived
    /// one nothing created.</summary>
    public string? IdPrefix { get; init; }
}
