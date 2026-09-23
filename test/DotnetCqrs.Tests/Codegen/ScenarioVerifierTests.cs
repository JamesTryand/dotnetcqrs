using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;

namespace DotnetCqrs.Tests.Codegen;

public class ScenarioVerifierTests
{
    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    /// <summary>Walks up from the test's own output directory to find the repo root
    /// (marked by dotnetcqrs.slnx) -- same approach as CSharpGeneratorTests.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private static string DotnetCqrsProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj");

    // Every test here compiles a scratch harness (dotnet build + run). That takes 25-55s
    // warm, so the old 60s budget failed two tests under full-suite load on 2026-09-23;
    // both passed alone in ~30s. Same budget, and the same reason, as CliTests' verify tests.
    private const int VerifyTimeoutMs = 300000;

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Verifies_every_scenario_kind_in_the_order_fulfillment_document()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

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
        // "the author's job" (Milestone 3's own framing) means. The verifier correctly
        // catching this, rather than silently passing, is the whole point of this
        // milestone -- so this scenario failing IS the passing assertion for the test.
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

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, $"{r.ScenarioId}: {r.Detail}"));

        var errorScenario = results.Single(r => r.ScenarioId == "duplicate-rejected");
        Assert.Equal("error", errorScenario.Kind);
        Assert.Contains("refused", errorScenario.Detail);
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

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var result = Assert.Single(results);
        Assert.True(result.Skipped);
        Assert.False(result.Passed);
        Assert.Contains("empty-rm", result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_toggle_derivation_sets_the_field_from_which_event_fired()
    {
        // Finding 3's case #2 (staff-roster.ssoEnabled): the generic field-merge can
        // only copy a literal payload key, and no event carries one named "ssoEnabled"
        // -- the toggle derivation computes it from which EVENT TYPE fired instead.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "toggle-test", "name": "Toggle Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "staff-added": {"name": "Staff Added", "swimlaneId": "s", "aggregate": "Staff"},
                "staff-sso-enabled": {"name": "Staff SSO Enabled", "swimlaneId": "s", "aggregate": "Staff"},
                "staff-sso-disabled": {"name": "Staff SSO Disabled", "swimlaneId": "s", "aggregate": "Staff"}
              },
              "commands": {
                "add-staff": {"name": "Add Staff", "aggregate": "Staff"},
                "enable-staff-sso": {"name": "Enable Staff SSO", "aggregate": "Staff"}
              },
              "readModels": {
                "staff-roster": {
                  "name": "Staff Roster",
                  "builtFromEventIds": ["staff-added"],
                  "fields": [
                    {"name": "staffId", "type": "string", "idAttribute": true},
                    {"name": "ssoEnabled", "type": "boolean",
                      "derivation": {"kind": "toggle", "onEventIds": ["staff-sso-enabled"], "offEventIds": ["staff-sso-disabled"], "initial": false}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Add Screen"}, "scr2": {"name": "Enable Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "add-staff-slice", "name": "Add Staff", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "add-staff", "eventIds": ["staff-added"],
                  "scenarios": [{"id":"add-scenario","name":"Add","kind":"stateChange","given":[],"when":{"commandId":"add-staff"},"then":{"events":[{"eventId":"staff-added"}]}}]
                },
                {
                  "id": "enable-sso-slice", "name": "Enable SSO", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "enable-staff-sso", "eventIds": ["staff-sso-enabled"],
                  "scenarios": [{"id":"enable-scenario","name":"Enable","kind":"stateChange","given":[{"eventId":"staff-added"}],"when":{"commandId":"enable-staff-sso"},"then":{"events":[{"eventId":"staff-sso-enabled"}]}}]
                },
                {
                  "id": "view-roster-slice", "name": "View Roster", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "staff-roster",
                  "scenarios": [{
                    "id": "view-after-enable", "name": "SSO shows enabled", "kind": "stateView",
                    "given": [{"eventId": "staff-added"}, {"eventId": "staff-sso-enabled"}],
                    "when": {"readModelId": "staff-roster"},
                    "then": {"result": {"ssoEnabled": true}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-after-enable");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = 300000)]
    public async Task A_view_over_a_pii_column_is_verified_against_plaintext_through_the_real_reveal_path()
    {
        // Milestone D3 (the read half of E): the given events are sealed the way the real
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

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var all = results.Single(r => r.ScenarioId == "all-customers");
        Assert.True(all.Passed, all.Detail);
        var wrong = results.Single(r => r.ScenarioId == "wrong-email");
        Assert.False(wrong.Passed);
        Assert.Contains("alice@example.com", wrong.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task View_scenarios_run_match_queries_through_the_routes_own_sql_pii_included()
    {
        // Milestone D5: a scenario's queryParams can name match filters. The harness uses the
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
                    {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"}
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
                    }
                  ]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var byName = results.Single(r => r.ScenarioId == "by-name");
        Assert.True(byName.Passed, byName.Detail);
        var byEmail = results.Single(r => r.ScenarioId == "by-email");
        Assert.True(byEmail.Passed, byEmail.Detail);
        Assert.False(results.Single(r => r.ScenarioId == "wrong-hit").Passed);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_count_derivation_rolls_up_across_streams()
    {
        // Finding 3's case #3 (projects.staffCount): the counted events
        // (staff-assigned-to-project) live on the ASSIGNMENT stream, not the project's
        // own -- the generic one-row-per-stream projection can't key on ev.AggregateId
        // for them at all. The count derivation keys on the event's own payload
        // (rowKeyField, defaulted here to "projectId") instead.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "count-test", "name": "Count Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "project-created": {"name": "Project Created", "swimlaneId": "s", "aggregate": "Project",
                  "fields": [{"name": "projectId", "type": "string", "idAttribute": true}]},
                "staff-assigned-to-project": {"name": "Staff Assigned To Project", "swimlaneId": "s", "aggregate": "ProjectStaffAssignment",
                  "fields": [{"name": "projectId", "type": "string"}]}
              },
              "commands": {
                "create-project": {"name": "Create Project", "aggregate": "Project"},
                "assign-staff-to-project": {"name": "Assign Staff To Project", "aggregate": "ProjectStaffAssignment"}
              },
              "readModels": {
                "projects": {
                  "name": "Projects",
                  "builtFromEventIds": ["project-created"],
                  "fields": [
                    {"name": "projectId", "type": "string", "idAttribute": true},
                    {"name": "staffCount", "type": "integer",
                      "derivation": {"kind": "count", "incrementOnEventIds": ["staff-assigned-to-project"]}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "Assign Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "create-project-slice", "name": "Create Project", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "create-project", "eventIds": ["project-created"],
                  "scenarios": [{"id":"create-scenario","name":"Create","kind":"stateChange","given":[],"when":{"commandId":"create-project"},"then":{"events":[{"eventId":"project-created"}]}}]
                },
                {
                  "id": "assign-staff-slice", "name": "Assign Staff", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "assign-staff-to-project", "eventIds": ["staff-assigned-to-project"],
                  "scenarios": [{"id":"assign-scenario","name":"Assign","kind":"stateChange","given":[],"when":{"commandId":"assign-staff-to-project"},"then":{"events":[{"eventId":"staff-assigned-to-project"}]}}]
                },
                {
                  "id": "view-projects-slice", "name": "View Projects", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "projects",
                  "scenarios": [{
                    "id": "view-after-assign", "name": "Staff count reflects the assignment", "kind": "stateView",
                    "given": [
                      {"eventId": "project-created", "data": {"projectId": "p1"}},
                      {"eventId": "staff-assigned-to-project", "data": {"projectId": "p1"}}
                    ],
                    "when": {"readModelId": "projects"},
                    "then": {"result": {"staffCount": 1}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-after-assign");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_count_derivation_honours_an_explicit_rowKeyField_that_differs_from_the_read_models_own_key()
    {
        // Regression for a bug found in review: the counted event's payload here names
        // the target project "forProjectId", NOT "projectId" (the "projects" read
        // model's own idAttribute/key column) -- exactly the schema's documented
        // "explicit override for when they differ" case. EmitRollup must still filter
        // the UPDATE on the read model's own key column (project_id), extracting the
        // VALUE via rowKeyField ("forProjectId") -- not generate a WHERE clause against
        // a for_project_id column, which the projects table never has.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "count-rowkey-test", "name": "Count RowKey Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "project-created": {"name": "Project Created", "swimlaneId": "s", "aggregate": "Project",
                  "fields": [{"name": "projectId", "type": "string", "idAttribute": true}]},
                "staff-assigned-to-project": {"name": "Staff Assigned To Project", "swimlaneId": "s", "aggregate": "ProjectStaffAssignment",
                  "fields": [{"name": "forProjectId", "type": "string"}]}
              },
              "commands": {
                "create-project": {"name": "Create Project", "aggregate": "Project"},
                "assign-staff-to-project": {"name": "Assign Staff To Project", "aggregate": "ProjectStaffAssignment"}
              },
              "readModels": {
                "projects": {
                  "name": "Projects",
                  "builtFromEventIds": ["project-created"],
                  "fields": [
                    {"name": "projectId", "type": "string", "idAttribute": true},
                    {"name": "staffCount", "type": "integer",
                      "derivation": {"kind": "count", "incrementOnEventIds": ["staff-assigned-to-project"], "rowKeyField": "forProjectId"}}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Create Screen"}, "scr2": {"name": "Assign Screen"}, "scr3": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "create-project-slice", "name": "Create Project", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "create-project", "eventIds": ["project-created"],
                  "scenarios": [{"id":"create-scenario","name":"Create","kind":"stateChange","given":[],"when":{"commandId":"create-project"},"then":{"events":[{"eventId":"project-created"}]}}]
                },
                {
                  "id": "assign-staff-slice", "name": "Assign Staff", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "assign-staff-to-project", "eventIds": ["staff-assigned-to-project"],
                  "scenarios": [{"id":"assign-scenario","name":"Assign","kind":"stateChange","given":[],"when":{"commandId":"assign-staff-to-project"},"then":{"events":[{"eventId":"staff-assigned-to-project"}]}}]
                },
                {
                  "id": "view-projects-slice", "name": "View Projects", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "projects",
                  "scenarios": [{
                    "id": "view-after-assign", "name": "Staff count reflects the assignment", "kind": "stateView",
                    "given": [
                      {"eventId": "project-created", "data": {"projectId": "p1"}},
                      {"eventId": "staff-assigned-to-project", "data": {"forProjectId": "p1"}}
                    ],
                    "when": {"readModelId": "projects"},
                    "then": {"result": {"staffCount": 1}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-after-assign");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_groupBy_derivation_produces_one_nested_row_per_distinct_group_key()
    {
        // Schema 2.3.0's payroll-periods.staffTotals shape: hours-logged lives on a
        // DIFFERENT aggregate (TimeEntry) than the read model it rolls up into
        // (PayrollPeriod) -- same cross-stream shape as the count-derivation tests above.
        // staffTotals nests one row per distinct staffId, each with its own sum
        // (outOfHoursHours) computed WITHIN that group -- two contributions for "s1"
        // (5 + 3) must land in the SAME nested entry, not two separate ones, and "s2"'s
        // single contribution must land in its own entry untouched by s1's.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.3.0", "id": "groupby-verify-test", "name": "GroupBy Verify Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "period-created": {"name": "Period Created", "swimlaneId": "s", "aggregate": "PayrollPeriod",
                  "fields": [{"name": "periodId", "type": "string", "idAttribute": true}]},
                "hours-logged": {"name": "Hours Logged", "swimlaneId": "s", "aggregate": "TimeEntry",
                  "fields": [
                    {"name": "periodId", "type": "string"},
                    {"name": "staffId", "type": "string"},
                    {"name": "hours", "type": "double"}
                  ]}
              },
              "commands": {
                "create-period": {"name": "Create Period", "aggregate": "PayrollPeriod"},
                "log-hours": {"name": "Log Hours", "aggregate": "TimeEntry"}
              },
              "readModels": {
                "payroll-periods": {
                  "name": "Payroll Periods",
                  "builtFromEventIds": ["period-created"],
                  "fields": [
                    {"name": "periodId", "type": "string", "idAttribute": true},
                    {"name": "staffTotals", "type": "custom", "cardinality": "list",
                      "derivation": {"kind": "groupBy", "groupByField": "staffId"},
                      "subfields": [
                        {"name": "staffId", "type": "string"},
                        {"name": "outOfHoursHours", "type": "double",
                          "derivation": {"kind": "sum", "addOnEventIds": ["hours-logged"], "amountField": "hours"}}
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
                  "id": "log-hours-slice", "name": "Log Hours", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "commandId": "log-hours", "eventIds": ["hours-logged"],
                  "scenarios": [{"id":"log-scenario","name":"Log","kind":"stateChange","given":[],"when":{"commandId":"log-hours"},"then":{"events":[{"eventId":"hours-logged"}]}}]
                },
                {
                  "id": "view-periods-slice", "name": "View Periods", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr3", "readModelId": "payroll-periods",
                  "scenarios": [{
                    "id": "view-after-log", "name": "Staff totals reflect logged hours, grouped by staff", "kind": "stateView",
                    "given": [
                      {"eventId": "period-created", "data": {"periodId": "p1"}},
                      {"eventId": "hours-logged", "data": {"periodId": "p1", "staffId": "s1", "hours": 5}},
                      {"eventId": "hours-logged", "data": {"periodId": "p1", "staffId": "s2", "hours": 3}},
                      {"eventId": "hours-logged", "data": {"periodId": "p1", "staffId": "s1", "hours": 3}}
                    ],
                    "when": {"readModelId": "payroll-periods"},
                    "then": {"result": {
                      "periodId": "p1",
                      "staffTotals": [
                        {"staffId": "s1", "outOfHoursHours": 8},
                        {"staffId": "s2", "outOfHoursHours": 3}
                      ]
                    }}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-after-log");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_dateRange_filter_narrows_a_view_query_to_the_declared_bounds()
    {
        // Group C item 4's grounding finding: before this, ANY object-shaped
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
                "time-entry-logged": {"name": "Time Entry Logged", "swimlaneId": "s", "aggregate": "TimeEntry",
                  "fields": [
                    {"name": "entryId", "type": "string", "idAttribute": true},
                    {"name": "taskDate", "type": "date"},
                    {"name": "hours", "type": "double"}
                  ]}
              },
              "commands": {
                "log-time-entry": {"name": "Log Time Entry", "aggregate": "TimeEntry"}
              },
              "readModels": {
                "time-entries": {
                  "name": "Time Entries",
                  "builtFromEventIds": ["time-entry-logged"],
                  "fields": [
                    {"name": "entryId", "type": "string", "idAttribute": true},
                    {"name": "taskDate", "type": "date"},
                    {"name": "hours", "type": "double"}
                  ],
                  "filters": [
                    {"param": "dateRange", "field": "taskDate", "kind": "dateRange", "presets": ["last7Days", "lastCalendarMonth", "custom"]}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Log Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "log-time-entry-slice", "name": "Log Time Entry", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "log-time-entry", "eventIds": ["time-entry-logged"],
                  "scenarios": [{"id":"log-scenario","name":"Log","kind":"stateChange","given":[],"when":{"commandId":"log-time-entry"},"then":{"events":[{"eventId":"time-entry-logged"}]}}]
                },
                {
                  "id": "view-time-entries-slice", "name": "View Time Entries", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "time-entries",
                  "scenarios": [{
                    "id": "view-within-custom-range", "name": "Only entries inside the custom date range are returned", "kind": "stateView",
                    "given": [
                      {"eventId": "time-entry-logged", "data": {"entryId": "e1", "taskDate": "2026-08-15", "hours": 4}},
                      {"eventId": "time-entry-logged", "data": {"entryId": "e2", "taskDate": "2026-08-20", "hours": 3}},
                      {"eventId": "time-entry-logged", "data": {"entryId": "e3", "taskDate": "2026-09-01", "hours": 2}}
                    ],
                    "when": {"readModelId": "time-entries", "queryParams": {"dateRange": {"kind": "custom", "from": "2026-08-01", "to": "2026-08-31"}}},
                    "then": {"result": {"entries": [
                      {"entryId": "e1", "taskDate": "2026-08-15", "hours": 4},
                      {"entryId": "e2", "taskDate": "2026-08-20", "hours": 3}
                    ]}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-within-custom-range");
        Assert.True(view.Passed, view.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task An_asOf_pin_resolves_a_last7Days_preset_against_a_fixed_date_not_the_live_clock()
    {
        // Schema 2.6.0's readModelQuery.asOf: without it, last7Days/lastCalendarMonth
        // resolve "today" from DateTime.UtcNow, so this exact scenario would only pass
        // on whatever 7-day window contains the real date the test happens to run on --
        // the drift that motivated asOf in the first place (see NEEDS.md item 8,
        // project/timesheets's export-pm-slice). asOf: "2026-09-06" pins the window to
        // 2026-08-31..2026-09-06 regardless of when this test actually executes: if the
        // harness ignored asOf and used the live clock instead, e2 (2026-09-01) would
        // fall outside today's real last7Days window and this assertion would fail.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.6.0", "id": "asof-verify-test", "name": "AsOf Verify Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "time-entry-logged": {"name": "Time Entry Logged", "swimlaneId": "s", "aggregate": "TimeEntry",
                  "fields": [
                    {"name": "entryId", "type": "string", "idAttribute": true},
                    {"name": "taskDate", "type": "date"},
                    {"name": "hours", "type": "double"}
                  ]}
              },
              "commands": {
                "log-time-entry": {"name": "Log Time Entry", "aggregate": "TimeEntry"}
              },
              "readModels": {
                "time-entries": {
                  "name": "Time Entries",
                  "builtFromEventIds": ["time-entry-logged"],
                  "fields": [
                    {"name": "entryId", "type": "string", "idAttribute": true},
                    {"name": "taskDate", "type": "date"},
                    {"name": "hours", "type": "double"}
                  ],
                  "filters": [
                    {"param": "dateRange", "field": "taskDate", "kind": "dateRange", "presets": ["last7Days", "lastCalendarMonth", "custom"]}
                  ]
                }
              },
              "screens": {"scr1": {"name": "Log Screen"}, "scr2": {"name": "View Screen"}},
              "slices": [
                {
                  "id": "log-time-entry-slice", "name": "Log Time Entry", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr1", "commandId": "log-time-entry", "eventIds": ["time-entry-logged"],
                  "scenarios": [{"id":"log-scenario","name":"Log","kind":"stateChange","given":[],"when":{"commandId":"log-time-entry"},"then":{"events":[{"eventId":"time-entry-logged"}]}}]
                },
                {
                  "id": "view-time-entries-slice", "name": "View Time Entries", "pattern": "stateView",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr2", "readModelId": "time-entries",
                  "scenarios": [{
                    "id": "view-within-pinned-last7days-window", "name": "Only entries inside the asOf-pinned last7Days window are returned", "kind": "stateView",
                    "given": [
                      {"eventId": "time-entry-logged", "data": {"entryId": "e1", "taskDate": "2026-08-30", "hours": 4}},
                      {"eventId": "time-entry-logged", "data": {"entryId": "e2", "taskDate": "2026-09-01", "hours": 3}},
                      {"eventId": "time-entry-logged", "data": {"entryId": "e3", "taskDate": "2026-09-06", "hours": 2}}
                    ],
                    "when": {"readModelId": "time-entries", "asOf": "2026-09-06", "queryParams": {"dateRange": {"kind": "last7Days"}}},
                    "then": {"result": {"entries": [
                      {"entryId": "e2", "taskDate": "2026-09-01", "hours": 3},
                      {"entryId": "e3", "taskDate": "2026-09-06", "hours": 2}
                    ]}}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var mapped = DocumentMapper.Map(doc);

        var results = await ScenarioVerifier.VerifyAsync(doc, mapped, DotnetCqrsProjectPath());

        var view = results.Single(r => r.ScenarioId == "view-within-pinned-last7days-window");
        Assert.True(view.Passed, view.Detail);
    }
}
