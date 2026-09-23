using System.Text.Json;
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
    // about events from slices already mapped) -- ScenarioNetExists needs this for
    // every given event up front, including ones from slices later in document order.
    private Dictionary<string, string> _eventOwners = [];

    // Every event id the document marks `endsStream: true` -- ScenarioNetExists needs
    // this to fold a scenario's `given` in order rather than merely scan it for any
    // own-stream event (see its doc comment).
    private HashSet<string> _endsStreamEvents = [];

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
            {
                foreach (var eventId in rm.BuiltFromEventIds ?? [])
                    if (!EventExists(eventId))
                        _report.Error($"read model \"{id}\" is built from event \"{eventId}\", which does not exist");
                foreach (var scope in rm.Scopes ?? [])
                    if (!ReadModelExists(scope.Via.ReadModelId))
                        _report.Error($"read model \"{id}\" scope on param \"{scope.Param}\" references " +
                            $"read model \"{scope.Via.ReadModelId}\", which does not exist");
            }
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
        _endsStreamEvents = BuildEndsStreamEvents();

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

    /// <summary>Every event id the document marks <c>endsStream: true</c> — see
    /// <see cref="ScenarioNetExists"/>.</summary>
    private HashSet<string> BuildEndsStreamEvents()
    {
        var ids = new HashSet<string>();
        if (_document.Events is not null)
            foreach (var (id, ev) in _document.Events)
                if (ev.EndsStream == true)
                    ids.Add(id);
        return ids;
    }

    private void MapStateChange(StateChangeSlice slice)
    {
        if (!TryGetCommand(slice.CommandId, out var cmd)) return;
        var (aggregate, ok) = AggregateFor("command", slice.CommandId, cmd.Aggregate);
        if (!ok) return;

        // A command with scenario evidence for BOTH cases -- a fresh stream (see
        // HasCreateEvidence) and an already-existing one (HasUpdateEvidence) -- is a
        // real upsert: neither flag is set, so DeciderGenerator emits no existence
        // check at all and the command always succeeds. Without update evidence it's
        // the ordinary create-only rule (see IsCreate's doc comment); without create
        // evidence, the pre-existing default (RequiresExisting) is unchanged.
        var hasCreate = HasCreateEvidence(slice, aggregate);
        var hasUpdate = HasUpdateEvidence(slice, aggregate);
        var once = hasCreate && !hasUpdate;
        var requiresExisting = !hasCreate;
        GetOrCreateDomain(aggregate).Commands.Add(BuildCommand(aggregate, slice.CommandId, cmd, slice.EventIds, once, requiresExisting));
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
        var isCreate = crossAggregate && HasCreateEvidence(slice, aggregate);
        target.Commands.Add(BuildCommand(aggregate, slice.CommandId, cmd, slice.ResultEventIds, isCreate, requiresExisting: !isCreate));

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

    private Domain.Command BuildCommand(string aggregate, string id, CommandDef cmd, IReadOnlyList<string> eventIds, bool once, bool requiresExisting)
    {
        var command = new Domain.Command
        {
            Name = CommandName(id),
            Once = once,
            RequiresExisting = requiresExisting,
            RequiredRole = cmd.RequiredRole,
            FieldGatedRole = BuildFieldGatedRole(id, cmd.FieldGatedRole),
            RequiredOwnership = BuildOwnership(id, cmd.RequiredOwnership),
            Scope = BuildScope(id, cmd.Scope),
        };
        command.Fields.AddRange(BuildFields($"command \"{id}\"", cmd.Fields ?? []));

        foreach (var eventId in eventIds)
        {
            if (!TryGetEvent(eventId, out var eventDef)) continue;
            _eventAggregate[eventId] = aggregate;

            var hasFields = eventDef.Fields is { Count: > 0 };
            var domainEvent = new Domain.Event { Name = EventTypeName(eventId), NoFields = !hasFields, EndsStream = eventDef.EndsStream == true };
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

    /// <param name="defaultRowKeyField">The read model's own key field, used as
    /// <c>rowKeyField</c>'s fallback for a <c>count</c>/<c>sum</c> derivation that
    /// doesn't name one explicitly. Null for command/event fields, which never carry a
    /// derivation in practice (nothing stops the schema from allowing one there too --
    /// it's a structural, not semantic, constraint -- but a generator only ever
    /// consults <see cref="Domain.Field.Derivation"/> on a read model's own fields).</summary>
    private List<Domain.Field> BuildFields(string owner, IReadOnlyList<Model.Field> fields, string? defaultRowKeyField = null,
        IReadOnlyList<Model.Field>? siblings = null)
    {
        siblings ??= fields;
        var result = new List<Domain.Field>(fields.Count);
        foreach (var field in fields)
        {
            var note = FieldTypeFolding.Note(owner, field);
            if (note is not null) _report.Warn(note);
            var derivation = BuildDerivation(owner, field, defaultRowKeyField);
            var piiSubject = ResolvePiiSubject(owner, field, siblings);
            result.Add(new Domain.Field(Names.SanitizeName(field.Name), FieldTypeFolding.Fold(field), derivation, field.Pii == true, piiSubject));
        }
        return result;
    }

    /// <summary>Resolves schema 3.0.0's <c>field.piiSubject</c> to the sanitised name of the
    /// sibling field whose value is the data subject's id. The schema enforces
    /// "<c>pii</c> ⇒ <c>piiSubject</c>" and "<c>piiSubject</c> ⇒ <c>pii</c>" structurally but
    /// leaves the reference checks to generators; a <see cref="Model.Document"/> built directly
    /// in C# can bypass even the structural ones, so all four are checked here. Every failure is
    /// an <b>error</b>, never a default: a value encrypted under the wrong subject survives that
    /// person's erasure, which is the one outcome crypto-shredding exists to prevent.</summary>
    private string? ResolvePiiSubject(string owner, Model.Field field, IReadOnlyList<Model.Field> siblings)
    {
        var isPii = field.Pii == true;
        if (field.PiiSubject is null)
        {
            if (isPii)
                _report.Error($"{owner}: field \"{field.Name}\" is pii but declares no piiSubject -- name the sibling field that holds the data subject's id");
            return null;
        }
        if (!isPii)
        {
            _report.Error($"{owner}: field \"{field.Name}\" declares piiSubject \"{field.PiiSubject}\" but is not pii");
            return null;
        }
        var subject = siblings.FirstOrDefault(s => s.Name == field.PiiSubject);
        if (subject is null)
        {
            _report.Error($"{owner}: field \"{field.Name}\" names piiSubject \"{field.PiiSubject}\", which is not a sibling field");
            return null;
        }
        if (subject.Pii == true)
        {
            _report.Error($"{owner}: field \"{field.Name}\" names piiSubject \"{field.PiiSubject}\", which is itself pii -- a subject id cannot be encrypted under its own key");
            return null;
        }
        WarnIfSubjectLooksPersonal(owner, field, subject);
        return Names.SanitizeName(subject.Name);
    }

    /// <summary>Erasure is terminal per subject id, so a person who is erased and later
    /// returns is a NEW subject with a NEW id (see this issue's findings, P6). That only
    /// works if subject ids are opaque: a model that uses the email address itself as the
    /// subject id reuses the id on a rejoin, and either hits the terminal erased state or
    /// silently re-links the new person to the erased one's history.
    ///
    /// <para>A warning, not an error, deliberately: this is a guess from a field NAME,
    /// and a name-shaped heuristic that hard-fails generation would be worse than the
    /// problem. A subject field that IS marked pii is already a hard error above -- this
    /// catches the plain (unflagged) email/phone field used as a subject.</para></summary>
    private void WarnIfSubjectLooksPersonal(string owner, Model.Field field, Model.Field subject)
    {
        string[] personalHints = ["email", "mail", "phone", "mobile", "telephone", "msisdn", "ssn", "nino", "passport"];
        var name = subject.Name.ToLowerInvariant();
        if (!personalHints.Any(h => name.Contains(h, StringComparison.Ordinal))) return;

        _report.Warn($"{owner}: field \"{field.Name}\" names piiSubject \"{subject.Name}\", which looks like personal data " +
            "rather than an opaque id. Erasure is terminal per subject id, so a subject who is erased and later returns " +
            "needs a NEW id; a natural key like an email address is reused on their return and re-links them to erased " +
            "history. Prefer a system-generated id, and mark the personal value itself pii.");
    }

    /// <summary>Resolves a field's <see cref="Model.FieldDerivation"/> (raw schema ids,
    /// an optional row key) into a <see cref="Domain.Derivation"/> (generated event type
    /// names, a row key that's always present) -- once, here, so <see cref="Generation"/>
    /// never re-consults the document.</summary>
    private Domain.Derivation? BuildDerivation(string owner, Model.Field field, string? defaultRowKeyField)
    {
        switch (field.Derivation)
        {
            case null:
                return null;
            case Model.ToggleDerivation t:
                return new Domain.ToggleDerivation(
                    t.OnEventIds.Select(EventTypeName).ToList(),
                    t.OffEventIds.Select(EventTypeName).ToList(),
                    t.Initial ?? false);
            case Model.CountDerivation c:
                var countKey = ResolveRowKeyField(owner, field.Name, c.RowKeyField, defaultRowKeyField);
                return countKey is null ? null : new Domain.CountDerivation(
                    c.IncrementOnEventIds.Select(EventTypeName).ToList(),
                    (c.DecrementOnEventIds ?? []).Select(EventTypeName).ToList(),
                    countKey);
            case Model.SumDerivation s:
                var sumKey = ResolveRowKeyField(owner, field.Name, s.RowKeyField, defaultRowKeyField);
                return sumKey is null ? null : new Domain.SumDerivation(
                    s.AddOnEventIds.Select(EventTypeName).ToList(),
                    (s.SubtractOnEventIds ?? []).Select(EventTypeName).ToList(),
                    Names.SanitizeName(s.AmountField),
                    sumKey);
            case Model.GroupByDerivation g:
                // subfields recurse through the SAME BuildFields/BuildDerivation path as
                // a read model's own top-level fields, with the SAME defaultRowKeyField --
                // a subfield's count/sum still names which TOP-LEVEL row a contributing
                // event targets (unrelated to groupByField, which only picks the entry
                // WITHIN that row's list), so reusing the read model's own key as the
                // default is exactly as correct here as it is at the top level.
                var subfields = new List<Domain.Field>();
                foreach (var subfield in field.Subfields ?? [])
                {
                    if (subfield.Derivation is Model.ToggleDerivation)
                    {
                        // ToggleDerivation carries no rowKeyField at all (schema-enforced --
                        // it's only ever meaningful same-stream), so there's no way to know
                        // which top-level row a toggle subfield's event should update once
                        // it's nested inside a groupBy field, which is foreign-stream by
                        // construction (that's the whole reason groupBy exists). Reported,
                        // not guessed at -- narrowing scope explicitly beats generating
                        // code that silently updates the wrong row.
                        _report.Error($"{owner}: field \"{field.Name}\" groupBy subfield \"{subfield.Name}\" " +
                            "declares a toggle derivation, which is not supported inside groupBy -- only count/sum subfields are");
                        continue;
                    }
                    subfields.AddRange(BuildFields($"{owner} groupBy subfield", [subfield], defaultRowKeyField, siblings: field.Subfields));
                }
                return new Domain.GroupByDerivation(Names.SanitizeName(g.GroupByField), subfields);
            default:
                throw new InvalidOperationException($"unhandled field derivation kind: {field.Derivation.GetType().Name}");
        }
    }

    private string? ResolveRowKeyField(string owner, string fieldName, string? explicitRowKeyField, string? defaultRowKeyField)
    {
        var rowKey = explicitRowKeyField ?? defaultRowKeyField;
        if (rowKey is null)
        {
            _report.Error($"{owner}: field \"{fieldName}\" declares a count/sum derivation with no `rowKeyField` " +
                "and no read-model key to default to -- name the payload field on the counted events that identifies this row");
            return null;
        }
        return Names.SanitizeName(rowKey);
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
            var onEventIds = new List<string>();
            var seedEventIds = new HashSet<string>();
            var seenEventIds = new HashSet<string>();

            // seed: true for an event whose OWN stream is this read model's row
            // (ev.AggregateId is a valid row key for it) -- builtFromEventIds, and a
            // toggle's on/off events (the schema gives toggle no rowKeyField, so it's
            // only ever meaningful same-aggregate). seed: false for a count/sum
            // derivation's events: those live on a DIFFERENT stream (that's the whole
            // reason a roll-up needs declaring), so ev.AggregateId is not this table's
            // key at all -- only the payload's own rowKeyField is.
            void AddOnEvent(string eventId, bool seed)
            {
                if (seenEventIds.Add(eventId)) onEventIds.Add(eventId);
                if (seed) seedEventIds.Add(eventId);
                if (_eventAggregate.TryGetValue(eventId, out var owner))
                    owners.Add(owner);
            }

            foreach (var eventId in rm.BuiltFromEventIds ?? [])
                AddOnEvent(eventId, seed: true);

            foreach (var field in rm.Fields ?? [])
            {
                switch (field.Derivation)
                {
                    case Model.ToggleDerivation t:
                        foreach (var eid in t.OnEventIds) AddOnEvent(eid, seed: true);
                        foreach (var eid in t.OffEventIds) AddOnEvent(eid, seed: true);
                        break;
                    case Model.CountDerivation c:
                        foreach (var eid in c.IncrementOnEventIds) AddOnEvent(eid, seed: false);
                        foreach (var eid in c.DecrementOnEventIds ?? []) AddOnEvent(eid, seed: false);
                        break;
                    case Model.SumDerivation s:
                        foreach (var eid in s.AddOnEventIds) AddOnEvent(eid, seed: false);
                        foreach (var eid in s.SubtractOnEventIds ?? []) AddOnEvent(eid, seed: false);
                        break;
                    case Model.GroupByDerivation:
                        // same seed:false reasoning as count/sum: a groupBy subfield's own
                        // contributing events live on a different stream by construction
                        // (that's the whole reason a grouped rollup needs declaring).
                        foreach (var subfield in field.Subfields ?? [])
                        {
                            switch (subfield.Derivation)
                            {
                                case Model.CountDerivation c:
                                    foreach (var eid in c.IncrementOnEventIds) AddOnEvent(eid, seed: false);
                                    foreach (var eid in c.DecrementOnEventIds ?? []) AddOnEvent(eid, seed: false);
                                    break;
                                case Model.SumDerivation s:
                                    foreach (var eid in s.AddOnEventIds) AddOnEvent(eid, seed: false);
                                    foreach (var eid in s.SubtractOnEventIds ?? []) AddOnEvent(eid, seed: false);
                                    break;
                                // Model.ToggleDerivation: rejected in BuildDerivation with a
                                // mapping error -- nothing to collect here either.
                            }
                        }
                        break;
                }
            }

            if (onEventIds.Count == 0)
            {
                _report.Warn($"read model \"{id}\" lists no builtFromEventIds, so the generated projection would never fire; skipped");
                continue;
            }
            if (seedEventIds.Count == 0)
                _report.Warn($"read model \"{id}\" has no builtFromEventIds or toggle-derived event beyond its count/sum " +
                    "derivations, so nothing seeds its row by aggregate id; a count/sum field written here has no row to " +
                    "land on unless some other event already created one");

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
            CheckReadModelPii(id, rm, seedEventIds, key);

            var readModel = new Domain.ReadModel { Collection = Names.SanitizeName(CollectionName(rm.Name, id)), Key = key, RequiredRole = rm.RequiredRole };
            readModel.Fields.AddRange(BuildFields($"read model \"{id}\"", rm.Fields ?? [], defaultRowKeyField: key));
            readModel.On.AddRange(onEventIds.Select(EventTypeName));
            readModel.SeedOn.AddRange(onEventIds.Where(seedEventIds.Contains).Select(EventTypeName));
            foreach (var scopeDef in rm.Scopes ?? [])
            {
                var viaCollection = ResolveReadModelCollection(scopeDef.Via.ReadModelId);
                if (viaCollection is null)
                {
                    _report.Error($"read model \"{id}\" scope on param \"{scopeDef.Param}\" references " +
                        $"read model \"{scopeDef.Via.ReadModelId}\", which does not exist");
                    continue;
                }
                readModel.Scopes.Add(new Domain.ReadModelScope(
                    scopeDef.Param, viaCollection,
                    Names.SanitizeName(scopeDef.Via.MatchParamTo),
                    Names.SanitizeName(scopeDef.Via.SelectField),
                    Names.SanitizeName(scopeDef.Via.FilterLocalField)));
            }
            foreach (var filterDef in rm.Filters ?? [])
            {
                // The schema's own allOf/if/then already requires `presets` whenever
                // `kind` is "dateRange" (the only kind that exists) and closes both to
                // fixed enums -- unreachable via a real, schema-validated document. This
                // guard exists for a Document built directly (a defensive unit test, or
                // any future non-JSON front-end), the same belt-and-suspenders posture
                // ResolveRowKeyField already takes for count/sum's own optional field.
                if (filterDef.Kind != "dateRange")
                {
                    _report.Error($"read model \"{id}\" filter on param \"{filterDef.Param}\" declares unsupported kind " +
                        $"\"{filterDef.Kind}\" -- only \"dateRange\" is supported");
                    continue;
                }
                if (filterDef.Presets is not { Count: > 0 })
                {
                    _report.Error($"read model \"{id}\" filter on param \"{filterDef.Param}\" (kind dateRange) declares no " +
                        "presets -- name at least one of last7Days/lastCalendarMonth/custom");
                    continue;
                }
                readModel.Filters.Add(new Domain.ReadModelFilter(
                    filterDef.Param, Names.SanitizeName(filterDef.Field), filterDef.Kind, filterDef.Presets));
            }
            // A pii column holds non-deterministic ciphertext (P4), so a SQL comparison
            // against it can never match: a scope or range filter on one is a modelling
            // error, not something to discover as an always-empty result at query time.
            var piiColumns = readModel.Fields.Where(f => f.Pii).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var scope in readModel.Scopes.Where(sc => piiColumns.Contains(sc.FilterLocalField)))
                _report.Error($"read model \"{id}\" scope on param \"{scope.Param}\" filters on pii field \"{scope.FilterLocalField}\" -- " +
                    "an encrypted column cannot be compared in SQL");
            foreach (var filter in readModel.Filters.Where(fl => piiColumns.Contains(fl.Field)))
                _report.Error($"read model \"{id}\" filter on param \"{filter.Param}\" ranges over pii field \"{filter.Field}\" -- " +
                    "an encrypted column cannot be compared in SQL");
            GetOrCreateDomain(chosenOwner).ReadModels.Add(readModel);
        }
    }

    /// <summary>A projection copies a plain column from any same-named field on a seed
    /// event's payload, and after the decider generator's PII work that payload carries a
    /// <c>{"$pii":…}</c> envelope for a pii field, not a scalar. So a read-model field's
    /// own <c>pii</c> flag must agree with every contributing event field it is copied
    /// from, in both directions -- an error, never inferred (user's call, 2026-09-22):
    /// the document has to say what the generated code does. The key column is exempt
    /// (written from <c>ev.AggregateId</c>, never the payload). A derived field is never
    /// copied, so it can't itself be pii, and the payload fields a derivation reads as
    /// plaintext (row key, amount, group key) can't be pii on the events it folds.</summary>
    private void CheckReadModelPii(string id, ReadModelDef rm, IReadOnlyCollection<string> seedEventIds, string key)
    {
        var owner = $"read model \"{id}\"";

        bool? EventFieldPii(string eventId, string fieldName)
        {
            if (!TryGetEvent(eventId, out var ev)) return null;
            var match = ev.Fields?.FirstOrDefault(f => Names.SanitizeName(f.Name) == Names.SanitizeName(fieldName));
            return match is null ? null : match.Pii == true;
        }

        void RequirePlainRef(string field, string role, string refField, IEnumerable<string> eventIds)
        {
            foreach (var eventId in eventIds.Distinct().OrderBy(e => e, StringComparer.Ordinal))
                if (EventFieldPii(eventId, refField) == true)
                    _report.Error($"{owner}: field \"{field}\"'s {role} \"{refField}\" is pii on event \"{eventId}\" -- " +
                        "a derivation reads it as plaintext, which an encrypted value is not");
        }

        void CheckDerivationRefs(string field, Model.FieldDerivation derivation)
        {
            switch (derivation)
            {
                case Model.CountDerivation c:
                    RequirePlainRef(field, "rowKeyField", c.RowKeyField ?? key, c.IncrementOnEventIds.Concat(c.DecrementOnEventIds ?? []));
                    break;
                case Model.SumDerivation s:
                    var events = s.AddOnEventIds.Concat(s.SubtractOnEventIds ?? []).ToList();
                    RequirePlainRef(field, "rowKeyField", s.RowKeyField ?? key, events);
                    RequirePlainRef(field, "amountField", s.AmountField, events);
                    break;
            }
        }

        foreach (var field in rm.Fields ?? [])
        {
            if (field.Derivation is not null)
            {
                if (field.Pii == true)
                    _report.Error($"{owner}: field \"{field.Name}\" is pii but derived ({field.Derivation.Kind}) -- " +
                        "a derived value is computed, never copied from a payload, so there is nothing to encrypt; drop pii");
                CheckDerivationRefs(field.Name, field.Derivation);
                if (field.Derivation is Model.GroupByDerivation g)
                    foreach (var sub in field.Subfields ?? [])
                    {
                        if (sub.Derivation is null) continue;
                        CheckDerivationRefs($"{field.Name}.{sub.Name}", sub.Derivation);
                        var subEvents = sub.Derivation switch
                        {
                            Model.CountDerivation c => c.IncrementOnEventIds.Concat(c.DecrementOnEventIds ?? []),
                            Model.SumDerivation s => s.AddOnEventIds.Concat(s.SubtractOnEventIds ?? []),
                            _ => [],
                        };
                        RequirePlainRef(field.Name, "groupByField", g.GroupByField, subEvents);
                    }
                continue;
            }

            if (Names.SanitizeName(field.Name) == key) continue;

            var readModelPii = field.Pii == true;
            foreach (var eventId in seedEventIds.OrderBy(e => e, StringComparer.Ordinal))
            {
                var eventPii = EventFieldPii(eventId, field.Name);
                if (eventPii is null || eventPii == readModelPii) continue;
                _report.Error(readModelPii
                    ? $"{owner}: field \"{field.Name}\" is pii, but event \"{eventId}\" carries it as plaintext -- " +
                      "mark the event field pii too, or drop pii here"
                    : $"{owner}: field \"{field.Name}\" is not pii, but event \"{eventId}\" carries it as pii -- " +
                      "mark it pii (with a piiSubject) or drop the column");
            }
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

    /// <summary>Recomputes the physical collection name a read model id would map onto
    /// -- deterministic from that id's own <c>name</c>, so this works for ANY read
    /// model id in the document regardless of whether it's been mapped yet.</summary>
    private string? ResolveReadModelCollection(string readModelId)
    {
        if (_document.ReadModels is null || !_document.ReadModels.TryGetValue(readModelId, out var rm)) return null;
        return Names.SanitizeName(CollectionName(rm.Name, readModelId));
    }

    /// <summary>Resolves a command's <c>fieldGatedRole</c> (schema 2.5.0): the payload
    /// field name is passed through unsanitized -- it is compared against the raw wire
    /// JSON's own property name at runtime, not turned into a code identifier or SQL
    /// column the way a read-model field is. <c>value</c>'s <see cref="JsonValueKind"/>
    /// is checked defensively even though the schema's own <c>oneOf</c> already restricts
    /// it to string/boolean/number -- same posture as <see cref="ResolveReadModelCollection"/>'s
    /// sibling <c>dateRange</c> filter guard, for a <see cref="Document"/> built directly
    /// in C# rather than parsed from JSON.</summary>
    private Domain.FieldGatedRolePolicy? BuildFieldGatedRole(string commandId, CommandFieldGatedRoleDef? def)
    {
        if (def is null) return null;

        Domain.FieldGatedRoleValue? value = def.Value.ValueKind switch
        {
            JsonValueKind.String => new Domain.StringFieldValue(def.Value.GetString()!),
            JsonValueKind.True or JsonValueKind.False => new Domain.BoolFieldValue(def.Value.GetBoolean()),
            JsonValueKind.Number => new Domain.NumberFieldValue(def.Value.GetDouble()),
            _ => null,
        };
        if (value is null)
        {
            _report.Error($"command \"{commandId}\"'s fieldGatedRole.value must be a string, boolean, or number, " +
                $"found {def.Value.ValueKind}");
            return null;
        }
        return new Domain.FieldGatedRolePolicy(def.Field, value, def.RequiredRole);
    }

    /// <summary>Resolves a command's <c>requiredOwnership</c> (schema 2.5.0), same
    /// via-resolution posture as <see cref="ResolveReadModelCollection"/>'s existing
    /// <c>readModel.scopes</c> mapping.</summary>
    private Domain.OwnershipPolicy? BuildOwnership(string commandId, CommandOwnershipDef? def)
    {
        if (def is null) return null;

        var viaCollection = ResolveReadModelCollection(def.Via.ReadModelId);
        if (viaCollection is null)
        {
            _report.Error($"command \"{commandId}\"'s requiredOwnership references read model " +
                $"\"{def.Via.ReadModelId}\", which does not exist");
            return null;
        }
        return new Domain.OwnershipPolicy(
            def.BypassRoles ?? [], viaCollection,
            Names.SanitizeName(def.Via.KeyField), Names.SanitizeName(def.Via.OwnerField));
    }

    /// <summary>Resolves a command's <c>scope</c> (schema 2.5.0) -- two independent
    /// via-resolutions, same posture as <see cref="BuildOwnership"/>.</summary>
    private Domain.ScopePolicy? BuildScope(string commandId, CommandScopeDef? def)
    {
        if (def is null) return null;

        var resolveCollection = ResolveReadModelCollection(def.ResolveVia.ReadModelId);
        if (resolveCollection is null)
        {
            _report.Error($"command \"{commandId}\"'s scope.resolveVia references read model " +
                $"\"{def.ResolveVia.ReadModelId}\", which does not exist");
            return null;
        }
        var memberOfCollection = ResolveReadModelCollection(def.MemberOfVia.ReadModelId);
        if (memberOfCollection is null)
        {
            _report.Error($"command \"{commandId}\"'s scope.memberOfVia references read model " +
                $"\"{def.MemberOfVia.ReadModelId}\", which does not exist");
            return null;
        }
        return new Domain.ScopePolicy(
            def.BypassRoles ?? [],
            resolveCollection, Names.SanitizeName(def.ResolveVia.KeyField), Names.SanitizeName(def.ResolveVia.SelectField),
            memberOfCollection, Names.SanitizeName(def.MemberOfVia.MatchField));
    }

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

    /// <summary>Folds one scenario's `given` sequence IN ORDER, tracking the
    /// aggregate's synthesized `Exists` exactly as the generated decider's `Evolve`
    /// will: a `given` event on THIS slice's own aggregate stream sets `Exists` to
    /// `false` when it's marked <c>endsStream</c>, `true` otherwise; a foreign-aggregate
    /// `given` event is not evidence either way and leaves `Exists` untouched. The
    /// scenario's own history nets to whichever value the LAST own-stream event left
    /// behind -- an empty `given`, or one made entirely of foreign events, nets to the
    /// initial `false`.
    ///
    /// This replaces the earlier "any own-stream event disqualifies create" scan: a
    /// re-assign-after-unassign scenario's `given` legitimately contains an own-stream
    /// event (the original assign) yet nets back to "doesn't exist," and must count as
    /// create evidence, not update evidence -- see the design proposal's Q5 (option c).</summary>
    private bool ScenarioNetExists(Scenario scenario, string aggregate)
    {
        var exists = false;
        foreach (var given in scenario.Given)
        {
            if (!_eventOwners.TryGetValue(given.EventId, out var owner) || owner != aggregate) continue;
            exists = !_endsStreamEvents.Contains(given.EventId);
        }
        return exists;
    }

    /// <summary>A scenario is evidence this command opens a fresh stream when its
    /// `given` sequence nets to `Exists == false` (see <see cref="ScenarioNetExists"/>).
    /// Where no scenario qualifies, there's no create evidence. Shared by both a
    /// directly-invoked command (<see cref="MapStateChange"/>) and an automation's
    /// dispatched command (<see cref="MapAutomation"/>) -- both slice kinds carry the
    /// same `given`-bearing scenarios.</summary>
    private bool HasCreateEvidence(Slice slice, string aggregate) =>
        slice.Scenarios.OfType<StateChangeScenario>().Any(s => !ScenarioNetExists(s, aggregate));

    /// <summary>The mirror of <see cref="HasCreateEvidence"/>: a scenario is evidence
    /// this command can run against an ALREADY-existing stream when its `given`
    /// sequence nets to `Exists == true`. A command with BOTH kinds of evidence is a
    /// genuine upsert (see <see cref="MapStateChange"/>) -- it isn't strictly
    /// create-only or strictly update-only, so neither guard applies.</summary>
    private bool HasUpdateEvidence(Slice slice, string aggregate) =>
        slice.Scenarios.OfType<StateChangeScenario>().Any(s => ScenarioNetExists(s, aggregate));

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

        // Event fields are no longer lossy: the decider generator types them Pii<T> and
        // emits a PiiProtector, so they are encrypted before append. Command fields never
        // persist (they arrive as plaintext and become event fields). What is still only
        // partially handled is the read side: a projection stores the ciphertext envelope's
        // raw JSON in the column (safe at rest; CheckReadModelPii keeps the pii flags in
        // agreement), but no generated query route reveals it yet.
        if (_document.ReadModels is not null)
            foreach (var (id, rm) in _document.ReadModels) Walk($"read model {id}", rm.Fields);

        if (flagged.Count > 0)
        {
            flagged.Sort(StringComparer.Ordinal);
            _report.Note($"{flagged.Count} read-model field(s) are marked pii: {string.Join(", ", flagged)}. " +
                "Their columns hold the ciphertext envelope's raw JSON; generated query routes return it as-is and do not reveal it yet");
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
