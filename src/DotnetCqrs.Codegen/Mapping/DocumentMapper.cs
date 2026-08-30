using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Model;

namespace DotnetCqrs.Codegen.Mapping;

/// <summary>
/// Maps a validated <see cref="Document"/> onto <see cref="Domain.Domain"/> instances,
/// one per aggregate — ports pocketcqrs's <c>emschema.Map</c>. This is the importer's
/// whole job: <c>document -&gt; Domain</c>, never <c>document -&gt; C#</c> directly, so a
/// later code generator (and any other future front-end) can't grow a separate opinion
/// about what a decider looks like.
///
/// Referential integrity (does a referenced command/event/read-model id actually
/// exist?) is checked here rather than in a separate up-front pass the way pocketcqrs's
/// <c>emschema.Lint</c> does: Go's map indexing returns a silent zero-value on a
/// missing key, so pocketcqrs genuinely needs Lint to run first or Map would silently
/// process bogus data. C#'s <see cref="Dictionary{TKey,TValue}"/> forces a
/// <c>TryGetValue</c> check at every access point regardless, so folding the check into
/// mapping itself is a deliberate simplification, not an oversight. Per-pattern property
/// legality and per-pattern scenario-kind legality are also NOT re-checked here (unlike
/// <c>emschema.Lint</c>'s <c>lintSlice</c>/<c>lintScenarios</c>): Milestone 1's JSON
/// Schema validation already enforces both via the schema's own conditional
/// <c>allOf</c>/<c>if</c>/<c>then</c> shapes, and the discriminated-union deserialization
/// can't even construct a <see cref="StateChangeSlice"/> missing a required field.
/// </summary>
public sealed class DocumentMapper
{
    private readonly Document _document;
    private readonly MappingOptions _options;
    private readonly MappingReport _report;
    private readonly Dictionary<string, Domain.Domain> _byAggregate = [];

    // Remembers where each event id landed, so a read model or a reactor can find the
    // aggregate that owns its events.
    private readonly Dictionary<string, string> _eventAggregate = [];

    // Which aggregate's stream each event belongs to, resolved statically from every
    // slice's own command tag (not the incremental `_eventAggregate`, which only knows
    // about events from slices already mapped) -- IsCreate needs this for every given
    // event up front, including ones from slices later in document order.
    private Dictionary<string, string> _eventOwners = [];

    private DocumentMapper(Document document, MappingOptions options, MappingReport report)
    {
        _document = document;
        _options = options;
        _report = report;
    }

    /// <summary>Maps <paramref name="document"/>. Throws <see cref="DocumentMappingException"/>
    /// if it can't be mapped; check <see cref="MappingResult.Report"/> either way for
    /// warnings/lossy notes about decisions made on the document's behalf.</summary>
    public static MappingResult Map(Document document, MappingOptions? options = null)
    {
        var report = new MappingReport();
        var mapper = new DocumentMapper(document, options ?? new MappingOptions(), report);
        return mapper.Run();
    }

    private MappingResult Run()
    {
        CheckReferentialIntegrity();
        if (_report.HasErrors) throw new DocumentMappingException(_report);

        MapSlices();
        MapReadModels();
        if (_report.HasErrors) throw new DocumentMappingException(_report);

        var domains = new List<Domain.Domain>();
        foreach (var name in _byAggregate.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var domain = _byAggregate[name];
            try
            {
                domain.Validate();
            }
            catch (DomainValidationException ex)
            {
                _report.Error($"aggregate \"{name}\": {ex.Message}");
                continue;
            }
            _report.Warnings.AddRange(domain.Warnings());
            domains.Add(domain);
        }

        NotePii();
        NoteLossy();

        if (_report.HasErrors) throw new DocumentMappingException(_report);

        return new MappingResult { Domains = domains, Report = _report };
    }

    // ---- referential integrity (see the class doc comment for why this replaces a
    // separate Lint pass) ----

