using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;
using DotnetCqrs.Tests.Crypto;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves Milestone 9's `generate --host` for real: the generated project actually
/// builds, its own baked-in `--verify` mode reports the document's real scenario
/// results (the same PASS/FAIL/exit-code shape CliTests already proves for the CLI's
/// own `verify` command), and a real HTTP request through the generated
/// `MapCqrsGateway()` actually dispatches a command end to end -- not just that the
/// process starts without throwing.
/// </summary>
public class HostGenerationTests : IDisposable
{
    private readonly string _scratchDir;

    public HostGenerationTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-codegen-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_scratchDir)) return;

        // Unlike CliTests (a single `dotnet build`), this test kills a live host
        // process right before cleanup -- Windows can hold its output DLLs locked for
        // a brief moment after Kill() returns, so a bare Directory.Delete flakes here.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(_scratchDir, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    /// <summary>Walks up from the test's own output directory to find the repo root
    /// (marked by dotnetcqrs.slnx) -- same approach as CliTests.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "dotnetcqrs.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (dotnetcqrs.slnx) from {AppContext.BaseDirectory}");
    }

    private static string CliProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs.Codegen.Cli", "DotnetCqrs.Codegen.Cli.csproj");
    private static string DotnetCqrsProjectPath() => Path.Combine(RepoRoot(), "src", "DotnetCqrs", "DotnetCqrs.csproj");

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>Binds then immediately releases a loopback port -- the ordinary
    /// (small-race) way to pick a free port for a child process to listen on.</summary>
    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(Timeout = 300000)]
    public async Task Generate_host_builds_self_verifies_and_dispatches_a_real_command_over_http()
    {
        var (genExit, genOutput) = await RunAsync("dotnet",
        [
            "run", "--project", CliProjectPath(), "--",
            "generate",
            "--input", TestDataPath("order-fulfillment.json"),
            "--output", _scratchDir,
            "--host",
            "--dotnetcqrs-project", DotnetCqrsProjectPath(),
            "--aggregate-override", "notify-shipping-partner=ShippingNotification",
        ]);
        Assert.True(genExit == 0, $"generate --host exited {genExit}:\n{genOutput}");
        Assert.Contains("OrderFulfillment.csproj", Directory.GetFiles(_scratchDir).Select(Path.GetFileName));
        Assert.Contains("document.json", Directory.GetFiles(_scratchDir).Select(Path.GetFileName));

        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        Assert.True(buildExit == 0, $"generated host project did not compile:\n{buildOutput}");

        // The baked-in self-test: `--verify` re-runs the document's own scenarios
        // against the generated code. order-fulfillment.json has one known real
        // failure (view-order-summary-after-placement -- the generic field-merge
        // projection can't derive "status" from any event's literal JSON, the same
        // gap CliTests' Verify_prints_every_scenario_result... test documents for the
        // CLI's own `verify`), so a non-zero exit here IS the passing assertion.
        var (verifyExit, verifyOutput) = await RunAsync("dotnet", ["run", "--project", _scratchDir, "--no-build", "--", "--verify"]);
        Assert.NotEqual(0, verifyExit);
        Assert.Contains("PASS [stateChange] place-order-slice/place-order-happy-path", verifyOutput);
        Assert.Contains("FAIL [stateView] order-status-slice/view-order-summary-after-placement", verifyOutput);

        // order-fulfillment.json has a field.pii value (order-placed.customerEmail), so the
        // host must refuse to start with no key service rather than fail on the first
        // request that touches PII.
        var noKms = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--project", _scratchDir, "--no-build" }) noKms.ArgumentList.Add(a);
        noKms.Environment.Remove("KMS_FACADE_URL");
        using (var refused = Process.Start(noKms)!)
        {
            var refusedErr = await refused.StandardError.ReadToEndAsync();
            Assert.True(refused.WaitForExit(60000), "a pii host with no KMS_FACADE_URL should exit, not serve");
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("KMS_FACADE_URL", refusedErr);
        }
        await using var facade = await StandInKmsFacade.StartAsync();

        // Real HTTP round trip: run the generated host for real and dispatch a command
        // through its MapCqrsGateway(), the same proof CqrsGatewayEndpointsTests uses
        // for the hand-written host -- not just that the process starts without
        // throwing.
        var port = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_scratchDir);
        psi.ArgumentList.Add("--no-build");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["KMS_FACADE_URL"] = facade.BaseUrl;

        using var process = Process.Start(psi)!;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpResponseMessage? response = null;
            for (var attempt = 0; attempt < 60 && response is null; attempt++)
            {
                await Task.Delay(500);
                try
                {
                    response = await client.PostAsJsonAsync("/api/cqrs/order/o1/PlaceOrder",
                        new { customerId = "11111111-1111-1111-1111-111111111111", items = new[] { new { sku = "widget", qty = 2 } } });
                }
                catch (HttpRequestException)
                {
                    // the host isn't listening yet -- retry
                }
            }

            Assert.False(process.HasExited, $"generated host process exited early (code {(process.HasExited ? process.ExitCode : (int?)null)})");
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
            var events = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, events.GetArrayLength());
            Assert.Equal("OrderPlaced", events[0].GetProperty("type").GetString());

            // auto-ship-pending-orders is a same-aggregate reactor (order-placed ->
            // ship-order, both "order"): it must dispatch back into the SAME order's
            // stream, not a derived one nothing created (the Milestone 5 finding this
            // regression-tests). ConsumerEngine polls every ~1s, so poll the real
            // events.db -- written by the live host process, opened here as a second
            // WAL reader -- for OrderShipped to land on stream ("order", "o1").
            var eventsDbPath = Directory.GetFiles(_scratchDir, "events.db", SearchOption.AllDirectories).Single();
            var shipped = false;
            for (var attempt = 0; attempt < 30 && !shipped; attempt++)
            {
                await Task.Delay(500);
                await using var store = await SqliteEventStore.OpenAsync(eventsDbPath);
                var stream = await store.LoadStreamAsync("order", "o1");
                shipped = stream.Any(e => e.Type == "OrderShipped");
            }
            Assert.True(shipped, "auto-ship reactor never dispatched OrderShipped onto the triggering order's own stream");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    // Milestone D4: a document whose only aggregate stores PII, and whose read model
    // shows it.
    private const string PiiHostJson = """
        {
          "eventModelingSchemaVersion": "3.1.0", "id": "pii-host-test", "name": "Pii Host Test",
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
              ],
              "filters": [
                {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"},
                {"param": "emailExact", "field": "email", "kind": "match", "mode": "exact", "normalize": "email"}
              ]
            }
          },
          "screens": {"scr1": {"name": "Register Screen"}},
          "slices": [
            {
              "id": "register-customer-slice", "name": "Register Customer", "pattern": "stateChange",
              "swimlaneId": "s", "status": "created",
              "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"],
              "scenarios": [{"id":"register","name":"Register","kind":"stateChange","given":[],
                "when":{"commandId":"register-customer"},"then":{"events":[{"eventId":"customer-registered"}]}}]
            }
          ]
        }
        """;

    [Fact(Timeout = 300000)]
    public async Task A_generated_pii_host_encrypts_on_write_reveals_on_read_and_redacts_after_erasure()
    {
        var inputPath = Path.Combine(_scratchDir, "..", $"pii-host-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, PiiHostJson);
        try
        {
            var (genExit, genOutput) = await RunAsync("dotnet",
            [
                "run", "--project", CliProjectPath(), "--",
                "generate", "--input", inputPath, "--output", _scratchDir, "--host",
                "--dotnetcqrs-project", DotnetCqrsProjectPath(),
            ]);
            Assert.True(genExit == 0, $"generate --host exited {genExit}:\n{genOutput}");
        }
        finally
        {
            File.Delete(inputPath);
        }

        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        Assert.True(buildExit == 0, $"generated host project did not compile:\n{buildOutput}");

        await using var facade = await StandInKmsFacade.StartAsync();
        var port = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--project", _scratchDir, "--no-build" }) psi.ArgumentList.Add(a);
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["KMS_FACADE_URL"] = facade.BaseUrl;
        psi.Environment["KMS_INDEX_KEY"] = "pii-host-test";

        // Reassigned by the restart below; local functions capture the variables.
        var process = Process.Start(psi)!;
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        try
        {

            // Write: the command carries plaintext; the protector encrypts before append.
            HttpResponseMessage? registered = null;
            for (var attempt = 0; attempt < 60 && registered is null; attempt++)
            {
                await Task.Delay(500);
                try
                {
                    registered = await client.PostAsJsonAsync("/api/cqrs/customer/c1/RegisterCustomer",
                        new { customerId = "c1", email = "alice@example.com" });
                }
                catch (HttpRequestException) { /* not listening yet -- retry */ }
            }
            Assert.False(process.HasExited, $"generated host exited early (code {(process.HasExited ? process.ExitCode : (int?)null)})");
            Assert.NotNull(registered);
            Assert.True(registered!.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());

            var eventsDbPath = Directory.GetFiles(_scratchDir, "events.db", SearchOption.AllDirectories).Single();
            await using (var store = await SqliteEventStore.OpenAsync(eventsDbPath))
            {
                var data = (await store.LoadStreamAsync("customer", "c1")).Single().Data;
                Assert.DoesNotContain("alice@example.com", data);
                Assert.Contains("\"$pii\"", data);
            }

            async Task<JsonElement> PollEmailAsync(Func<JsonElement, bool> until)
            {
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var rows = await client.GetFromJsonAsync<JsonElement>("/api/query/customers");
                    var row = rows.EnumerateArray().FirstOrDefault(r => r.GetProperty("customer_id").GetString() == "c1");
                    if (row.ValueKind == JsonValueKind.Object && until(row.GetProperty("email"))) return row.GetProperty("email");
                    await Task.Delay(500);
                }
                throw new TimeoutException("the customers read model never reached the expected state");
            }

            // Read: the projection stored the envelope; the route reveals it through DI's KmsClient.
            var email = await PollEmailAsync(e => e.ValueKind == JsonValueKind.String);
            Assert.Equal("alice@example.com", email.GetString());

            // Search: the pii contains filter is served from search.db, which the generated host
            // opens, attaches and fills through the consumer engine.
            async Task<int> SearchHitsAsync(string term) =>
                (await client.GetFromJsonAsync<JsonElement>($"/api/query/customers?emailSearch={term}")).GetArrayLength();
            var found = 0;
            for (var attempt = 0; attempt < 40 && found == 0; attempt++)
            {
                found = await SearchHitsAsync("ALICE");
                if (found == 0) await Task.Delay(500);
            }
            Assert.Equal(1, found);
            var searchDbPath = Path.Combine(Path.GetDirectoryName(eventsDbPath)!, "search.db");
            Assert.True(File.Exists(searchDbPath), "search.db should sit beside events.db");

            // Milestone D6: the pii exact filter is a keyed-hash index in the same search.db, under
            // the index key the host ensured at startup.
            Assert.Equal(1, facade.Handler.IndexKeyVersion("pii-host-test"));
            async Task<int> ExactHitsAsync(string term) =>
                (await client.GetFromJsonAsync<JsonElement>($"/api/query/customers?emailExact={Uri.EscapeDataString(term)}")).GetArrayLength();
            async Task<int> PollExactAsync(string term, int want)
            {
                var hits = -1;
                for (var attempt = 0; attempt < 40 && hits != want; attempt++)
                {
                    hits = await ExactHitsAsync(term);
                    if (hits != want) await Task.Delay(500);
                }
                return hits;
            }
            Assert.Equal(1, await PollExactAsync(" ALICE@example.com", 1));
            Assert.Equal(0, await ExactHitsAsync("alice"));

            // Rotation: ops rotates the index key, and the host is restarted on the same data.
            // Startup sees version 2, rebuilds while searches stay on version 1, then switches.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            process.Dispose();
            facade.Handler.RotateIndexKey("pii-host-test");
            port = FreeTcpPort();
            psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            process = Process.Start(psi)!;
            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(500);
                try { (await client.GetAsync("/api/query/customers")).EnsureSuccessStatusCode(); break; }
                catch (HttpRequestException) { /* not listening yet -- retry */ }
            }
            Assert.False(process.HasExited, "restarted host exited early");
            int? indexVersion = null;
            for (var attempt = 0; attempt < 40 && indexVersion != 2; attempt++)
            {
                await using (var search = await SqliteSearchIndexStore.OpenAsync(searchDbPath))
                    indexVersion = await search.IndexVersionAsync("customers:hashed");
                if (indexVersion != 2) await Task.Delay(500);
            }
            Assert.Equal(2, indexVersion);
            Assert.Equal(1, await ExactHitsAsync("alice@example.com"));
            Assert.Equal(2, facade.Handler.HmacKeyVersions[^1]); // the search now hashes at version 2
            Assert.DoesNotContain(null, facade.Handler.HmacKeyVersions); // every call pinned a version

            // Erase through the gateway: the built-in data-subject aggregate is registered.
            using var erased = await client.PostAsJsonAsync("/api/cqrs/dataSubject/c1/EraseSubject", new { });
            Assert.True(erased.IsSuccessStatusCode, await erased.Content.ReadAsStringAsync());

            // The value was cached by the read above, so this only turns into the marker if
            // the evictor is wired (and the destroyer, for anything not cached).
            var redacted = await PollEmailAsync(e => e.ValueKind == JsonValueKind.Object);
            Assert.True(redacted.GetProperty("$redacted").GetBoolean());

            // ...and the search index forgot the subject: "no match" is the right answer now.
            var remaining = 1;
            for (var attempt = 0; attempt < 40 && remaining > 0; attempt++)
            {
                remaining = await SearchHitsAsync("alice");
                if (remaining > 0) await Task.Delay(500);
            }
            Assert.Equal(0, remaining);
            Assert.Equal(0, await PollExactAsync("alice@example.com", 0)); // hashed rows deleted too

            // A returning person is a new subject: the guard refuses the old id.
            using var again = await client.PostAsJsonAsync("/api/cqrs/customer/c1b/RegisterCustomer",
                new { customerId = "c1", email = "alice@example.com" });
            Assert.False(again.IsSuccessStatusCode, "PII for an erased subject id must be refused");
        }
        finally
        {
            client.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            process.Dispose();
        }
    }
}
