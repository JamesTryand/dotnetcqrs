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

    /// <summary>When folded, resets the aggregate's synthesized <c>Exists</c> to
    /// <c>false</c> instead of <c>true</c> — the terminal event of a lifecycle that can
    /// legitimately begin again (unassign before a re-assign). Generator-synthesized
    /// state, not a schema concept of its own; see <c>eventmodelschema</c>'s
    /// <c>event.endsStream</c>.</summary>
    public bool EndsStream { get; init; }
}

/// <summary>One payload/column field. <c>Type</c> is one of a minimal, target-agnostic
/// set — <c>text</c>/<c>number</c>/<c>bool</c>/<c>date</c>/<c>json</c> — that either a
/// C# or a JS code generator maps onto its own concrete types (see
/// <see cref="FieldTypeFolding"/>). <c>Derivation</c> is only ever set on a read-model
/// field; a command/event field carries none.</summary>
public sealed record Field(string Name, string Type, Derivation? Derivation = null);

/// <summary>How a read-model field is computed as a fold over named (already
/// generator-resolved) event type names, instead of copied from a same-named payload
/// key — the domain-level counterpart of <see cref="Model.FieldDerivation"/>, with
/// every id already resolved to the generated event type name and every row-key default
/// already applied.</summary>
public abstract record Derivation;

public sealed record ToggleDerivation(IReadOnlyList<string> OnEvents, IReadOnlyList<string> OffEvents, bool Initial) : Derivation;

public sealed record CountDerivation(IReadOnlyList<string> IncrementOnEvents, IReadOnlyList<string> DecrementOnEvents, string RowKeyField) : Derivation;

public sealed record SumDerivation(IReadOnlyList<string> AddOnEvents, IReadOnlyList<string> SubtractOnEvents, string AmountField, string RowKeyField) : Derivation;

/// <summary>Grouped-rollup fold (schema 2.3.0): the field's own value is a nested list of
/// rows, one per distinct value of <see cref="GroupByField"/> (a contributing event's own
/// payload field). Each of <see cref="Subfields"/> is an ordinary <see cref="Field"/>
/// whose own <see cref="Derivation"/> (<see cref="CountDerivation"/>/
/// <see cref="SumDerivation"/> only -- see <see cref="Mapping.DocumentMapper"/>'s own doc
/// comment for why <see cref="ToggleDerivation"/> is rejected here) is computed within
/// that group's own row, reusing exactly the row-key mechanism <see cref="CountDerivation"/>/
/// <see cref="SumDerivation"/> already have (a subfield's own <c>RowKeyField</c> still
/// names which TOP-LEVEL read-model row the contributing event targets -- unrelated to
/// <see cref="GroupByField"/>, which only picks the entry WITHIN that row's list).</summary>
public sealed record GroupByDerivation(string GroupByField, IReadOnlyList<Field> Subfields) : Derivation;

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

    /// <summary>The subset of <see cref="On"/> whose OWN stream is this row — i.e.
    /// <c>ev.AggregateId</c> is a valid key for it, so these (and only these) get the
    /// generic row-seed insert and the plain-column/toggle copy. A <c>count</c>/<c>sum</c>
    /// derivation's events are in <see cref="On"/> (so the projection reacts to them at
    /// all) but never here — they live on a different stream, and are keyed by their
    /// own payload's <c>rowKeyField</c> instead (see <see cref="CountDerivation"/>/
    /// <see cref="SumDerivation"/>). Equal to <see cref="On"/> whenever a read model
    /// declares no derivation, which is every read model before this feature and
    /// exactly reproduces the generated code it already got.</summary>
    public List<string> SeedOn { get; } = [];

    /// <summary>Declared semi-joins for a query param that names no column of this read
    /// model — see <see cref="ReadModelScope"/>.</summary>
    public List<ReadModelScope> Scopes { get; } = [];

    /// <summary>Declared single-field WHERE-range filters (schema 2.4.0) — see
    /// <see cref="ReadModelFilter"/>.</summary>
    public List<ReadModelFilter> Filters { get; } = [];
}

/// <summary>One <c>readModel.scopes</c> entry, fully resolved: <c>ViaCollection</c> is
/// already the target physical collection/table name (resolved once, here, from the
/// document's own <c>via.readModelId</c> — generation never re-consults the document),
/// and the field names are the via-model's and this model's own, unresolved beyond
/// <see cref="Domain.Names.SanitizeName"/> since the generator snake-cases columns at
/// its own use site the same way it already does for every other field.</summary>
public sealed record ReadModelScope(string Param, string ViaCollection, string MatchParamToField, string SelectField, string FilterLocalField);

/// <summary>One <c>readModel.filters</c> entry, fully resolved (schema 2.4.0): a
/// single-field WHERE-range filter with named presets, sibling to
/// <see cref="ReadModelScope"/>'s semi-join. <c>Kind</c> is currently always
/// <c>"dateRange"</c> — the schema's own <c>filterKind</c> enum has one member so far —
/// carried through rather than hard-coded so a generator/harness consulting this record
/// fails loudly on an unrecognized future kind instead of silently mis-filtering.
/// <c>Presets</c> names which of <c>last7Days</c>/<c>lastCalendarMonth</c>/<c>custom</c>
/// this param accepts at query time; resolving one to concrete bounds is
/// <see cref="Generation.DateRangeResolver"/>'s job, not this record's.</summary>
public sealed record ReadModelFilter(string Param, string Field, string Kind, IReadOnlyList<string> Presets);

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
