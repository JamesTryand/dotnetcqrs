using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Domain;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

public class DocumentMapperTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    [Fact]
    public void Maps_the_minimal_document_to_one_aggregate_with_one_create_command()
    {
        // minimal.json's "place-order" command, like order-fulfillment.json's
        // "notify-shipping-partner", carries no `aggregate` tag -- an override is
        // needed either way, exercising the same resolution path as the richer test
        // below but on the smallest possible document.
        var doc = DocumentLoader.LoadFromFile(TestDataPath("minimal.json"));
        var options = new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        };

        var result = DocumentMapper.Map(doc, options);

        var order = Assert.Single(result.Domains);
        Assert.Equal("order", order.Aggregate);
        var command = Assert.Single(order.Commands);
        Assert.Equal("PlaceOrder", command.Name);
        // minimal.json declares an empty `scenarios: []` -- IsCreate's only real
        // signal is an explicit scenario with an empty `given`, so with none present
        // this command is correctly NOT treated as the create (see the richer
        // order-fulfillment.json test below, which does supply that scenario).
        Assert.False(command.Once);
        Assert.True(command.RequiresExisting);
    }

    [Fact]
    public void An_untagged_command_with_no_override_fails_mapping_with_a_clear_reason()
    {
        // order-fulfillment.json's "notify-shipping-partner" command deliberately
        // carries no `aggregate` tag (the worked example's own point: a
        // boundary-crossing integration with nothing to own it by default).
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("notify-shipping-partner") && e.Contains("no `aggregate` tag"));
    }

    [Fact]
    public void An_aggregate_override_resolves_the_untagged_command()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var options = new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        };

        var result = DocumentMapper.Map(doc, options);

        Assert.Contains(result.Domains, d => d.Aggregate == "order");
        Assert.Contains(result.Domains, d => d.Aggregate == "shippingNotification");
        Assert.Contains(result.Report.Warnings, w => w.Contains("notify-shipping-partner") && w.Contains("override"));
    }

    private static MappingResult MapOrderFulfillment() =>
        DocumentMapper.Map(
            DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            new MappingOptions { AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" } });

    [Fact]
    public void The_order_aggregate_gets_both_its_own_commands()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        Assert.Equal(["PlaceOrder", "ShipOrder"], order.Commands.Select(c => c.Name));
    }

    [Fact]
    public void PlaceOrder_is_the_create_and_ShipOrder_requires_an_existing_stream()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var placeOrder = order.Commands.Single(c => c.Name == "PlaceOrder");
        Assert.True(placeOrder.Once);
        Assert.False(placeOrder.RequiresExisting);

        var shipOrder = order.Commands.Single(c => c.Name == "ShipOrder");
        Assert.False(shipOrder.Once);
        Assert.True(shipOrder.RequiresExisting);
    }

    [Fact]
    public void The_same_aggregate_automation_reactor_lands_on_the_order_domain()
    {
        // auto-ship-pending-orders: triggered by order-placed (owned by "order"),
        // dispatches ship-order (also "order") -- source == target, same-aggregate.
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var reactor = Assert.Single(order.Reactors, r => r.Command == "ShipOrder");
        Assert.Equal(["OrderPlaced"], reactor.On);
        Assert.Equal("order", reactor.Aggregate);
        // No IdPrefix: the target is the triggering order's own existing stream, not a
        // new one -- a prefix here would dispatch to an id nothing ever created.
        Assert.Null(reactor.IdPrefix);
    }

    [Fact]
    public void The_cross_aggregate_reactor_lands_on_the_source_while_its_command_lands_on_the_target()
    {
        // notify-shipping-partner-automation: triggered by order-shipped (owned by
        // "order"), dispatches notify-shipping-partner (overridden to
        // "shippingNotification") -- cross-aggregate, so that command is a "create".
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");
        var shipping = result.Domains.Single(d => d.Aggregate == "shippingNotification");

        var reactor = Assert.Single(order.Reactors, r => r.Command == "NotifyShippingPartner");
        Assert.Equal(["OrderShipped"], reactor.On);
        Assert.Equal("shippingNotification", reactor.Aggregate);
        Assert.Equal("notify-shipping-partner-", reactor.IdPrefix);

        var command = Assert.Single(shipping.Commands);
        Assert.Equal("NotifyShippingPartner", command.Name);
        Assert.True(command.Once); // cross-aggregate: a fresh stream per trigger
    }

    [Fact]
    public void A_cross_aggregate_automation_with_evidence_of_both_a_new_and_an_existing_target_is_an_upsert()
    {
        // The same rule as a stateChange command: one scenario on a fresh target stream and one on an
        // existing one means the command runs on either, so neither guard applies. For
        // example, a ledger records every entry's pay, its first record and every later one alike.
        const string json = """
            {
              "eventModelingSchemaVersion": "3.14.0", "id": "upsert-automation", "name": "Upsert Automation",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "entry-logged": {"name": "Entry Logged", "swimlaneId": "s", "aggregate": "entry", "fields": [{"name": "entryId", "type": "string"}]},
                "pay-recorded": {"name": "Pay Recorded", "swimlaneId": "s", "aggregate": "ledger", "fields": [{"name": "entryId", "type": "string"}]}
              },
              "commands": {
                "log-entry": {"name": "Log Entry", "aggregate": "entry", "fields": [{"name": "entryId", "type": "string"}]},
                "record-pay": {"name": "Record Pay", "aggregate": "ledger", "fields": [{"name": "entryId", "type": "string"}]}
              },
              "screens": {"scr": {"name": "Screen"}},
              "automations": {"record-pay-automation": {"name": "Record Pay"}},
              "slices": [
                {"id": "log-entry-slice", "name": "Log Entry", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
                 "screenId": "scr", "commandId": "log-entry", "eventIds": ["entry-logged"], "scenarios": []},
                {
                  "id": "record-pay-slice", "name": "Record Pay", "pattern": "automation", "swimlaneId": "s", "status": "created",
                  "automationId": "record-pay-automation", "triggerEventIds": ["entry-logged"], "commandId": "record-pay", "resultEventIds": ["pay-recorded"],
                  "scenarios": [
                    {"id": "first", "name": "The first record", "kind": "stateChange",
                     "given": [{"eventId": "entry-logged"}], "when": {"commandId": "record-pay"}, "then": {"events": [{"eventId": "pay-recorded"}]}},
                    {"id": "later", "name": "A later record", "kind": "stateChange",
                     "given": [{"eventId": "pay-recorded"}, {"eventId": "entry-logged"}], "when": {"commandId": "record-pay"}, "then": {"events": [{"eventId": "pay-recorded"}]}}
                  ]
                }
              ]
            }
            """;

        var result = DocumentMapper.Map(DocumentLoader.Parse(json));

        var command = result.Domains.Single(d => d.Aggregate == "ledger").Commands.Single(c => c.Name == "RecordPay");
        Assert.False(command.Once, "a later record must not be refused as 'already exists'");
        Assert.False(command.RequiresExisting, "the first record must not be refused as 'does not exist'");
    }

    [Fact]
    public void Read_models_are_attached_to_their_owning_aggregate_with_a_derived_key()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var summary = order.ReadModels.Single(rm => rm.Collection == "orderSummary");
        Assert.Equal("orderId", summary.Key); // orderId is marked idAttribute in the document
        Assert.Equal(["OrderPlaced", "OrderShipped"], summary.On);

        var pending = order.ReadModels.Single(rm => rm.Collection == "pendingShipments");
        Assert.Equal(["OrderPlaced"], pending.On);
    }

    [Fact]
    public void A_list_field_folds_to_json_and_is_reported_in_warnings()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");

        var orderPlaced = order.Commands.Single(c => c.Name == "PlaceOrder").Events.Single(e => e.Name == "OrderPlaced");
        var items = orderPlaced.Fields.Single(f => f.Name == "items");
        Assert.Equal("json", items.Type);
        Assert.Contains(result.Report.Warnings, w => w.Contains("items") && w.Contains("json column"));
    }

    [Fact]
    public void An_event_PII_field_is_no_longer_reported_as_lossy_now_that_the_generator_encrypts_it()
    {
        // Before the field.pii integration this asserted the opposite: customerEmail was
        // named as "stored as an ordinary column". The decider generator now types it
        // Pii<T> and emits a PiiProtector, so silently keeping the old note would be the
        // exact kind of stale claim the note exists to prevent.
        var result = MapOrderFulfillment();
        Assert.DoesNotContain(result.Report.Lossy, l => l.Contains("customerEmail"));
    }

    [Fact]
    public void A_field_pii_marked_field_carries_Pii_true_through_to_the_domain_and_unmarked_fields_do_not()
    {
        var result = MapOrderFulfillment();
        var order = result.Domains.Single(d => d.Aggregate == "order");
        var orderPlaced = order.Commands.Single(c => c.Name == "PlaceOrder").Events.Single(e => e.Name == "OrderPlaced");

        var customerEmail = orderPlaced.Fields.Single(f => f.Name == "customerEmail");
        Assert.True(customerEmail.Pii);
        Assert.Equal("customerId", customerEmail.PiiSubject);

        var orderId = orderPlaced.Fields.Single(f => f.Name == "orderId");
        Assert.False(orderId.Pii);
        Assert.Null(orderId.PiiSubject);
    }

    // Schema 3.0.0's `piiSubject`: the schema enforces pii <=> piiSubject structurally but
    // leaves the reference checks (does it name a sibling? is that sibling itself pii?) to
    // generators, and a Document mutated in C# can bypass even the structural ones -- same
    // reasoning as the dateRange/presets test below. Every case is an error, never a default.
    private static DotnetCqrs.Codegen.Model.Document WithOrderPlacedCustomerEmail(Func<DotnetCqrs.Codegen.Model.Field, DotnetCqrs.Codegen.Model.Field> mutate)
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var ev = doc.Events!["order-placed"];
        var fields = ev.Fields!.Select(f => f.Name == "customerEmail" ? mutate(f) : f).ToList();
        var events = new Dictionary<string, DotnetCqrs.Codegen.Model.EventDef>(doc.Events!) { ["order-placed"] = ev with { Fields = fields } };
        return doc with { Events = events };
    }

    [Theory]
    [InlineData(null, "declares no piiSubject")]
    [InlineData("noSuchField", "not a sibling field")]
    [InlineData("customerEmail", "is itself pii")]
    public void A_pii_field_whose_subject_cannot_be_resolved_is_rejected_not_defaulted(string? subject, string reason)
    {
        var doc = WithOrderPlacedCustomerEmail(f => f with { PiiSubject = subject });

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(ex.Report.Errors, e => e.Contains("customerEmail") && e.Contains(reason));
    }

    [Fact]
    public void A_piiSubject_on_a_field_that_is_not_pii_is_rejected()
    {
        var doc = WithOrderPlacedCustomerEmail(f => f with { Pii = null });

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));

        Assert.Contains(ex.Report.Errors, e => e.Contains("customerEmail") && e.Contains("is not pii"));
    }

    // A projection copies a plain column from any same-named field on its
    // seed events, so a read-model field's pii flag must agree with the event field it
    // copies -- a mapping error in both directions, never inferred.
    private static readonly MappingOptions OrderFulfillmentOptions =
        new() { AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" } };

    private static DotnetCqrs.Codegen.Model.Field RmField(string name, string type = "string", bool pii = false,
        string? piiSubject = null, DotnetCqrs.Codegen.Model.FieldDerivation? derivation = null) =>
        new(name, type, null, null, null, null, pii ? true : null, piiSubject, null, derivation);

    private static DotnetCqrs.Codegen.Model.Document WithOrderSummaryFields(
        DotnetCqrs.Codegen.Model.Document doc, params DotnetCqrs.Codegen.Model.Field[] extra)
    {
        var rm = doc.ReadModels!["order-summary"];
        var readModels = new Dictionary<string, DotnetCqrs.Codegen.Model.ReadModelDef>(doc.ReadModels!)
        {
            ["order-summary"] = rm with { Fields = [.. rm.Fields!, .. extra] },
        };
        return doc with { ReadModels = readModels };
    }

    [Fact]
    public void A_read_model_column_copied_from_a_pii_event_field_must_itself_be_pii()
    {
        var doc = WithOrderSummaryFields(DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            RmField("customerEmail"));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("order-summary") && e.Contains("customerEmail")
            && e.Contains("is not pii, but event \"order-placed\" carries it as pii"));
    }

    [Fact]
    public void A_pii_read_model_column_copied_from_a_plaintext_event_field_is_rejected()
    {
        var doc = WithOrderPlacedCustomerEmail(f => f with { Pii = null, PiiSubject = null });
        doc = WithOrderSummaryFields(doc, RmField("customerId", "uuid"), RmField("customerEmail", pii: true, piiSubject: "customerId"));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("customerEmail")
            && e.Contains("is pii, but event \"order-placed\" carries it as plaintext"));
    }

    [Fact]
    public void A_read_model_column_whose_pii_flag_matches_its_event_field_maps_as_pii()
    {
        var doc = WithOrderSummaryFields(DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            RmField("customerId", "uuid"), RmField("customerEmail", pii: true, piiSubject: "customerId"));

        var result = DocumentMapper.Map(doc, OrderFulfillmentOptions);

        var summary = result.Domains.Single(d => d.Aggregate == "order").ReadModels.Single(rm => rm.Collection == "orderSummary");
        Assert.True(summary.Fields.Single(f => f.Name == "customerEmail").Pii);
        Assert.False(summary.Fields.Single(f => f.Name == "customerId").Pii);
    }

    // Ciphertext is non-deterministic, so SQL can never match a pii column.
    private static DotnetCqrs.Codegen.Model.Document WithPiiOrderSummary(
        Func<DotnetCqrs.Codegen.Model.ReadModelDef, DotnetCqrs.Codegen.Model.ReadModelDef> change)
    {
        var doc = WithOrderSummaryFields(DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            RmField("customerId", "uuid"), RmField("customerEmail", pii: true, piiSubject: "customerId"));
        var readModels = new Dictionary<string, DotnetCqrs.Codegen.Model.ReadModelDef>(doc.ReadModels!)
        {
            ["order-summary"] = change(doc.ReadModels!["order-summary"]),
        };
        return doc with { ReadModels = readModels };
    }

    [Fact]
    public void A_scope_that_filters_on_a_pii_column_is_rejected()
    {
        var doc = WithPiiOrderSummary(rm => rm with
        {
            Scopes = [new DotnetCqrs.Codegen.Model.ReadModelScopeDef("who",
                new DotnetCqrs.Codegen.Model.ReadModelScopeVia("order-summary", "orderId", "customerEmail", "customerEmail"))],
        });

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("scope on param \"who\"") && e.Contains("pii field \"customerEmail\""));
    }

    [Fact]
    public void A_dateRange_filter_over_a_pii_column_is_rejected()
    {
        var doc = WithPiiOrderSummary(rm => rm with
        {
            Filters = [new DotnetCqrs.Codegen.Model.ReadModelFilterDef("when", "customerEmail", "dateRange", ["last7Days"])],
        });

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("filter on param \"when\"") && e.Contains("pii field \"customerEmail\""));
    }

    [Fact]
    public void A_derivation_that_reads_a_pii_event_field_as_plaintext_is_rejected()
    {
        // sum's amountField is read with GetDouble() -- impossible on a ciphertext envelope.
        var doc = WithOrderSummaryFields(DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            RmField("emailTotal", "double", derivation: new DotnetCqrs.Codegen.Model.SumDerivation("sum", ["order-placed"], null, "customerEmail", "orderId")));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("emailTotal") && e.Contains("amountField \"customerEmail\" is pii on event \"order-placed\""));
    }

    [Fact]
    public void A_derived_read_model_field_cannot_itself_be_pii()
    {
        var doc = WithOrderSummaryFields(DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json")),
            RmField("customerId", "uuid"),
            RmField("orderCount", "integer", pii: true, piiSubject: "customerId",
                derivation: new DotnetCqrs.Codegen.Model.CountDerivation("count", ["order-placed"], null, "orderId")));

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc, OrderFulfillmentOptions));

        Assert.Contains(ex.Report.Errors, e => e.Contains("orderCount") && e.Contains("is pii but derived (count)"));
    }

    [Fact]
    public void A_piiSubject_that_looks_like_personal_data_is_warned_about_not_rejected()
    {
        // Erasure is terminal per subject id, so a returning person needs a NEW id.
        // A natural key like an email is reused on their return, which re-links them to
        // erased history. A warning, not an error: the signal is only the field's name.
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var ev = doc.Events!["order-placed"];
        var fields = ev.Fields!
            .Select(f => f.Name == "customerEmail" ? f with { PiiSubject = "contactEmail" } : f)
            .Append(RmField("contactEmail"))
            .ToList();
        var events = new Dictionary<string, DotnetCqrs.Codegen.Model.EventDef>(doc.Events!) { ["order-placed"] = ev with { Fields = fields } };

        var result = DocumentMapper.Map(doc with { Events = events }, OrderFulfillmentOptions);

        Assert.Contains(result.Report.Warnings, w => w.Contains("contactEmail") && w.Contains("opaque id"));
        var orderPlaced = result.Domains.Single(d => d.Aggregate == "order")
            .Commands.Single(c => c.Name == "PlaceOrder").Events.Single(e => e.Name == "OrderPlaced");
        Assert.Equal("contactEmail", orderPlaced.Fields.Single(f => f.Name == "customerEmail").PiiSubject);
    }

    [Fact]
    public void An_opaque_piiSubject_is_not_warned_about()
    {
        var result = MapOrderFulfillment(); // customerEmail's subject is customerId
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("opaque id"));
    }

    [Fact]
    public void Chapters_and_slice_status_are_named_as_lossy()
    {
        var result = MapOrderFulfillment();
        Assert.Contains(result.Report.Lossy, l => l.Contains("chapter"));
        Assert.Contains(result.Report.Lossy, l => l.Contains("slice status"));
    }

    [Fact]
    public void A_reassign_scenario_after_endsStream_nets_to_create_evidence_not_update()
    {
        // A `given` that seeds the own stream (an
        // earlier assign) but then ENDS on an endsStream event (the unassign) nets back
        // to "doesn't exist" -- create evidence, not update. The earlier scanning
        // code would have called this update evidence (an own-stream event IS present)
        // and wrongly flipped the command to an upsert, dropping the guard against
        // double-assigning a currently-active pair.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "reassign-test", "name": "Reassign Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "courier-assigned": {"name": "Courier Assigned", "swimlaneId": "s", "aggregate": "RegionCourierAssignment"},
                "courier-unassigned": {"name": "Courier Unassigned", "swimlaneId": "s", "aggregate": "RegionCourierAssignment", "endsStream": true}
              },
              "commands": {
                "assign-courier": {"name": "Assign Courier", "aggregate": "RegionCourierAssignment"},
                "unassign-courier": {"name": "Unassign Courier", "aggregate": "RegionCourierAssignment"}
              },
              "screens": {"scr": {"name": "Screen"}},
              "slices": [
                {
                  "id": "assign-courier-slice", "name": "Assign Courier", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "assign-courier", "eventIds": ["courier-assigned"],
                  "scenarios": [
                    {
                      "id": "create-scenario", "name": "First assignment", "kind": "stateChange",
                      "given": [], "when": {"commandId": "assign-courier"}, "then": {"events": [{"eventId": "courier-assigned"}]}
                    },
                    {
                      "id": "reassign-scenario", "name": "Reassign after unassign", "kind": "stateChange",
                      "given": [{"eventId": "courier-assigned"}, {"eventId": "courier-unassigned"}],
                      "when": {"commandId": "assign-courier"}, "then": {"events": [{"eventId": "courier-assigned"}]}
                    }
                  ]
                },
                {
                  "id": "unassign-courier-slice", "name": "Unassign Courier", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "unassign-courier", "eventIds": ["courier-unassigned"],
                  "scenarios": [{
                    "id": "unassign-scenario", "name": "Unassign an existing pair", "kind": "stateChange",
                    "given": [{"eventId": "courier-assigned"}], "when": {"commandId": "unassign-courier"},
                    "then": {"events": [{"eventId": "courier-unassigned"}]}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var result = DocumentMapper.Map(doc);

        var command = Assert.Single(result.Domains).Commands.Single(c => c.Name == "AssignCourier");
        Assert.True(command.Once, "still a create: a redundant assign of a currently-active pair must be refused");
        Assert.False(command.RequiresExisting);
        Assert.True(command.CanBeginAgain, "the reassign scenario creates again after the stream's end");
    }

    [Fact]
    public void A_create_with_no_scenario_after_the_end_cannot_begin_again()
    {
        // A deleted entry ends its stream (endsStream), but no
        // scenario logs it again afterwards. Unlike an unassign, the end is final, so the
        // create must not be able to begin the stream again.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "final-end-test", "name": "Final End Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "entry-logged": {"name": "Entry Logged", "swimlaneId": "s", "aggregate": "Entry"},
                "entry-deleted": {"name": "Entry Deleted", "swimlaneId": "s", "aggregate": "Entry", "endsStream": true}
              },
              "commands": {
                "log-entry": {"name": "Log Entry", "aggregate": "Entry"},
                "delete-entry": {"name": "Delete Entry", "aggregate": "Entry"}
              },
              "screens": {"scr": {"name": "Screen"}},
              "slices": [
                {
                  "id": "log-slice", "name": "Log", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "log-entry", "eventIds": ["entry-logged"],
                  "scenarios": [
                    {
                      "id": "log-scenario", "name": "Log an entry", "kind": "stateChange",
                      "given": [], "when": {"commandId": "log-entry"}, "then": {"events": [{"eventId": "entry-logged"}]}
                    },
                    {
                      "id": "relog-refused", "name": "A deleted entry cannot be logged again", "kind": "error",
                      "given": [{"eventId": "entry-logged"}, {"eventId": "entry-deleted"}],
                      "when": {"commandId": "log-entry"}, "then": {"error": {"message": "Entry has ended and cannot begin again"}}
                    }
                  ]
                },
                {
                  "id": "delete-slice", "name": "Delete", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "delete-entry", "eventIds": ["entry-deleted"],
                  "scenarios": [{
                    "id": "delete-scenario", "name": "Delete an entry", "kind": "stateChange",
                    "given": [{"eventId": "entry-logged"}], "when": {"commandId": "delete-entry"},
                    "then": {"events": [{"eventId": "entry-deleted"}]}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var result = DocumentMapper.Map(doc);

        var command = Assert.Single(result.Domains).Commands.Single(c => c.Name == "LogEntry");
        Assert.True(command.Once);
        Assert.False(command.CanBeginAgain, "an error scenario after the end is not evidence of beginning again");
    }

    [Fact]
    public void A_foreign_aggregate_endsStream_event_in_given_is_not_evidence_either_way()
    {
        // A `given` event on a DIFFERENT aggregate's stream is never evidence for this
        // command, endsStream or not -- ScenarioNetExists must check ownership before
        // ever consulting the endsStream set, not the reverse.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "foreign-endsstream-test", "name": "Foreign EndsStream Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "warehouse-opened": {"name": "Warehouse Opened", "swimlaneId": "s", "aggregate": "Warehouse"},
                "warehouse-closed": {"name": "Warehouse Closed", "swimlaneId": "s", "aggregate": "Warehouse", "endsStream": true},
                "shipment-staged": {"name": "Shipment Staged", "swimlaneId": "s", "aggregate": "Shipment"}
              },
              "commands": {
                "close-warehouse": {"name": "Close Warehouse", "aggregate": "Warehouse"},
                "stage-shipment": {"name": "Stage Shipment", "aggregate": "Shipment"}
              },
              "screens": {"scr": {"name": "Screen"}},
              "slices": [
                {
                  "id": "close-warehouse-slice", "name": "Close Warehouse", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "close-warehouse", "eventIds": ["warehouse-closed"],
                  "scenarios": [{
                    "id": "close-scenario", "name": "Close an existing warehouse", "kind": "stateChange",
                    "given": [{"eventId": "warehouse-opened"}], "when": {"commandId": "close-warehouse"},
                    "then": {"events": [{"eventId": "warehouse-closed"}]}
                  }]
                },
                {
                  "id": "stage-shipment-slice", "name": "Stage Shipment", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "stage-shipment", "eventIds": ["shipment-staged"],
                  "scenarios": [{
                    "id": "stage-after-warehouse-closed", "name": "Stage after the warehouse closed", "kind": "stateChange",
                    "given": [{"eventId": "warehouse-closed"}],
                    "when": {"commandId": "stage-shipment"}, "then": {"events": [{"eventId": "shipment-staged"}]}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var result = DocumentMapper.Map(doc);

        var command = result.Domains.Single(d => d.Aggregate == "shipment").Commands.Single();
        Assert.True(command.Once, "a foreign-aggregate given (endsStream or not) is an ordinary cross-aggregate precondition, not update evidence");
        Assert.False(command.RequiresExisting);
    }

    [Fact]
    public void A_dangling_reference_is_caught_before_mapping_ever_runs()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "x", "name": "X",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "slices": [{
                "id":"sl","name":"SL","pattern":"stateChange","swimlaneId":"s","status":"created",
                "screenId":"scr","commandId":"nope-does-not-exist","eventIds":["also-missing"],
                "scenarios":[]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e => e.Contains("nope-does-not-exist"));
        Assert.Contains(ex.Report.Errors, e => e.Contains("also-missing"));
    }

    // A minimal reporting-periods.sellerTotals shape (schema 2.3.0): "sale-recorded" events
    // live on a different aggregate ("Sale") than the read model they roll up into
    // ("ReportingPeriod"), same cross-stream shape as the count-derivation precedent
    // (ScenarioVerifierTests's "A_count_derivation_rolls_up_across_streams") -- groupBy
    // additionally nests one row per distinct sellerId inside each period's own row.
    private const string GroupByDocumentJson = """
        {
          "eventModelingSchemaVersion": "2.3.0", "id": "groupby-test", "name": "GroupBy Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "period-created": {"name": "Period Created", "swimlaneId": "s", "aggregate": "ReportingPeriod",
              "fields": [{"name": "periodId", "type": "string", "idAttribute": true}]},
            "sale-recorded": {"name": "Sale Recorded", "swimlaneId": "s", "aggregate": "Sale",
              "fields": [
                {"name": "periodId", "type": "string"},
                {"name": "sellerId", "type": "string"},
                {"name": "units", "type": "double"}
              ]}
          },
          "commands": {
            "create-period": {"name": "Create Period", "aggregate": "ReportingPeriod"},
            "record-sale": {"name": "Record Sale", "aggregate": "Sale"}
          },
          "readModels": {
            "reporting-periods": {
              "name": "Reporting Periods",
              "builtFromEventIds": ["period-created"],
              "fields": [
                {"name": "periodId", "type": "string", "idAttribute": true},
                {"name": "sellerTotals", "type": "custom", "cardinality": "list",
                  "derivation": {"kind": "groupBy", "groupByField": "sellerId"},
                  "subfields": [
                    {"name": "sellerId", "type": "string"},
                    {"name": "weekendUnits", "type": "double",
                      "derivation": {"kind": "sum", "addOnEventIds": ["sale-recorded"], "amountField": "units"}}
                  ]}
              ]
            }
          },
          "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "Log Screen"}, "scr3": {"name": "View Screen"}},
          "slices": [
            {
              "id": "create-period-slice", "name": "Create Period", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "create-period", "eventIds": ["period-created"],
              "scenarios": [{"id":"create-scenario","name":"Create","kind":"stateChange","given":[],"when":{"commandId":"create-period"},"then":{"events":[{"eventId":"period-created"}]}}]
            },
            {
              "id": "record-sale-slice", "name": "Record Sale", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr2", "commandId": "record-sale", "eventIds": ["sale-recorded"],
              "scenarios": [{"id":"log-scenario","name":"Log","kind":"stateChange","given":[],"when":{"commandId":"record-sale"},"then":{"events":[{"eventId":"sale-recorded"}]}}]
            },
            {
              "id": "view-periods-slice", "name": "View Periods", "pattern": "stateView",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr3", "readModelId": "reporting-periods",
              "scenarios": [{
                "id": "view-after-log", "name": "Seller totals reflect recorded sales", "kind": "stateView",
                "given": [
                  {"eventId": "period-created", "data": {"periodId": "p1"}},
                  {"eventId": "sale-recorded", "data": {"periodId": "p1", "sellerId": "s1", "units": 5}}
                ],
                "when": {"readModelId": "reporting-periods"},
                "then": {"result": {"sellerTotals": [{"sellerId": "s1", "weekendUnits": 5}]}}
              }]
            }
          ]
        }
        """;

    [Fact]
    public void A_groupBy_derivation_maps_to_a_nested_field_with_its_own_subfield_derivations()
    {
        var doc = DocumentLoader.Parse(GroupByDocumentJson);

        var result = DocumentMapper.Map(doc);

        var readModel = result.Domains.Single(d => d.Aggregate == "reportingPeriod").ReadModels.Single();
        var sellerTotals = readModel.Fields.Single(f => f.Name == "sellerTotals");
        var groupBy = Assert.IsType<GroupByDerivation>(sellerTotals.Derivation);
        Assert.Equal("sellerId", groupBy.GroupByField);

        var sellerIdSubfield = groupBy.Subfields.Single(f => f.Name == "sellerId");
        Assert.Null(sellerIdSubfield.Derivation);

        var unitsSubfield = groupBy.Subfields.Single(f => f.Name == "weekendUnits");
        var sum = Assert.IsType<SumDerivation>(unitsSubfield.Derivation);
        Assert.Equal(["SaleRecorded"], sum.AddOnEvents);
        // defaulted from the read model's own key, exactly like a top-level sum/count's
        // own rowKeyField default -- the contributing event's payload names it "periodId".
        Assert.Equal("periodId", sum.RowKeyField);

        // sale-recorded lives on a DIFFERENT stream (Sale, not ReportingPeriod) --
        // reacted to (On) but not seed-eligible (SeedOn), same rule as count/sum.
        Assert.Contains("SaleRecorded", readModel.On);
        Assert.DoesNotContain("SaleRecorded", readModel.SeedOn);
    }

    [Fact]
    public void A_toggle_subfield_inside_a_groupBy_is_rejected_with_a_clear_reason()
    {
        // ToggleDerivation carries no rowKeyField at all (schema-enforced -- it's only
        // ever meaningful same-stream), so there is no way to know which top-level row a
        // toggle subfield's event should update once nested inside a groupBy field, which
        // is foreign-stream by construction. See DocumentMapper.BuildDerivation's own
        // doc comment on the groupBy case.
        var json = GroupByDocumentJson.Replace(
            """{"name": "sellerId", "type": "string"},""",
            """
            {"name": "sellerId", "type": "string"},
            {"name": "everSoldAtWeekend", "type": "boolean",
              "derivation": {"kind": "toggle", "onEventIds": ["sale-recorded"], "offEventIds": ["sale-recorded"]}},
            """);
        var doc = DocumentLoader.Parse(json);

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(doc));
        Assert.Contains(ex.Report.Errors, e =>
            e.Contains("everSoldAtWeekend") && e.Contains("toggle") && e.Contains("not supported"));
    }

    // A shipments read model with several export
    // slices: a shipDate-scoped
    // dateRange filter (schema 2.4.0), sibling to readModel.scopes.
    private const string DateRangeFilterDocumentJson = """
        {
          "eventModelingSchemaVersion": "2.4.0", "id": "daterange-test", "name": "DateRange Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "shipment-logged": {"name": "Shipment Logged", "swimlaneId": "s", "aggregate": "Shipment",
              "fields": [
                {"name": "shipmentId", "type": "string", "idAttribute": true},
                {"name": "shipDate", "type": "date"},
                {"name": "weightKg", "type": "double"}
              ]}
          },
          "commands": {
            "log-shipment": {"name": "Log Shipment", "aggregate": "Shipment"}
          },
          "readModels": {
            "shipments": {
              "name": "Shipments",
              "builtFromEventIds": ["shipment-logged"],
              "fields": [
                {"name": "shipmentId", "type": "string", "idAttribute": true},
                {"name": "shipDate", "type": "date"},
                {"name": "weightKg", "type": "double"}
              ],
              "filters": [
                {"param": "dateRange", "field": "shipDate", "kind": "dateRange", "presets": ["last7Days", "lastCalendarMonth", "custom"]}
              ]
            }
          },
          "screens": {"scr1": {"name": "Log Screen"}, "scr2": {"name": "View Screen"}},
          "slices": [
            {
              "id": "log-shipment-slice", "name": "Log Shipment", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "log-shipment", "eventIds": ["shipment-logged"],
              "scenarios": [{"id":"log-scenario","name":"Log","kind":"stateChange","given":[],"when":{"commandId":"log-shipment"},"then":{"events":[{"eventId":"shipment-logged"}]}}]
            },
            {
              "id": "view-shipments-slice", "name": "View Shipments", "pattern": "stateView",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr2", "readModelId": "shipments",
              "scenarios": []
            }
          ]
        }
        """;

    [Fact]
    public void A_dateRange_filter_maps_to_a_ReadModelFilter_alongside_the_field_it_names()
    {
        var doc = DocumentLoader.Parse(DateRangeFilterDocumentJson);

        var result = DocumentMapper.Map(doc);

        var readModel = result.Domains.Single(d => d.Aggregate == "shipment").ReadModels.Single();
        var filter = Assert.Single(readModel.Filters);
        Assert.Equal("dateRange", filter.Param);
        Assert.Equal("shipDate", filter.Field);
        Assert.Equal("dateRange", filter.Kind);
        Assert.Equal(["last7Days", "lastCalendarMonth", "custom"], filter.Presets);
    }

    [Fact]
    public void A_dateRange_filter_missing_presets_is_rejected_with_a_clear_reason()
    {
        // The schema's own allOf/if/then already requires `presets` whenever `kind` is
        // "dateRange" and closes both to fixed enums, so no real (schema-validated)
        // document can reach DocumentMapper with this shape -- exactly why this test
        // mutates an already-parsed, already-valid Document directly in C# instead of
        // going through DocumentLoader a second time (which would just reject the JSON
        // itself, proving JsonSchema.Net works rather than DocumentMapper's own guard).
        // Mirrors the toggle-subfield rejection test above: a defensive mapper-level
        // check for a shape the schema also happens to forbid.
        var doc = DocumentLoader.Parse(DateRangeFilterDocumentJson);
        var readModel = doc.ReadModels!["shipments"];
        var malformedFilter = readModel.Filters!.Single() with { Presets = null };
        var mutatedReadModels = new Dictionary<string, DotnetCqrs.Codegen.Model.ReadModelDef>(doc.ReadModels!)
        {
            ["shipments"] = readModel with { Filters = [malformedFilter] },
        };
        var mutatedDoc = doc with { ReadModels = mutatedReadModels };

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(mutatedDoc));
        Assert.Contains(ex.Report.Errors, e =>
            e.Contains("dateRange") && e.Contains("presets"));
    }

    // Command authorization (schema 2.5.0):
    // requiredRole (both single-value and array form), fieldGatedRole,
    // requiredOwnership, and scope on a real-shaped command set (an ownership-only
    // update, a supervisor-bypass-else-region-scoped flag, a supervisor-only setup command, and
    // an Administrator-field-gated onboarding command).
    private const string CommandAuthorizationDocumentJson = """
        {
          "eventModelingSchemaVersion": "2.5.0", "id": "command-auth-test", "name": "Command Authorization Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order"},
            "order-updated": {"name": "Order Updated", "swimlaneId": "s", "aggregate": "Order"},
            "order-flagged": {"name": "Order Flagged", "swimlaneId": "s", "aggregate": "Order"},
            "member-onboarded": {"name": "Member Onboarded", "swimlaneId": "s", "aggregate": "Member"}
          },
          "commands": {
            "place-order": {"name": "Place Order", "aggregate": "Order", "requiredRole": ["supervisor", "administrator"]},
            "update-order": {"name": "Update Order", "aggregate": "Order",
              "requiredOwnership": {"via": {"readModelId": "orders", "keyField": "orderId", "ownerField": "ownerId"}}},
            "flag-order": {"name": "Flag Order", "aggregate": "Order",
              "scope": {
                "bypassRoles": ["supervisor"],
                "resolveVia": {"readModelId": "orders", "keyField": "orderId", "selectField": "regionId"},
                "memberOfVia": {"readModelId": "region-supervisors", "matchField": "regionId", "actorField": "memberId"}
              }},
            "onboard-member": {"name": "Onboard Member", "aggregate": "Member",
              "fieldGatedRole": {"field": "role", "value": "supervisor", "requiredRole": "administrator"}}
          },
          "readModels": {
            "orders": {"name": "Orders", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "orderId", "type": "string", "idAttribute": true}, {"name": "ownerId", "type": "string"}, {"name": "regionId", "type": "string"}],
              "requiredRole": "supervisor"},
            "region-supervisors": {"name": "Region Supervisors", "builtFromEventIds": ["order-placed"],
              "fields": [{"name": "memberId", "type": "string", "idAttribute": true}, {"name": "regionId", "type": "string"}],
              "requiredRole": ["supervisor", "administrator"]}
          },
          "screens": {"scr": {"name": "Screen"}},
          "slices": [
            {"id": "place-order-slice", "name": "Place Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"], "scenarios": []},
            {"id": "update-order-slice", "name": "Update Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "update-order", "eventIds": ["order-updated"], "scenarios": []},
            {"id": "flag-order-slice", "name": "Flag Order", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "flag-order", "eventIds": ["order-flagged"], "scenarios": []},
            {"id": "onboard-member-slice", "name": "Onboard Member", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
              "screenId": "scr", "commandId": "onboard-member", "eventIds": ["member-onboarded"], "scenarios": []}
          ]
        }
        """;

    [Fact]
    public void A_scope_that_does_not_name_the_members_field_is_refused()
    {
        var withoutActorField = CommandAuthorizationDocumentJson.Replace(", \"actorField\": \"memberId\"", "");
        Assert.NotEqual(CommandAuthorizationDocumentJson, withoutActorField);

        var ex = Assert.ThrowsAny<Exception>(() => DocumentLoader.Parse(withoutActorField));

        Assert.Contains("actorField", ex.Message);
    }

    [Fact]
    public void RequiredRole_maps_as_a_list_whether_declared_as_one_value_or_an_array()
    {
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);

        var result = DocumentMapper.Map(doc);

        var order = result.Domains.Single(d => d.Aggregate == "order");
        var placeOrder = order.Commands.Single(c => c.Name == "PlaceOrder");
        Assert.Equal(["supervisor", "administrator"], placeOrder.RequiredRole);
    }

    [Fact]
    public void ReadModel_requiredRole_maps_as_a_list_whether_declared_as_one_value_or_an_array()
    {
        // Schema 2.7.0's read-side mirror of command.requiredRole -- same
        // RoleOrRolesConverter, same normalize-to-a-list behavior.
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);

        var result = DocumentMapper.Map(doc);

        var order = result.Domains.Single(d => d.Aggregate == "order");
        var orders = order.ReadModels.Single(rm => rm.Collection == "orders");
        Assert.Equal(["supervisor"], orders.RequiredRole);
        var regionSupervisors = order.ReadModels.Single(rm => rm.Collection == "regionSupervisors");
        Assert.Equal(["supervisor", "administrator"], regionSupervisors.RequiredRole);
    }

    [Fact]
    public void RequiredOwnership_resolves_its_via_read_model_to_a_physical_collection_name()
    {
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);

        var result = DocumentMapper.Map(doc);

        var order = result.Domains.Single(d => d.Aggregate == "order");
        var updateOrder = order.Commands.Single(c => c.Name == "UpdateOrder");
        Assert.NotNull(updateOrder.RequiredOwnership);
        var ownership = updateOrder.RequiredOwnership!;
        Assert.Equal("orders", ownership.ViaCollection);
        Assert.Equal("orderId", ownership.KeyField);
        Assert.Equal("ownerId", ownership.OwnerField);
        Assert.Empty(ownership.BypassRoles);
    }

    [Fact]
    public void Scope_resolves_both_via_read_models_and_carries_bypassRoles()
    {
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);

        var result = DocumentMapper.Map(doc);

        var order = result.Domains.Single(d => d.Aggregate == "order");
        var flagOrder = order.Commands.Single(c => c.Name == "FlagOrder");
        Assert.NotNull(flagOrder.Scope);
        var scope = flagOrder.Scope!;
        Assert.Equal(["supervisor"], scope.BypassRoles);
        Assert.Equal("orders", scope.ResolveViaCollection);
        Assert.Equal("orderId", scope.ResolveKeyField);
        Assert.Equal("regionId", scope.ResolveSelectField);
        Assert.Equal("regionSupervisors", scope.MemberOfViaCollection);
        Assert.Equal("regionId", scope.MemberOfMatchField);
        Assert.Equal("memberId", scope.MemberOfActorField);
    }

    [Fact]
    public void FieldGatedRole_resolves_a_string_value_and_keeps_the_payload_field_name_unsanitized()
    {
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);

        var result = DocumentMapper.Map(doc);

        var member = result.Domains.Single(d => d.Aggregate == "member");
        var onboardMember = member.Commands.Single(c => c.Name == "OnboardMember");
        Assert.NotNull(onboardMember.FieldGatedRole);
        var fieldGated = onboardMember.FieldGatedRole!;
        Assert.Equal("role", fieldGated.Field);
        Assert.Equal(new StringFieldValue("supervisor"), fieldGated.Value);
        Assert.Equal(["administrator"], fieldGated.RequiredRole);
    }

    [Fact]
    public void RequiredOwnership_referencing_a_nonexistent_read_model_is_rejected_with_a_clear_reason()
    {
        var doc = DocumentLoader.Parse(CommandAuthorizationDocumentJson);
        var updateOrder = doc.Commands!["update-order"];
        var mutatedOwnership = updateOrder.RequiredOwnership! with
        {
            Via = updateOrder.RequiredOwnership!.Via with { ReadModelId = "no-such-read-model" },
        };
        var mutatedCommands = new Dictionary<string, DotnetCqrs.Codegen.Model.CommandDef>(doc.Commands!)
        {
            ["update-order"] = updateOrder with { RequiredOwnership = mutatedOwnership },
        };
        var mutatedDoc = doc with { Commands = mutatedCommands };

        var ex = Assert.Throws<DocumentMappingException>(() => DocumentMapper.Map(mutatedDoc));
        Assert.Contains(ex.Report.Errors, e =>
            e.Contains("update-order") && e.Contains("requiredOwnership") && e.Contains("no-such-read-model"));
    }
}