    private void CheckReferentialIntegrity()
    {
        var swimlaneIds = new HashSet<string>();
        foreach (var swimlane in _document.Swimlanes)
            if (!swimlaneIds.Add(swimlane.Id))
                _report.Error($"swimlane id \"{swimlane.Id}\" is declared twice");

        if (_document.Events is not null)
            foreach (var (id, ev) in _document.Events)
                if (!swimlaneIds.Contains(ev.SwimlaneId))
                    _report.Error($"event \"{id}\" references swimlane \"{ev.SwimlaneId}\", which does not exist");

        var sliceIds = new HashSet<string>();
        foreach (var slice in _document.Slices)
        {
            if (!sliceIds.Add(slice.Id))
                _report.Error($"slice id \"{slice.Id}\" is declared twice");
            CheckSlice(slice, swimlaneIds);
        }

        if (_document.ReadModels is not null)
            foreach (var (id, rm) in _document.ReadModels)
                foreach (var eventId in rm.BuiltFromEventIds ?? [])
                    if (!EventExists(eventId))
                        _report.Error($"read model \"{id}\" is built from event \"{eventId}\", which does not exist");
    }

    private bool EventExists(string id) => _document.Events?.ContainsKey(id) == true;
    private bool CommandExists(string id) => _document.Commands?.ContainsKey(id) == true;
    private bool ReadModelExists(string id) => _document.ReadModels?.ContainsKey(id) == true;
    private bool AutomationExists(string id) => _document.Automations?.ContainsKey(id) == true;
    private bool ChapterExists(string id) => _document.Chapters?.ContainsKey(id) == true;

    private void CheckSlice(Slice slice, HashSet<string> swimlaneIds)
    {
        if (!swimlaneIds.Contains(slice.SwimlaneId))
            _report.Error($"slice \"{slice.Id}\" references swimlane \"{slice.SwimlaneId}\", which does not exist");
        if (!string.IsNullOrEmpty(slice.ChapterId) && !ChapterExists(slice.ChapterId))
            _report.Error($"slice \"{slice.Id}\" references chapter \"{slice.ChapterId}\", which does not exist");

        switch (slice)
        {
            case StateChangeSlice s:
                if (!CommandExists(s.CommandId))
                    _report.Error($"slice \"{s.Id}\" references command \"{s.CommandId}\", which does not exist");
                foreach (var id in s.EventIds)
                    if (!EventExists(id))
                        _report.Error($"slice \"{s.Id}\" references event \"{id}\" in eventIds, which does not exist");
                break;
            case StateViewSlice s:
                if (!ReadModelExists(s.ReadModelId))
                    _report.Error($"slice \"{s.Id}\" references read model \"{s.ReadModelId}\", which does not exist");
                break;
            case AutomationSlice s:
                if (!AutomationExists(s.AutomationId))
                    _report.Error($"slice \"{s.Id}\" references automation \"{s.AutomationId}\", which does not exist");
                foreach (var id in s.TriggerEventIds)
                    if (!EventExists(id))
                        _report.Error($"slice \"{s.Id}\" references event \"{id}\" in triggerEventIds, which does not exist");
                if (!CommandExists(s.CommandId))
                    _report.Error($"slice \"{s.Id}\" references command \"{s.CommandId}\", which does not exist");
                foreach (var id in s.ResultEventIds)
                    if (!EventExists(id))
                        _report.Error($"slice \"{s.Id}\" references event \"{id}\" in resultEventIds, which does not exist");
                if (!string.IsNullOrEmpty(s.ReadModelId) && !ReadModelExists(s.ReadModelId))
                    _report.Error($"slice \"{s.Id}\" references read model \"{s.ReadModelId}\", which does not exist");
                break;
        }

        foreach (var scenario in slice.Scenarios)
            CheckScenario(slice.Id, scenario);
    }

