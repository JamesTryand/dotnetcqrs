using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

// Compiles generated code in memory (InProcessHarness): seconds each, but Roslyn is the bulk of
// the everyday set. The tightest loop skips it: --filter "Category!=Slow&Category!=Compiles".
[Trait("Category", "Compiles")]
public class ScenarioVerifierTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    // Every test here compiles the harness with the generated code in memory and runs it
    // in-process (InProcessHarness): seconds, where the old scratch `dotnet build` + `run`
    // took minutes on a small machine. The budget stays generous for a loaded one.
    private const int VerifyTimeoutMs = 120000;

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Verifies_every_scenario_kind_in_the_order_fulfillment_document()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        // order-fulfillment.json declares exactly one scenario per slice (4 slices):
        // place-order (stateChange), order-status (stateView), auto-ship
        // (stateChange, same-aggregate automation), notify-shipping-partner
        // (stateChange, cross-aggregate automation).
        Assert.Equal(4, results.Count);

        var placeOrder = results.Single(r => r.ScenarioId == "place-order-happy-path");
        Assert.Equal("stateChange", placeOrder.Kind);
        Assert.True(placeOrder.Passed, placeOrder.Detail);

        // EXPECTED to fail, not a verifier or generator bug: "status" is a derived
        // value ("placed" once order-placed happened, "shipped" once order-shipped
        // does) that neither event's own JSON payload literally carries -- the
        // generic field-merge projection (see ProjectionGenerator's own doc comment)
        // can only copy fields present in an event's data, and computing "status"
        // from *which event type fired* is exactly the kind of real per-event rule
        // "the author's job" means. The verifier correctly
        // catching this, rather than silently passing, is the whole point of the
        // verifier -- so this scenario failing IS the passing assertion for the test.
        var orderStatus = results.Single(r => r.ScenarioId == "view-order-summary-after-placement");
        Assert.Equal("stateView", orderStatus.Kind);
        Assert.False(orderStatus.Passed);
        Assert.Contains("status", orderStatus.Detail);

        var autoShip = results.Single(r => r.ScenarioId == "auto-ship-triggers-on-order-placed");
        Assert.Equal("stateChange", autoShip.Kind);
        Assert.True(autoShip.Passed, autoShip.Detail);

        // this is the cross-aggregate one: given (order-shipped) belongs to "order",
        // the dispatched command (notify-shipping-partner) belongs to
        // "shippingNotification" -- passing here proves SplitGiven's own/foreign
        // split and the aggregate-resolution plumbing are both correct end to end.
        var notify = results.Single(r => r.ScenarioId == "notify-partner-on-shipment");
        Assert.Equal("stateChange", notify.Kind);
        Assert.True(notify.Passed, notify.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_error_scenario_that_should_be_rejected_is_reported_as_passed()
    {
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "error-test", "name": "Error Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [
                  {
                    "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                    "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                  },
                  {
                    "id": "duplicate-rejected", "name": "A second PlaceOrder is refused", "kind": "error",
                    "given": [{"eventId": "order-placed"}], "when": {"commandId": "place-order"},
                    "then": {"error": {"message": "order already exists"}}
                  }
                ]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));

        var errorScenario = results.Single(r => r.ScenarioId == "duplicate-rejected");
        Assert.Equal("error", errorScenario.Kind);
        Assert.Contains("refused", errorScenario.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_list_field_the_command_does_not_carry_is_recorded_as_null_not_a_crash()
    {
        // Two commands carrying a list field failed --verify with "Operation is not
        // valid due to the current state of the object". The generated payload record held a list
        // (json) field as a non-nullable JsonElement, so a field the command doesn't carry was a
        // default JsonElement, and serialising it threw. It must be null instead.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "list-field-test", "name": "List Field Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s", "fields": [
                {"name": "orderId", "type": "string"}, {"name": "lines", "type": "custom", "cardinality": "list"}]}},
              "commands": {"place-order": {"name": "Place Order", "fields": [{"name": "orderId", "type": "string"}]}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [{
                  "id": "place-without-lines", "name": "The command carries no lines", "kind": "stateChange",
                  "given": [], "when": {"commandId": "place-order", "data": {"orderId": "o1"}},
                  "then": {"events": [{"eventId": "order-placed"}]}
                }]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var result = Assert.Single(results);
        Assert.True(result.Passed, result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_error_scenario_refused_for_another_reason_fails_and_names_both_messages()
    {
        // The harness used to pass an error scenario on ANY refusal. Three
        // scenarios passed because the PII protector refused a payload, not because
        // of the rule they describe, and another passed on a
        // crash. The decider's message must be the scenario's then.error.message.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "error-reason-test", "name": "Error Reason Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [
                  {
                    "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                    "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                  },
                  {
                    "id": "closed-rejected", "name": "A closed order refuses a PlaceOrder", "kind": "error",
                    "given": [{"eventId": "order-placed"}], "when": {"commandId": "place-order"},
                    "then": {"error": {"message": "order is closed"}}
                  }
                ]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var errorScenario = results.Single(r => r.ScenarioId == "closed-rejected");
        Assert.False(errorScenario.Passed, errorScenario.Detail);
        Assert.Contains("order is closed", errorScenario.Detail);
        Assert.Contains("order already exists", errorScenario.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_scenario_whose_read_model_was_skipped_during_mapping_is_reported_skipped_not_failed()
    {
        // A read model with no builtFromEventIds is a WARNING during mapping (the
        // generated projection would never fire, so the mapper skips it) rather than
        // an ERROR -- unlike an untagged command (always a hard mapping failure, so
        // DocumentMapper.Map never returns a MappingResult for ScenarioVerifier to see
        // in that case at all). This is the genuinely reachable "referenced but not
        // generated" path: mapping succeeds, but "empty-rm" is absent from the
        // resulting Domain, so the scenario referencing it must be Skipped, not
        // silently treated as a failure it never had a chance to pass.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "skip-test", "name": "Skip Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order", "aggregate": "Order"}},
              "readModels": {"empty-rm": {"name": "Empty RM"}},
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "place-order", "eventIds": ["order-placed"],
                  "scenarios": []
                },
                {
                  "id": "view-slice", "name": "View", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "empty-rm",
                  "scenarios": [{
                    "id": "view-scenario", "name": "Should be skipped", "kind": "stateView",
                    "given": [], "when": {"readModelId": "empty-rm"}, "then": {"result": {}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc); // succeeds -- the empty read model is a warning, not an error
        Assert.Contains(mapped.Report.Warnings, w => w.Contains("empty-rm") && w.Contains("skipped"));
        Assert.Empty(mapped.Domains.Single().ReadModels); // confirms it's genuinely absent, not just under-tested

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.False(result.Passed);
        Assert.Contains("empty-rm", result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_toggle_derivation_sets_the_field_from_which_event_fired()
    {
        // A case like member-directory.ssoEnabled: the generic field-merge can
        // only copy a literal payload key, and no event carries one named "ssoEnabled"
        // -- the toggle derivation computes it from which EVENT TYPE fired instead.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "toggle-test", "name": "Toggle Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "member-added": {"name": "Member Added", "swimlaneId": "s", "aggregate": "Member"},
                "member-sso-enabled": {"name": "Member SSO Enabled", "swimlaneId": "s", "aggregate": "Member"},
                "member-sso-disabled": {"name": "Member SSO Disabled", "swimlaneId": "s", "aggregate": "Member"}
              },
              "commands": {
                "add-member": {"name": "Add Member", "aggregate": "Member"},
                "enable-member-sso": {"name": "Enable Member SSO", "aggregate": "Member"}
              },
              "readModels": {
                "member-directory": {
                  "name": "Member Directory",
                  "builtFromEventIds": ["member-added"],
                  "fields": [
                    {"name": "memberId", "type": "string", "idAttribute": true},
                    {"name": "ssoEnabled", "type": "boolean",
                      "derivation": {"kind": "toggle", "onEventIds": ["member-sso-enabled"], "offEventIds": ["member-sso-disabled"], "initial": false}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Add Screen"}, "scr2": {"name": "Enable Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "add-member-slice", "name": "Add Member", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "add-member", "eventIds": ["member-added"],
                  "scenarios": [{"id":"add-scenario","name":"Add","kind":"stateChange","given":[],"when":{"commandId":"add-member"},"then":{"events":[{"eventId":"member-added"}]}}]
                },
                {
                  "id": "enable-sso-slice", "name": "Enable SSO", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "enable-member-sso", "eventIds": ["member-sso-enabled"],
                  "scenarios": [{"id":"enable-scenario","name":"Enable","kind":"stateChange","given":[{"eventId":"member-added"}],"when":{"commandId":"enable-member-sso"},"then":{"events":[{"eventId":"member-sso-enabled"}]}}]
                },
                {
                  "id": "view-directory-slice", "name": "View Directory", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "member-directory",
                  "scenarios": [{
                    "id": "view-after-enable", "name": "SSO shows enabled", "kind": "stateView",
                    "given": [{"eventId": "member-added"}, {"eventId": "member-sso-enabled"}],
                    "when": {"readModelId": "member-directory"},
                    "then": {"result": {"ssoEnabled": true}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-after-enable");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = 300000)]
    public async Task A_view_over_a_pii_column_is_verified_against_plaintext_through_the_real_reveal_path()
    {
        // The given events are sealed the way the real
        // write path seals them, the projection stores the envelope, and the rows are
        // revealed through the same PiiColumnRevealer a query route uses. So the scenario
        // states plaintext and is compared with what a caller would actually see.
        const string json = """
            {
              "eventModelingSchemaVersion": "3.0.0", "id": "pii-view-test", "name": "Pii View Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
                  "fields": [
                    {"name": "customerId", "type": "string", "idAttribute": true},
                    {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
                  ]}
              },
              "commands": {
                "register-customer": {"name": "Register Customer", "aggregate": "Customer",
                  "fields": [
                    {"name": "customerId", "type": "string", "idAttribute": true},
                    {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
                  ]}
              },
              "readModels": {
                "customers": {
                  "name": "Customers",
                  "builtFromEventIds": ["customer-registered"],
                  "fields": [
                    {"name": "customerId", "type": "string", "idAttribute": true},
                    {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Register Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "register-slice", "name": "Register", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"],
                  "scenarios": []
                },
                {
                  "id": "view-customers-slice", "name": "View Customers", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "customers",
                  "scenarios": [
                    {
                      "id": "all-customers", "name": "Every customer, emails in the clear", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers"},
                      "then": {"result": {"customers": [
                        {"customerId": "c1", "email": "alice@example.com"},
                        {"customerId": "c2", "email": "bob@example.com"}
                      ]}}
                    },
                    {
                      "id": "wrong-email", "name": "A wrong expectation still fails", "kind": "stateView",
                      "given": [{"eventId": "customer-registered", "data": {"customerId": "c1", "email": "alice@example.com"}}],
                      "when": {"readModelId": "customers", "queryParams": {"customerId": "c1"}},
                      "then": {"result": {"email": "mallory@example.com"}}
                    }
                  ]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var all = results.Single(r => r.ScenarioId == "all-customers");
        Assert.True(all.Passed, all.Detail);
        var wrong = results.Single(r => r.ScenarioId == "wrong-email");
        Assert.False(wrong.Passed);
        Assert.Contains("alice@example.com", wrong.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task View_scenarios_run_match_queries_through_the_routes_own_sql_pii_included()
    {
        // A scenario's queryParams can name match filters. The harness uses the
        // clause GenerationSupport.MatchClause builds for the route, the same normalizer and
        // pattern, and for a pii field a real temporary search.db filled by the generated
        // search index from the (sealed) given events.
        const string json = """
            {
              "eventModelingSchemaVersion": "3.1.0", "id": "match-view-test", "name": "Match View Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
                  "fields": [
                    {"name": "customerId", "type": "string", "idAttribute": true},
                    {"name": "name", "type": "string"},
                    {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
                  ]}
              },
              "commands": {"register-customer": {"name": "Register Customer", "aggregate": "Customer"}},
              "readModels": {
                "customers": {
                  "name": "Customers",
                  "builtFromEventIds": ["customer-registered"],
                  "fields": [
                    {"name": "customerId", "type": "string", "idAttribute": true},
                    {"name": "name", "type": "string"},
                    {"name": "email", "type": "string", "pii": true, "piiSubject": "customerId"}
                  ],
                  "filters": [
                    {"param": "nameSearch", "field": "name", "kind": "match", "mode": "contains", "normalize": "personName"},
                    {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"},
                    {"param": "emailExact", "field": "email", "kind": "match", "mode": "exact", "normalize": "email"},
                    {"param": "emailPrefix", "field": "email", "kind": "match", "mode": "prefix", "normalize": "email", "minPrefixLength": 2}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Register Screen"}, "scr2": {"name": "Search Screen"}},
              "slices": [
                {
                  "id": "register-slice", "name": "Register", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"],
                  "scenarios": []
                },
                {
                  "id": "search-slice", "name": "Search Customers", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "customers",
                  "scenarios": [
                    {
                      "id": "by-name", "name": "Name search ignores case and accents", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "name": "Bob", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"nameSearch": "nunez"}},
                      "then": {"result": {"customers": [{"customerId": "c1", "email": "alice@example.com"}]}}
                    },
                    {
                      "id": "by-email", "name": "Email search finds the pii field", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "name": "Bob", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"emailSearch": "BOB@"}},
                      "then": {"result": {"customers": [{"customerId": "c2", "name": "Bob"}]}}
                    },
                    {
                      "id": "wrong-hit", "name": "A wrong expectation still fails", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "name": "Bob", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"emailSearch": "alice"}},
                      "then": {"result": {"customers": [{"customerId": "c2"}]}}
                    },
                    {
                      "id": "by-email-exact", "name": "Exact email search goes through the keyed-hash index", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "name": "Bob", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"emailExact": " ALICE@Example.com"}},
                      "then": {"result": {"customers": [{"customerId": "c1", "email": "alice@example.com"}]}}
                    },
                    {
                      "id": "by-email-prefix", "name": "Prefix email search goes through the keyed-hash index", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}},
                        {"eventId": "customer-registered", "data": {"customerId": "c2", "name": "Bob", "email": "bob@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"emailPrefix": "BO"}},
                      "then": {"result": {"customers": [{"customerId": "c2", "name": "Bob"}]}}
                    },
                    {
                      "id": "wrong-exact", "name": "Exact is not prefix, so this expectation fails", "kind": "stateView",
                      "given": [
                        {"eventId": "customer-registered", "data": {"customerId": "c1", "name": "José Núñez", "email": "alice@example.com"}}
                      ],
                      "when": {"readModelId": "customers", "queryParams": {"emailExact": "alice"}},
                      "then": {"result": {"customers": [{"customerId": "c1"}]}}
                    }
                  ]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var byName = results.Single(r => r.ScenarioId == "by-name");
        Assert.True(byName.Passed, byName.Detail);
        var byEmail = results.Single(r => r.ScenarioId == "by-email");
        Assert.True(byEmail.Passed, byEmail.Detail);
        Assert.False(results.Single(r => r.ScenarioId == "wrong-hit").Passed);
        // Pii exact/prefix hash the normalized term with the harness's index key.
        var byExact = results.Single(r => r.ScenarioId == "by-email-exact");
        Assert.True(byExact.Passed, byExact.Detail);
        var byPrefix = results.Single(r => r.ScenarioId == "by-email-prefix");
        Assert.True(byPrefix.Passed, byPrefix.Detail);
        Assert.False(results.Single(r => r.ScenarioId == "wrong-exact").Passed);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_count_derivation_rolls_up_across_streams()
    {
        // A case like regions.courierCount: the counted events
        // (courier-assigned-to-region) live on the ASSIGNMENT stream, not the region's
        // own -- the generic one-row-per-stream projection can't key on ev.AggregateId
        // for them at all. The count derivation keys on the event's own payload
        // (rowKeyField, defaulted here to "regionId") instead.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "count-test", "name": "Count Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "region-created": {"name": "Region Created", "swimlaneId": "s", "aggregate": "Region",
                  "fields": [{"name": "regionId", "type": "string", "idAttribute": true}]},
                "courier-assigned-to-region": {"name": "Courier Assigned To Region", "swimlaneId": "s", "aggregate": "RegionCourierAssignment",
                  "fields": [{"name": "regionId", "type": "string"}]}
              },
              "commands": {
                "create-region": {"name": "Create Region", "aggregate": "Region"},
                "assign-courier-to-region": {"name": "Assign Courier To Region", "aggregate": "RegionCourierAssignment"}
              },
              "readModels": {
                "regions": {
                  "name": "Regions",
                  "builtFromEventIds": ["region-created"],
                  "fields": [
                    {"name": "regionId", "type": "string", "idAttribute": true},
                    {"name": "courierCount", "type": "integer",
                      "derivation": {"kind": "count", "incrementOnEventIds": ["courier-assigned-to-region"]}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "Assign Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "create-region-slice", "name": "Create Region", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "create-region", "eventIds": ["region-created"],
                  "scenarios": [{"id":"create-scenario","name":"Create","kind":"stateChange","given":[],"when":{"commandId":"create-region"},"then":{"events":[{"eventId":"region-created"}]}}]
                },
                {
                  "id": "assign-courier-slice", "name": "Assign Courier", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "assign-courier-to-region", "eventIds": ["courier-assigned-to-region"],
                  "scenarios": [{"id":"assign-scenario","name":"Assign","kind":"stateChange","given":[],"when":{"commandId":"assign-courier-to-region"},"then":{"events":[{"eventId":"courier-assigned-to-region"}]}}]
                },
                {
                  "id": "view-regions-slice", "name": "View Regions", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "regions",
                  "scenarios": [{
                    "id": "view-after-assign", "name": "Courier count reflects the assignment", "kind": "stateView",
                    "given": [
                      {"eventId": "region-created", "data": {"regionId": "p1"}},
                      {"eventId": "courier-assigned-to-region", "data": {"regionId": "p1"}}
                    ],
                    "when": {"readModelId": "regions"},
                    "then": {"result": {"courierCount": 1}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-after-assign");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_count_derivation_honours_an_explicit_rowKeyField_that_differs_from_the_read_models_own_key()
    {
        // Regression for a bug found in review: the counted event's payload here names
        // the target region "forRegionId", NOT "regionId" (the "regions" read
        // model's own idAttribute/key column) -- exactly the schema's documented
        // "explicit override for when they differ" case. EmitRollup must still filter
        // the UPDATE on the read model's own key column (region_id), extracting the
        // VALUE via rowKeyField ("forRegionId") -- not generate a WHERE clause against
        // a for_region_id column, which the regions table never has.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "count-rowkey-test", "name": "Count RowKey Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "region-created": {"name": "Region Created", "swimlaneId": "s", "aggregate": "Region",
                  "fields": [{"name": "regionId", "type": "string", "idAttribute": true}]},
                "courier-assigned-to-region": {"name": "Courier Assigned To Region", "swimlaneId": "s", "aggregate": "RegionCourierAssignment",
                  "fields": [{"name": "forRegionId", "type": "string"}]}
              },
              "commands": {
                "create-region": {"name": "Create Region", "aggregate": "Region"},
                "assign-courier-to-region": {"name": "Assign Courier To Region", "aggregate": "RegionCourierAssignment"}
              },
              "readModels": {
                "regions": {
                  "name": "Regions",
                  "builtFromEventIds": ["region-created"],
                  "fields": [
                    {"name": "regionId", "type": "string", "idAttribute": true},
                    {"name": "courierCount", "type": "integer",
                      "derivation": {"kind": "count", "incrementOnEventIds": ["courier-assigned-to-region"], "rowKeyField": "forRegionId"}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "Assign Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "create-region-slice", "name": "Create Region", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "create-region", "eventIds": ["region-created"],
                  "scenarios": [{"id":"create-scenario","name":"Create","kind":"stateChange","given":[],"when":{"commandId":"create-region"},"then":{"events":[{"eventId":"region-created"}]}}]
                },
                {
                  "id": "assign-courier-slice", "name": "Assign Courier", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "assign-courier-to-region", "eventIds": ["courier-assigned-to-region"],
                  "scenarios": [{"id":"assign-scenario","name":"Assign","kind":"stateChange","given":[],"when":{"commandId":"assign-courier-to-region"},"then":{"events":[{"eventId":"courier-assigned-to-region"}]}}]
                },
                {
                  "id": "view-regions-slice", "name": "View Regions", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "regions",
                  "scenarios": [{
                    "id": "view-after-assign", "name": "Courier count reflects the assignment", "kind": "stateView",
                    "given": [
                      {"eventId": "region-created", "data": {"regionId": "p1"}},
                      {"eventId": "courier-assigned-to-region", "data": {"forRegionId": "p1"}}
                    ],
                    "when": {"readModelId": "regions"},
                    "then": {"result": {"courierCount": 1}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-after-assign");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_groupBy_derivation_produces_one_nested_row_per_distinct_group_key()
    {
        // Schema 2.3.0's reporting-periods.sellerTotals shape: sale-recorded lives on a
        // DIFFERENT aggregate (Sale) than the read model it rolls up into
        // (ReportingPeriod) -- same cross-stream shape as the count-derivation tests above.
        // sellerTotals nests one row per distinct sellerId, each with its own sum
        // (weekendUnits) computed WITHIN that group -- two contributions for "s1"
        // (5 + 3) must land in the SAME nested entry, not two separate ones, and "s2"'s
        // single contribution must land in its own entry untouched by s1's.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.3.0", "id": "groupby-verify-test", "name": "GroupBy Verify Test",
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
                    "id": "view-after-log", "name": "Seller totals reflect recorded sales, grouped by seller", "kind": "stateView",
                    "given": [
                      {"eventId": "period-created", "data": {"periodId": "p1"}},
                      {"eventId": "sale-recorded", "data": {"periodId": "p1", "sellerId": "s1", "units": 5}},
                      {"eventId": "sale-recorded", "data": {"periodId": "p1", "sellerId": "s2", "units": 3}},
                      {"eventId": "sale-recorded", "data": {"periodId": "p1", "sellerId": "s1", "units": 3}}
                    ],
                    "when": {"readModelId": "reporting-periods"},
                    "then": {"result": {
                      "periodId": "p1",
                      "sellerTotals": [
                        {"sellerId": "s1", "weekendUnits": 8},
                        {"sellerId": "s2", "weekendUnits": 3}
                      ]
                    }}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-after-log");
        Assert.True(view.Passed, view.Detail);
    }

    [Theory(Timeout = VerifyTimeoutMs)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public async Task A_custom_field_copied_from_an_event_is_compared_with_its_own_keys(int expectedRate, bool passes)
    {
        // A rate's history is a custom list carried on its event and stored as the
        // event wrote it, so its objects keep the event's camelCase keys. Only a groupBy row's objects
        // are built with snake_case keys; comparing every nested key as snake_case meant a custom
        // field's view scenario could never pass. A wrong value must still fail.
        var json = """
            {
              "eventModelingSchemaVersion": "3.10.0", "id": "custom-verify-test", "name": "Custom Verify Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "rate-set": {"name": "Rate Set", "swimlaneId": "s", "aggregate": "Rate",
                  "fields": [{"name": "rateId", "type": "string", "idAttribute": true}, {"name": "history", "type": "custom"}]}
              },
              "commands": {"set-rate": {"name": "Set Rate", "aggregate": "Rate"}},
              "readModels": {
                "rates": {"name": "Rates", "builtFromEventIds": ["rate-set"],
                  "fields": [{"name": "rateId", "type": "string", "idAttribute": true}, {"name": "history", "type": "custom"}]}
              },
              "screens": {"scr1": {"name": "Set Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {"id": "set-rate-slice", "name": "Set Rate", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "set-rate", "eventIds": ["rate-set"],
                  "scenarios": [{"id":"set","name":"Set","kind":"stateChange","given":[],"when":{"commandId":"set-rate"},"then":{"events":[{"eventId":"rate-set"}]}}]},
                {"id": "view-rates-slice", "name": "View Rates", "pattern": "stateView", "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "rates",
                  "scenarios": [{"id": "view-history", "name": "The history as the event carried it", "kind": "stateView",
                    "given": [{"eventId": "rate-set", "data": {"rateId": "r1", "history": [{"effectiveFrom": "2026-09-01", "premiumRate": 65, "rate": 50}]}}],
                    "when": {"readModelId": "rates"},
                    "then": {"result": {"entries": [{"rateId": "r1", "history": [{"effectiveFrom": "2026-09-01", "rate": EXPECTED_RATE}]}]}}}]}
              ]
            }
            """.Replace("EXPECTED_RATE", expectedRate.ToString());
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-history");
        Assert.True(view.Passed == passes, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_dateRange_filter_narrows_a_view_query_to_the_declared_bounds()
    {
        // Before this, ANY object-shaped
        // queryParams value (exactly the `{kind, from, to}` shape a real dateRange
        // param uses) was silently skipped by the harness -- this is the regression
        // test that it now becomes a real predicate instead. "custom" is used (not
        // last7Days/lastCalendarMonth) so the scenario's own expected bounds don't
        // depend on when the test happens to run.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.4.0", "id": "daterange-verify-test", "name": "DateRange Verify Test",
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
                  "scenarios": [{
                    "id": "view-within-custom-range", "name": "Only shipments inside the custom date range are returned", "kind": "stateView",
                    "given": [
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e1", "shipDate": "2026-08-15", "weightKg": 4}},
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e2", "shipDate": "2026-08-20", "weightKg": 3}},
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e3", "shipDate": "2026-09-01", "weightKg": 2}}
                    ],
                    "when": {"readModelId": "shipments", "queryParams": {"dateRange": {"kind": "custom", "from": "2026-08-01", "to": "2026-08-31"}}},
                    "then": {"result": {"entries": [
                      {"shipmentId": "e1", "shipDate": "2026-08-15", "weightKg": 4},
                      {"shipmentId": "e2", "shipDate": "2026-08-20", "weightKg": 3}
                    ]}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-within-custom-range");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_asOf_pin_resolves_a_last7Days_preset_against_a_fixed_date_not_the_live_clock()
    {
        // Schema 2.6.0's readModelQuery.asOf: without it, last7Days/lastCalendarMonth
        // resolve "today" from DateTime.UtcNow, so this exact scenario would only pass
        // on whatever 7-day window contains the real date the test happens to run on --
        // the drift that motivated asOf in the first place (an export slice over
        // a date window). asOf: "2026-09-06" pins the window to
        // 2026-08-31..2026-09-06 regardless of when this test actually executes: if the
        // harness ignored asOf and used the live clock instead, e2 (2026-09-01) would
        // fall outside today's real last7Days window and this assertion would fail.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.6.0", "id": "asof-verify-test", "name": "AsOf Verify Test",
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
                  "scenarios": [{
                    "id": "view-within-pinned-last7days-window", "name": "Only shipments inside the asOf-pinned last7Days window are returned", "kind": "stateView",
                    "given": [
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e1", "shipDate": "2026-08-30", "weightKg": 4}},
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e2", "shipDate": "2026-09-01", "weightKg": 3}},
                      {"eventId": "shipment-logged", "data": {"shipmentId": "e3", "shipDate": "2026-09-06", "weightKg": 2}}
                    ],
                    "when": {"readModelId": "shipments", "asOf": "2026-09-06", "queryParams": {"dateRange": {"kind": "last7Days"}}},
                    "then": {"result": {"entries": [
                      {"shipmentId": "e2", "shipDate": "2026-09-01", "weightKg": 3},
                      {"shipmentId": "e3", "shipDate": "2026-09-06", "weightKg": 2}
                    ]}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped);

        var view = results.Single(r => r.ScenarioId == "view-within-pinned-last7days-window");
        Assert.True(view.Passed, view.Detail);
    }
}
