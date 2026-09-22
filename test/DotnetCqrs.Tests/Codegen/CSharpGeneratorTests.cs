using System.Diagnostics;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves the generator's output is real, compilable C# against the actual
/// <c>DotnetCqrs</c> library -- not just "no exception while building the string". A
/// generator's only real deliverable is text that compiles (and, for the wiring it
/// claims to get right, runs correctly), so this shells out to a real
/// <c>dotnet build</c>/<c>dotnet run</c> against a scratch project rather than
/// asserting anything about the generated text itself.
/// </summary>
public class CSharpGeneratorTests : IDisposable
{
    private readonly string _scratchDir;

    public CSharpGeneratorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-codegen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
            Directory.Delete(_scratchDir, recursive: true);
    }

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    /// <summary>Walks up from the test's own output directory to find the repo root
    /// (marked by dotnetcqrs.slnx), rather than a fixed number of parent hops that
    /// would silently break if the build output path ever changes shape.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private async Task<(bool Success, string Output)> BuildAsync(IEnumerable<GeneratedFile> files, string? programCs = null)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <OutputType>{(programCs is null ? "Library" : "Exe")}</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj")}" />
                <ProjectReference Include="{Path.Combine(RepoRoot(), "src", "DotnetCqrs.Crypto", "DotnetCqrs.Crypto.csproj")}" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Scratch.csproj"), csproj);

        foreach (var file in files)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, file.Name), file.Source);

        if (programCs is not null)
            await File.WriteAllTextAsync(Path.Combine(_scratchDir, "Program.cs"), programCs);

        var arguments = programCs is null ? $"build \"{_scratchDir}\" -v quiet" : $"run --project \"{_scratchDir}\"";
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, stdout + stderr);
    }

    [Fact]
    public async Task Generated_code_for_the_full_order_fulfillment_domain_compiles()
    {
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var result = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });

        var files = result.Domains.SelectMany(CSharpGenerator.Generate).ToList();
        Assert.NotEmpty(files); // sanity: the generator actually produced something

        var (success, output) = await BuildAsync(files);
        Assert.True(success, $"generated code did not compile:\n{output}");
    }

    [Fact]
    public async Task Generated_decider_decides_and_evolves_correctly_at_runtime()
    {
        // A minimal document with an explicit create scenario (empty `given`) so
        // IsCreate marks PlaceOrder a create (Once=true, RequiresExisting=false) --
        // minimal.json itself declares no scenarios at all, so its PlaceOrder is
        // faithfully mapped as requiring an EXISTING stream instead (see
        // DocumentMapperTests' own note on this same distinction). No fields on
        // either the command or the event, so this proves the WIRING (registration,
        // command dispatch, the create-guard, event append, stream contents) in
        // isolation from field-shape concerns, which are a document-content question,
        // not a generator correctness one.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.0.0", "id": "runtime-test", "name": "Runtime Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s"}},
              "commands": {"place-order": {"name": "Place Order"}},
              "screens": {"scr": {"name": "Screen"}},
              "slices": [{
                "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
                "swimlaneId": "s", "status": "created",
                "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
                "scenarios": [{
                  "id": "create-scenario", "name": "Creates the order", "kind": "stateChange",
                  "given": [], "when": {"commandId": "place-order"}, "then": {"events": [{"eventId": "order-placed"}]}
                }]
              }]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var result = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["place-order"] = "Order" },
        });
        var domain = Assert.Single(result.Domains);
        var files = CSharpGenerator.Generate(domain);

        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Order;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(OrderDecider.Aggregate, OrderDecider.Create());

            await registry.HandleAsync("order", "o1", new Command("PlaceOrder", "{}"));
            var stream = await store.LoadStreamAsync("order", "o1");

            if (stream.Count != 1 || stream[0].Type != OrderEvents.OrderPlaced)
            {
                Console.WriteLine($"FAIL: stream = [{string.Join(",", stream.Select(e => e.Type))}]");
                return 1;
            }

            try
            {
                // the create-guard should also be real, not just present in source
                await registry.HandleAsync("order", "o1", new Command("PlaceOrder", "{}"));
                Console.WriteLine("FAIL: expected rejection of a duplicate PlaceOrder");
                return 1;
            }
            catch (InvalidOperationException)
            {
                // expected
            }

            Console.WriteLine("PASS");
            return 0;
            """;

        var (success, output) = await BuildAsync(files, programCs);
        Assert.True(success, $"generated decider did not build/run correctly:\n{output}");
        Assert.Contains("PASS", output);
    }

    [Fact]
    public async Task A_field_pii_value_is_encrypted_before_append_and_never_stored_in_plaintext()
    {
        // The real order-fulfillment document (schema 3.0.0: customerEmail is pii with
        // piiSubject customerId), generated for real, built for real, run for real over
        // the actual DeciderRegistry with the generated PiiProtector -- against
        // InMemoryKmsClient, since no facade is reachable from a test. Proves the whole
        // write path end to end: Decide sees the plaintext, the protector encrypts under
        // the SUBJECT's id (cust-1, not the stream id o1), the store holds only the
        // envelope, and Evolve folds the envelope back into state without ever needing a
        // reveal (ShipOrder only checks Exists).
        var doc = DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var result = DocumentMapper.Map(doc, new MappingOptions
        {
            AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
        });
        var order = result.Domains.Single(d => d.Aggregate == "order");
        var files = CSharpGenerator.Generate(order);

        const string programCs = """"
            using DotnetCqrs.Crypto;
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.Order;

            var kms = new InMemoryKmsClient();
            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(OrderDecider.Aggregate, OrderDecider.Create(), new OrderDecider.PiiProtector(kms));

            await registry.HandleAsync("order", "o1", new Command("PlaceOrder",
                """{"orderId":"o1","customerId":"cust-1","customerEmail":"ada@example.com","items":[]}"""));
            var stored = (await store.LoadStreamAsync("order", "o1")).Single().Data;

            if (stored.Contains("ada@example.com")) { Console.WriteLine($"FAIL: plaintext in store: {stored}"); return 1; }
            if (!stored.Contains("\"$pii\"")) { Console.WriteLine($"FAIL: no envelope in store: {stored}"); return 1; }
            if (!stored.Contains("\"s\":\"cust-1\"")) { Console.WriteLine($"FAIL: wrong subject in store: {stored}"); return 1; }
            if (!stored.Contains("\"customerId\":\"cust-1\"")) { Console.WriteLine($"FAIL: subject field missing: {stored}"); return 1; }
            if (kms.EncryptCalls != 1) { Console.WriteLine($"FAIL: expected 1 encrypt call, got {kms.EncryptCalls}"); return 1; }

            // Evolve folds the stored envelope into state (Pending) and the next decision,
            // which never reads the email, must cost no reveal at all.
            await registry.HandleAsync("order", "o1", new Command("ShipOrder", "{}"));
            if (kms.DecryptBatchCalls != 0) { Console.WriteLine($"FAIL: ShipOrder should not reveal, got {kms.DecryptBatchCalls}"); return 1; }

            // Fail closed: the same aggregate registered without its protector must refuse
            // to append plaintext, and the store must be untouched.
            var bare = new DeciderRegistry(store);
            bare.Register(OrderDecider.Aggregate, OrderDecider.Create());
            try
            {
                await bare.HandleAsync("order", "o2", new Command("PlaceOrder",
                    """{"orderId":"o2","customerId":"cust-2","customerEmail":"grace@example.com","items":[]}"""));
                Console.WriteLine("FAIL: expected fail-closed without a protector");
                return 1;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("unencrypted"))
            {
                if ((await store.LoadStreamAsync("order", "o2")).Count != 0) { Console.WriteLine("FAIL: o2 was appended"); return 1; }
            }

            Console.WriteLine("PASS");
            return 0;
            """";

        var (success, output) = await BuildAsync(files, programCs);
        Assert.True(success, $"generated PII decider did not build/run correctly:\n{output}");
        Assert.Contains("PASS", output);
    }

    [Fact]
    public async Task An_endsStream_event_lets_a_removed_link_be_reassigned()
    {
        // Finding 3's Class 3 (findings.md §4): before this fix, Evolve only ever set
        // Exists = true, so the Once guard blocked a re-assign forever after an
        // unassign. staff-unassigned-from-project is endsStream: true here, so Evolve
        // must reset Exists to false on it, and assign-staff-to-project (Once=true,
        // per its "reassign" scenario nets to create evidence -- see
        // DocumentMapperTests) must succeed a second time after the unassign.
        const string json = """
            {
              "eventModelingSchemaVersion": "2.2.0", "id": "reassign-runtime-test", "name": "Reassign Runtime Test",
              "swimlanes": [{"id":"s","name":"S","kind":"team"}],
              "events": {
                "staff-assigned-to-project": {"name": "Staff Assigned To Project", "swimlaneId": "s", "aggregate": "ProjectStaffAssignment"},
                "staff-unassigned-from-project": {"name": "Staff Unassigned From Project", "swimlaneId": "s", "aggregate": "ProjectStaffAssignment", "endsStream": true}
              },
              "commands": {
                "assign-staff-to-project": {"name": "Assign Staff To Project", "aggregate": "ProjectStaffAssignment"},
                "unassign-staff-from-project": {"name": "Unassign Staff From Project", "aggregate": "ProjectStaffAssignment"}
              },
              "screens": {"scr": {"name": "Screen"}},
              "slices": [
                {
                  "id": "assign-slice", "name": "Assign", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "assign-staff-to-project", "eventIds": ["staff-assigned-to-project"],
                  "scenarios": [
                    {
                      "id": "create-scenario", "name": "First assignment", "kind": "stateChange",
                      "given": [], "when": {"commandId": "assign-staff-to-project"},
                      "then": {"events": [{"eventId": "staff-assigned-to-project"}]}
                    },
                    {
                      "id": "reassign-scenario", "name": "Reassign after unassign", "kind": "stateChange",
                      "given": [{"eventId": "staff-assigned-to-project"}, {"eventId": "staff-unassigned-from-project"}],
                      "when": {"commandId": "assign-staff-to-project"},
                      "then": {"events": [{"eventId": "staff-assigned-to-project"}]}
                    }
                  ]
                },
                {
                  "id": "unassign-slice", "name": "Unassign", "pattern": "stateChange",
                  "swimlaneId": "s", "status": "created",
                  "screenId": "scr", "commandId": "unassign-staff-from-project", "eventIds": ["staff-unassigned-from-project"],
                  "scenarios": [{
                    "id": "unassign-scenario", "name": "Unassign an existing pair", "kind": "stateChange",
                    "given": [{"eventId": "staff-assigned-to-project"}], "when": {"commandId": "unassign-staff-from-project"},
                    "then": {"events": [{"eventId": "staff-unassigned-from-project"}]}
                  }]
                }
              ]
            }
            """;
        var doc = DocumentLoader.Parse(json);
        var result = DocumentMapper.Map(doc);
        var domain = Assert.Single(result.Domains);

        // Confirms the mapping side too, not just the generated runtime behavior below.
        var assign = domain.Commands.Single(c => c.Name == "AssignStaffToProject");
        Assert.True(assign.Once);
        Assert.False(assign.RequiresExisting);

        var files = CSharpGenerator.Generate(domain);

        const string programCs = """
            using DotnetCqrs.Deciders;
            using DotnetCqrs.EventStore;
            using Generated.ProjectStaffAssignment;

            var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            registry.Register(ProjectStaffAssignmentDecider.Aggregate, ProjectStaffAssignmentDecider.Create());

            await registry.HandleAsync("projectStaffAssignment", "a1", new Command("AssignStaffToProject", "{}"));
            await registry.HandleAsync("projectStaffAssignment", "a1", new Command("UnassignStaffFromProject", "{}"));

            // Before the endsStream fix, Evolve never reset Exists, so this next line
            // would throw "already exists" -- the reassignment the Once guard should
            // allow after a real removal.
            await registry.HandleAsync("projectStaffAssignment", "a1", new Command("AssignStaffToProject", "{}"));

            var stream = await store.LoadStreamAsync("projectStaffAssignment", "a1");
            var types = string.Join(",", stream.Select(e => e.Type));
            if (types != "StaffAssignedToProject,StaffUnassignedFromProject,StaffAssignedToProject")
            {
                Console.WriteLine($"FAIL: stream = [{types}]");
                return 1;
            }

            Console.WriteLine("PASS");
            return 0;
            """;

        var (success, output) = await BuildAsync(files, programCs);
        Assert.True(success, $"generated decider did not build/run correctly:\n{output}");
        Assert.Contains("PASS", output);
    }
}