    private void CheckScenario(string sliceId, Scenario scenario)
    {
        foreach (var given in scenario.Given)
            if (!EventExists(given.EventId))
                _report.Error($"scenario \"{scenario.Id}\" (slice \"{sliceId}\") gives event \"{given.EventId}\", which does not exist");

        switch (scenario)
        {
            case StateChangeScenario s:
                if (!CommandExists(s.When.CommandId))
                    _report.Error($"scenario \"{s.Id}\" uses command \"{s.When.CommandId}\", which does not exist");
                foreach (var e in s.Then.Events)
                    if (!EventExists(e.EventId))
                        _report.Error($"scenario \"{s.Id}\" expects event \"{e.EventId}\", which does not exist");
                break;
            case ErrorScenario s:
                if (!CommandExists(s.When.CommandId))
                    _report.Error($"scenario \"{s.Id}\" uses command \"{s.When.CommandId}\", which does not exist");
                break;
            case StateViewScenario s:
                if (!ReadModelExists(s.When.ReadModelId))
                    _report.Error($"scenario \"{s.Id}\" queries read model \"{s.When.ReadModelId}\", which does not exist");
                break;
        }
    }

    // ---- slice mapping ----

    private Domain.Domain GetOrCreateDomain(string aggregate)
    {
        if (!_byAggregate.TryGetValue(aggregate, out var domain))
        {
            domain = new Domain.Domain { Aggregate = aggregate };
            _byAggregate[aggregate] = domain;
        }
        return domain;
    }

    /// <summary>Resolves the aggregate owning an element, in precedence order: the
    /// document's own tag, then the operator's override, then a refusal naming the
    /// element.</summary>
    private (string Aggregate, bool Ok) AggregateFor(string kind, string id, string? tagged)
    {
        // lower-camel is this project's aggregate convention ("order", "supportTicket")
        // while the schema's tag is free-form prose, so the case is normalized here
        // rather than leaking a foreign convention into every generated file/route.
        if (!string.IsNullOrEmpty(tagged))
            return (Names.LowerFirst(Names.SanitizeName(tagged)), true);

        if (_options.AggregateOverrides.TryGetValue(id, out var overrideName) && !string.IsNullOrEmpty(overrideName))
        {
            _report.Warn($"{kind} \"{id}\" has no `aggregate` tag; using the supplied override \"{overrideName}\"");
            return (Names.LowerFirst(Names.SanitizeName(overrideName)), true);
        }

        _report.Error($"{kind} \"{id}\" has no `aggregate` tag and no override was supplied. " +
            "This project's write side is organised by aggregate and an automation's result events are " +
            $"real log entries, so something must own the stream. Supply an aggregate override for \"{id}\", " +
            "or add the tag to the document; deriving one from the swimlane would silently merge unrelated stream families");
        return ("", false);
    }

    private void MapSlices()
    {
        _eventOwners = BuildEventOwners();

        // stateChange slices first: an automation's dispatched command is only a
        // create when its target aggregate has no other beginning (see
        // MapAutomation), so every stateChange-derived create must already be
        // registered before the automation pass runs.
        foreach (var slice in _document.Slices.OfType<StateChangeSlice>())
            MapStateChange(slice);
        foreach (var slice in _document.Slices.OfType<AutomationSlice>())
            MapAutomation(slice);
        // StateViewSlice: the read model itself is mapped separately; the slice
        // adds only a screen, which has no runtime concept here.
    }

    /// <summary>Resolves, for every event a stateChange or automation slice produces,
    /// which aggregate's stream it belongs to -- statically, from each slice's own
    /// command tag, independent of document order. <see cref="IsCreate"/> needs this to
    /// tell a scenario's own-aggregate `given` (real evidence the stream already
    /// exists) apart from a cross-aggregate precondition (a different stream's event,
    /// which says nothing about this one).</summary>
    private Dictionary<string, string> BuildEventOwners()
    {
        var owners = new Dictionary<string, string>();

        void Assign(string aggregate, IReadOnlyList<string> eventIds)
        {
            foreach (var id in eventIds)
                owners[id] = aggregate;
        }

        foreach (var slice in _document.Slices.OfType<StateChangeSlice>())
        {
            if (!TryGetCommand(slice.CommandId, out var cmd)) continue;
            var (aggregate, ok) = AggregateFor("command", slice.CommandId, cmd.Aggregate);
            if (ok) Assign(aggregate, slice.EventIds);
        }
        foreach (var slice in _document.Slices.OfType<AutomationSlice>())
        {
            if (!TryGetCommand(slice.CommandId, out var cmd)) continue;
            var (aggregate, ok) = AggregateFor("command", slice.CommandId, cmd.Aggregate);
            if (ok) Assign(aggregate, slice.ResultEventIds);
        }

        return owners;
    }

    private void MapStateChange(StateChangeSlice slice)
    {
        if (!TryGetCommand(slice.CommandId, out var cmd)) return;
        var (aggregate, ok) = AggregateFor("command", slice.CommandId, cmd.Aggregate);
        if (!ok) return;

        GetOrCreateDomain(aggregate).Commands.Add(BuildCommand(aggregate, slice.CommandId, cmd, slice.EventIds, IsCreate(slice, aggregate)));
    }

    /// <summary>Turns an automation slice into a reactor plus the command it
    /// dispatches — event(s) → command → event(s), the multi-aggregate "saga" shape.</summary>
    private void MapAutomation(AutomationSlice slice)
    {
        if (!TryGetCommand(slice.CommandId, out var cmd)) return;
        var (aggregate, ok) = AggregateFor("command", slice.CommandId, cmd.Aggregate);
        if (!ok) return;

        // the reactor belongs to whichever aggregate OWNS the trigger events -- that's
        // the stream it consumes, and it may well not be the target
        var source = aggregate;
        foreach (var id in slice.TriggerEventIds)
        {
            if (_eventAggregate.TryGetValue(id, out var owner))
            {
                source = owner;
                break;
            }
        }

        // An automation dispatching ACROSS aggregates derives the target id from the
        // source event, so it opens a new target stream per fire -- but only when this
        // slice's own scenario evidence says so, same rule as IsCreate uses for a
        // directly-invoked command (see its doc comment): a `given` event on the
        // TARGET aggregate's own stream is real evidence the stream already exists
        // (the fan-out-lock case: log an entry, then an invoice reaction locks it);
        // foreign-aggregate given events (the trigger's own history) are not. This
        // also means two independent automations/commands can each genuinely create
        // the same aggregate TYPE (e.g. two different triggers each raising a fresh
        // "notification" instance) without one disqualifying the other -- unlike a
        // single "has this aggregate got a create yet" flag would. An automation whose
        // target is its own trigger's aggregate (auto-ship an order) is never a create
        // either.
        var crossAggregate = source != aggregate;
        var target = GetOrCreateDomain(aggregate);
        var isCreate = crossAggregate && IsCreate(slice, aggregate);
        target.Commands.Add(BuildCommand(aggregate, slice.CommandId, cmd, slice.ResultEventIds, isCreate));

        var triggers = slice.TriggerEventIds.Select(EventTypeName).ToList();
        if (!string.IsNullOrEmpty(slice.ReadModelId))
            _report.Warn($"automation \"{slice.Id}\" consults read model \"{slice.ReadModelId}\"; the generated " +
                "reaction does not read it -- the author adds that access if the rule needs it");

        // Same-aggregate automations (auto-ship: order -> order) dispatch back into the
        // stream that already exists -- prefixing it would build an id nothing ever
        // created. Only a cross-aggregate dispatch needs a prefix, both to derive a new
        // target instance per trigger and to avoid colliding with another trigger stream
        // that happens to share the same raw id.
        var reactor = new Reactor
        {
            Name = Names.LowerFirst(Names.SanitizeName(Names.TypeName(slice.Name, slice.Id))),
            Aggregate = aggregate,
            Command = CommandName(slice.CommandId),
            IdPrefix = crossAggregate ? Names.DeriveId(Names.TypeName(slice.Name, slice.Id)) + "-" : null,
        };
        reactor.On.AddRange(triggers);
        GetOrCreateDomain(source).Reactors.Add(reactor);
    }

    private Domain.Command BuildCommand(string aggregate, string id, CommandDef cmd, IReadOnlyList<string> eventIds, bool once)
    {
        var command = new Domain.Command
        {
            Name = CommandName(id),
            Once = once,
            RequiresExisting = !once,
        };
        command.Fields.AddRange(BuildFields($"command \"{id}\"", cmd.Fields ?? []));

        foreach (var eventId in eventIds)
        {
            if (!TryGetEvent(eventId, out var eventDef)) continue;
            _eventAggregate[eventId] = aggregate;

            var hasFields = eventDef.Fields is { Count: > 0 };
            var domainEvent = new Domain.Event { Name = EventTypeName(eventId), NoFields = !hasFields };
            if (hasFields)
            {
                domainEvent.Fields.AddRange(BuildFields($"event \"{eventId}\"", eventDef.Fields!));
            }
            else
            {
                // the schema makes `fields` optional throughout, so this is the common
                // case for a v1-era document; saying "no payload" out loud is what
                // stops it reading as "nobody has specified this"
                _report.Warn($"event \"{eventId}\" declares no fields; generated as carrying no payload");
            }
            command.Events.Add(domainEvent);
        }
        return command;
    }

    private List<Domain.Field> BuildFields(string owner, IReadOnlyList<Model.Field> fields)
    {
        var result = new List<Domain.Field>(fields.Count);
        foreach (var field in fields)
        {
            var note = FieldTypeFolding.Note(owner, field);
            if (note is not null) _report.Warn(note);
            result.Add(new Domain.Field(Names.SanitizeName(field.Name), FieldTypeFolding.Fold(field)));
        }
        return result;
    }

    // ---- read models ----

    /// <summary>Attaches each read model to the aggregate owning the events it folds. A
    /// read model spanning several is legitimate — the schema gives them no aggregate
    /// precisely because they're cross-cutting — so it lands on the first (alphabetical)
    /// owner and the span is reported.</summary>
    private void MapReadModels()
    {
        if (_document.ReadModels is null) return;

        foreach (var id in _document.ReadModels.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var rm = _document.ReadModels[id];
            var owners = new HashSet<string>();
            var on = new List<string>();
            foreach (var eventId in rm.BuiltFromEventIds ?? [])
            {
                on.Add(EventTypeName(eventId));
                if (_eventAggregate.TryGetValue(eventId, out var owner))
                    owners.Add(owner);
            }
            if (on.Count == 0)
            {
                _report.Warn($"read model \"{id}\" lists no builtFromEventIds, so the generated projection would never fire; skipped");
                continue;
            }

            var chosenOwner = owners.Count == 0 ? null : owners.OrderBy(o => o, StringComparer.Ordinal).First();
            if (chosenOwner is null)
            {
                _report.Error($"read model \"{id}\" folds events owned by no aggregate this document defines");
                continue;
            }
            if (owners.Count > 1)
                _report.Warn($"read model \"{id}\" folds events from {owners.Count} aggregates; its projection is generated " +
                    $"under \"{chosenOwner}\" but listens to all of them, which is what a cross-cutting read model means");

            var (key, keyNote) = ReadModelKey(id, rm, chosenOwner);
            if (keyNote is not null) _report.Warn(keyNote);

            var readModel = new Domain.ReadModel { Collection = Names.SanitizeName(CollectionName(rm.Name, id)), Key = key };
            readModel.Fields.AddRange(BuildFields($"read model \"{id}\"", rm.Fields ?? []));
            readModel.On.AddRange(on);
            GetOrCreateDomain(chosenOwner).ReadModels.Add(readModel);
        }
    }

    /// <summary>Picks the row key. <c>idAttribute</c> names the field carrying identity
    /// — exactly what the projection keys on — falling back to the aggregate id.</summary>
    private static (string Key, string? Note) ReadModelKey(string id, ReadModelDef rm, string owner)
    {
        foreach (var field in rm.Fields ?? [])
            if (field.IdAttribute == true)
                return (Names.SanitizeName(field.Name), null);

        var fallback = owner + "Id";
        return (fallback, $"read model \"{id}\" marks no field with idAttribute; keying rows on \"{fallback}\" (the aggregate id)");
    }

    private static string CollectionName(string name, string id) => Names.LowerFirst(Names.TypeName(name, id));

    // ---- helpers ----

    private bool TryGetCommand(string id, out CommandDef cmd)
    {
        cmd = null!;
        if (_document.Commands is null || !_document.Commands.TryGetValue(id, out var found)) return false;
        cmd = found;
        return true;
    }

    private bool TryGetEvent(string id, out EventDef ev)
    {
        ev = null!;
        if (_document.Events is null || !_document.Events.TryGetValue(id, out var found)) return false;
        ev = found;
        return true;
    }

    private string EventTypeName(string id) =>
        TryGetEvent(id, out var ev) ? Names.TypeName(ev.Name, id) : Names.TypeName(null, id);

    private string CommandName(string id) =>
        TryGetCommand(id, out var cmd) ? Names.TypeName(cmd.Name, id) : Names.TypeName(null, id);

    /// <summary>A scenario is evidence this command opens a fresh stream when none of
    /// its `given` events belong to THIS slice's own aggregate -- an empty `given` is
    /// the obvious case, but a `given` that's entirely a different aggregate's events
    /// (a normal cross-aggregate precondition, e.g. "the project exists") says nothing
    /// about whether this aggregate's own stream already has history. Only a given
    /// event on the slice's own stream is real evidence against create. Where no
    /// scenario qualifies, the slice's command is not treated as the create. Shared by
    /// both a directly-invoked command (<see cref="MapStateChange"/>) and an
    /// automation's dispatched command (<see cref="MapAutomation"/>) -- both slice
    /// kinds carry the same `given`-bearing scenarios.</summary>
    private bool IsCreate(Slice slice, string aggregate) =>
        slice.Scenarios.OfType<StateChangeScenario>().Any(s =>
            s.Given.All(g => !_eventOwners.TryGetValue(g.EventId, out var owner) || owner != aggregate));

    // ---- lossy notes ----

    private void NotePii()
    {
        var flagged = new List<string>();
        void Walk(string owner, IReadOnlyList<Model.Field>? fields)
        {
            void Recurse(string prefix, IReadOnlyList<Model.Field>? fs)
            {
                if (fs is null) return;
                foreach (var f in fs)
                {
                    if (f.Pii == true) flagged.Add($"{owner}.{prefix}{f.Name}");
                    Recurse($"{prefix}{f.Name}.", f.Subfields);
                }
            }
            Recurse("", fields);
        }

        if (_document.Events is not null)
            foreach (var (id, e) in _document.Events) Walk($"event {id}", e.Fields);
        if (_document.Commands is not null)
            foreach (var (id, c) in _document.Commands) Walk($"command {id}", c.Fields);
        if (_document.ReadModels is not null)
            foreach (var (id, rm) in _document.ReadModels) Walk($"read model {id}", rm.Fields);

        if (flagged.Count > 0)
        {
            flagged.Sort(StringComparer.Ordinal);
            _report.Note($"{flagged.Count} field(s) are marked pii and NOTHING here carries that flag: " +
                $"{string.Join(", ", flagged)}. They are stored as ordinary columns; treat them accordingly");
        }
    }

    private void NoteLossy()
    {
        if (_document.Chapters is { Count: > 0 })
            _report.Note($"{_document.Chapters.Count} chapter(s) are design-time grouping with no runtime concept here");
        if (_document.ActorLanes is { Count: > 0 })
            _report.Note($"{_document.ActorLanes.Count} actor lane(s) are design-time notation with no runtime concept here");
        if (_document.Hotspots is { Count: > 0 })
            _report.Note($"{_document.Hotspots.Count} hotspot(s) are open questions about the model; not carried into generated code");
        if (_document.Screens is { Count: > 0 })
            _report.Note($"{_document.Screens.Count} screen(s) have no runtime concept here");

        var statuses = _document.Slices.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count());
        if (statuses.Count > 0)
        {
            var parts = statuses.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}×{kv.Value}");
            _report.Note($"slice status is board state and is not preserved ({string.Join(", ", parts)})");
        }

        var optional = _document.Events?.Values.Sum(e => e.Fields?.Count(f => f.Optional == true) ?? 0) ?? 0;
        if (optional > 0)
            _report.Note($"{optional} field(s) are marked optional; not yet expressed in generated code, so they become ordinary columns");
    }
}
