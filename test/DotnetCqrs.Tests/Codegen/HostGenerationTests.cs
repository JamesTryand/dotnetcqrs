using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using DotnetCqrs.ReadModels;
using DotnetCqrs.Tests.Crypto;
using DotnetCqrs.Tests.Postgres;
using Npgsql;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Proves Milestone 9's `generate --host` for real: the generated project actually
/// builds, its own baked-in `--verify` mode reports the document's real scenario
/// results (the same PASS/FAIL/exit-code shape CliTests already proves for the CLI's
/// own `verify` command), and a real HTTP request through the generated
/// `MapCqrsGateway()` actually dispatches a command end to end -- not just that the
/// process starts without throwing.
///
/// <para>Each test runs twice: on the SQLite files the host uses by default, and with
/// <c>DOTNETCQRS_POSTGRES</c> pointing it at a fresh Postgres database (skipped unless
/// <c>DOTNETCQRS_PG</c> is set). The generated code is identical; only the stores differ.</para>
/// </summary>
[Collection(CompilesCollection.Name)]
[Trait("Category", "Slow")]
public class HostGenerationTests : IDisposable, IClassFixture<PostgresFixture>, IClassFixture<CliUnderTestFixture>
{
    private readonly string _scratchDir;
    private readonly PostgresFixture _pg;

    public HostGenerationTests(PostgresFixture pg)
    {
        _pg = pg;
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

    /// <summary>The host's event store: its <c>events.db</c>, opened as a second WAL reader, or
    /// the Postgres database it was pointed at.</summary>
    private async Task<IEventStore> OpenEventStoreAsync(string? postgres) => postgres is not null
        ? await PostgresEventStore.OpenAsync(postgres)
        : await SqliteEventStore.OpenAsync(Directory.GetFiles(_scratchDir, "events.db", SearchOption.AllDirectories).Single());

    /// <summary>The host's search index store: <c>search.db</c> beside <c>events.db</c>, or the
    /// <c>search</c> schema of its Postgres database.</summary>
    private async Task<ISearchIndexStore> OpenSearchStoreAsync(string? postgres) => postgres is not null
        ? await PostgresSearchIndexStore.OpenAsync(postgres)
        : await SqliteSearchIndexStore.OpenAsync(Path.Combine(
            Path.GetDirectoryName(Directory.GetFiles(_scratchDir, "events.db", SearchOption.AllDirectories).Single())!, "search.db"));

    private static async Task<object?> PgScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Starts a generated host with its output drained into <paramref name="log"/>: an
    /// unread redirected pipe can fill and block the host, and on failure the log says why.</summary>
    private static Process StartHost(ProcessStartInfo psi, System.Text.StringBuilder log)
    {
        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string Tail(System.Text.StringBuilder log)
    {
        lock (log) return log.Length <= 6000 ? log.ToString() : log.ToString(log.Length - 6000, 6000);
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

    [Fact]
    public void The_generated_host_logs_consumer_failures_and_dropped_reactions_instead_of_discarding_them()
    {
        // A consumer that fails blocks at that event and retries forever; with the engine's
        // default no-op logger a stuck projection or reactor is invisible. The generated host
        // must hand the engine (and every ReactorConsumer) a real sink.
        var overrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" };
        var doc = DotnetCqrs.Codegen.DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DotnetCqrs.Codegen.Mapping.DocumentMapper.Map(doc,
            new DotnetCqrs.Codegen.Mapping.MappingOptions { AggregateOverrides = overrides });
        var program = DotnetCqrs.Codegen.Generation.HostProjectGenerator
            .Generate(doc, mapped, "OrderFulfillment", DotnetCqrsProjectPath(), overrides)
            .Single(f => f.Name == "Program.cs").Source;

        Assert.Contains("new ConsumerEngine(eventStore, consumerState, logger: Console.Error.WriteLine)", program);
        Assert.DoesNotContain("new ConsumerEngine(eventStore, consumerState);", program);
        Assert.Contains("new ReactorConsumer(", program);
        Assert.DoesNotContain("(), registry));", program); // every ReactorConsumer gets a logger too
    }

    [Fact]
    public void The_generated_pii_host_refuses_erasure_unless_an_authorize_policy_is_wired()
    {
        // order-fulfillment.json has a field.pii value, so its host registers the dataSubject aggregate.
        var overrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" };
        var doc = DotnetCqrs.Codegen.DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DotnetCqrs.Codegen.Mapping.DocumentMapper.Map(doc,
            new DotnetCqrs.Codegen.Mapping.MappingOptions { AggregateOverrides = overrides });
        var program = DotnetCqrs.Codegen.Generation.HostProjectGenerator
            .Generate(doc, mapped, "OrderFulfillment", DotnetCqrsProjectPath(), overrides)
            .Single(f => f.Name == "Program.cs").Source;

        // Closed by default: no authorize unless the throwaway switch is set, and the switch is loud.
        Assert.Contains("app.MapCqrsGateway(authorize: allowUnauthorizedErasure ? (_, _, _, _, _, _) => Task.FromResult(true) : null);", program);
        Assert.Contains("DOTNETCQRS_ALLOW_UNAUTHORIZED_ERASURE", program);
        Assert.Contains("WARNING: DOTNETCQRS_ALLOW_UNAUTHORIZED_ERASURE=1", program);
        Assert.DoesNotContain("app.MapCqrsGateway();", program.Replace("app.MapCqrsGateway(forward", ""));
    }

    [Fact]
    public void The_generated_host_resolves_its_node_identity_before_opening_anything_and_registers_it()
    {
        var overrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" };
        var doc = DotnetCqrs.Codegen.DocumentLoader.LoadFromFile(TestDataPath("order-fulfillment.json"));
        var mapped = DotnetCqrs.Codegen.Mapping.DocumentMapper.Map(doc,
            new DotnetCqrs.Codegen.Mapping.MappingOptions { AggregateOverrides = overrides });
        var program = DotnetCqrs.Codegen.Generation.HostProjectGenerator
            .Generate(doc, mapped, "OrderFulfillment", DotnetCqrsProjectPath(), overrides)
            .Single(f => f.Name == "Program.cs").Source;

        var resolve = program.IndexOf("NodeIdentity.FromEnvironment(\"OrderFulfillment\", role", StringComparison.Ordinal);
        Assert.True(resolve >= 0, program);
        Assert.True(resolve < program.IndexOf("SqliteEventStore.OpenAsync", StringComparison.Ordinal));
        Assert.Contains("catch (InvalidIdentitySettingException ex)", program);
        Assert.Contains("builder.Services.AddSingleton(nodeIdentity);", program);

        // Health/telemetry contract section 2: the ops port binds before configuration is read,
        // identity is resolved, or anything opens; then it is told the identity.
        var ops = program.IndexOf("await OpsServer.StartAsync(health)", StringComparison.Ordinal);
        Assert.True(ops >= 0, program);
        Assert.True(ops < program.IndexOf("DOTNETCQRS_POSTGRES", StringComparison.Ordinal));
        Assert.True(ops < resolve);
        Assert.True(program.IndexOf("health.SetIdentity(nodeIdentity);", StringComparison.Ordinal) > resolve);
        Assert.Contains("builder.Services.AddSingleton(health);", program);
    }

    [Fact(Timeout = 300000)]
    public Task Generate_host_builds_self_verifies_and_dispatches_a_real_command_over_http()
        => RunOrderFulfillmentHostAsync(postgres: null);

    [SkippableFact(Timeout = 600000)]
    public async Task Generate_host_builds_self_verifies_and_dispatches_a_real_command_over_http_on_postgres()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await RunOrderFulfillmentHostAsync(await _pg.NewDatabaseAsync());
    }

    private async Task RunOrderFulfillmentHostAsync(string? postgres)
    {
        var (genExit, genOutput) = await RunAsync("dotnet",
        [
            await CliUnderTest.DllAsync(),
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
        noKms.Environment["CQRS_OPS_PORT"] = "0";
        noKms.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        using (var refused = Process.Start(noKms)!)
        {
            var refusedErr = await refused.StandardError.ReadToEndAsync();
            Assert.True(refused.WaitForExit(60000), "a pii host with no KMS_FACADE_URL should exit, not serve");
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("KMS_FACADE_URL", refusedErr);
        }
        await using var facade = await StandInKmsFacade.StartAsync();

        // Node identity: an invalid CQRS_NODE_ID is bad configuration, so the host refuses to
        // start rather than run under an id that would break the NATS subject.
        var badId = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "--project", _scratchDir, "--no-build" }) badId.ArgumentList.Add(a);
        badId.Environment["KMS_FACADE_URL"] = facade.BaseUrl;
        badId.Environment["CQRS_OPS_PORT"] = "0";
        badId.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        badId.Environment["CQRS_NODE_ID"] = "not.a.valid.id";
        using (var refused = Process.Start(badId)!)
        {
            var refusedErr = await refused.StandardError.ReadToEndAsync();
            Assert.True(refused.WaitForExit(60000), "a host with an invalid CQRS_NODE_ID should exit, not serve");
            Assert.Equal(1, refused.ExitCode);
            Assert.Contains("CQRS_NODE_ID", refusedErr);
        }
        var stateDir = Path.Combine(_scratchDir, "node-state");

        // Real HTTP round trip: run the generated host for real and dispatch a command
        // through its MapCqrsGateway(), the same proof CqrsGatewayEndpointsTests uses
        // for the hand-written host -- not just that the process starts without
        // throwing.
        var port = FreeTcpPort();
        var opsPort = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_scratchDir);
        psi.ArgumentList.Add("--no-build");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["CQRS_OPS_PORT"] = opsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        psi.Environment["KMS_FACADE_URL"] = facade.BaseUrl;
        psi.Environment["DOTNETCQRS_POSTGRES"] = postgres ?? "";
        psi.Environment["CQRS_STATE_DIR"] = stateDir;
        psi.Environment.Remove("CQRS_NODE_ID");

        var hostLog = new System.Text.StringBuilder();
        using var process = StartHost(psi, hostLog);
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

            Assert.False(process.HasExited, $"generated host process exited early (code {(process.HasExited ? process.ExitCode : (int?)null)}):\n{Tail(hostLog)}");
            Assert.True(response is not null, $"generated host never answered:\n{Tail(hostLog)}");
            Assert.Equal(HttpStatusCode.OK, response!.StatusCode);
            var events = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, events.GetArrayLength());
            Assert.Equal("OrderPlaced", events[0].GetProperty("type").GetString());

            // First boot with a state directory: a new id, written there, logged once.
            var nodeId = File.ReadAllText(Path.Combine(stateDir, "node-id")).Trim();
            Assert.Contains($"node identity: node_id={nodeId} identity=persistent instance=OrderFulfillment", Tail(hostLog));
            Assert.Contains("stack=dotnetcqrs role=writer started_at=", Tail(hostLog));

            // Health/telemetry contract section 3: /healthz on the ops port, never the traffic
            // port, reports the same identity.
            using (var ops = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") })
            {
                var healthz = await ops.GetFromJsonAsync<JsonElement>("/healthz");
                Assert.Equal("alive", healthz.GetProperty("status").GetString());
                Assert.Equal(nodeId, healthz.GetProperty("node_id").GetString());
                Assert.Equal("persistent", healthz.GetProperty("identity").GetString());
                Assert.Equal("OrderFulfillment", healthz.GetProperty("instance").GetString());
                Assert.Equal("writer", healthz.GetProperty("role").GetString());
                Assert.Equal("dotnetcqrs", healthz.GetProperty("stack").GetString());

                // Section 4: /readyz opens once the projections have caught up with the one
                // command above (they poll every ~1s), with no reasons.
                JsonElement readyz = default;
                var readyzCode = HttpStatusCode.ServiceUnavailable;
                for (var attempt = 0; attempt < 30 && readyzCode != HttpStatusCode.OK; attempt++)
                {
                    using var readyzResponse = await ops.GetAsync("/readyz");
                    readyzCode = readyzResponse.StatusCode;
                    readyz = await readyzResponse.Content.ReadFromJsonAsync<JsonElement>();
                    if (readyzCode != HttpStatusCode.OK) await Task.Delay(500);
                }
                Assert.True(readyzCode == HttpStatusCode.OK, $"/readyz never opened: {readyz}\n{Tail(hostLog)}");
                Assert.Equal("ready", readyz.GetProperty("status").GetString());
                Assert.Equal(0, readyz.GetProperty("reasons").GetArrayLength());
                Assert.Equal(nodeId, readyz.GetProperty("node_id").GetString());
                // Section 4.6: this sample has personal data, so the key service is required too.
                var dependencies = readyz.GetProperty("checks").GetProperty("dependencies");
                Assert.Equal("up", dependencies.GetProperty("event_store").GetString());
                Assert.Equal("up", dependencies.GetProperty("kms").GetString());

                // Section 6: the one command above was decided here, and its event appended.
                var metrics = await ops.GetStringAsync("/metrics");
                Assert.Contains("cqrs_commands_total{status=\"accepted\"} 1\n", metrics, StringComparison.Ordinal);
                Assert.DoesNotContain("cqrs_events_appended_total 0\n", metrics, StringComparison.Ordinal);
                Assert.Contains("cqrs_readiness_status{status=\"ready\"} 1\n", metrics, StringComparison.Ordinal);
            }

            // Section 5: the writer heartbeat, beside the event log, with this node's id and ops URL.
            await using (var heartbeatStore = await OpenEventStoreAsync(postgres))
            {
                WriterHeartbeat? heartbeat = null;
                for (var attempt = 0; attempt < 20 && heartbeat is null; attempt++)
                {
                    heartbeat = await ((IHeartbeatStore)heartbeatStore).ReadHeartbeatAsync();
                    if (heartbeat is null) await Task.Delay(250);
                }
                Assert.NotNull(heartbeat);
                Assert.Equal(nodeId, heartbeat.WriterNodeId);
                // CQRS_OPS_BIND=127.0.0.1 here, so that is the address readers are told.
                Assert.Equal($"http://127.0.0.1:{opsPort}", heartbeat.WriterOpsUrl);
                Assert.True(heartbeat.Sequence >= 1);
            }
            using (var traffic = await client.GetAsync("/healthz"))
                Assert.Equal(HttpStatusCode.NotFound, traffic.StatusCode);

            // auto-ship-pending-orders is a same-aggregate reactor (order-placed ->
            // ship-order, both "order"): it must dispatch back into the SAME order's
            // stream, not a derived one nothing created (the Milestone 5 finding this
            // regression-tests). ConsumerEngine polls every ~1s, so poll the host's real
            // event store for OrderShipped to land on stream ("order", "o1").
            var shipped = false;
            for (var attempt = 0; attempt < 30 && !shipped; attempt++)
            {
                await Task.Delay(500);
                await using var store = await OpenEventStoreAsync(postgres);
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

    [Fact(Timeout = 300000)]
    public Task A_generated_reader_follows_the_writer_forwards_commands_and_reports_replication()
        => RunWriterAndReaderAsync(postgres: null);

    [SkippableFact(Timeout = 600000)]
    public async Task A_generated_reader_follows_the_writer_forwards_commands_and_reports_replication_on_postgres()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await RunWriterAndReaderAsync(await _pg.NewDatabaseAsync());
    }

    /// <summary>The single-writer/multi-reader topology from one generated host: a writer, and a
    /// reader (<c>DOTNETCQRS_ROLE=reader</c>) on the same event log (the shared <c>events.db</c> a
    /// same-host reader opens read-only, or the same Postgres database). Each runs from its own copy
    /// of the build, as separate installs would, so each has its own <c>data/</c>.</summary>
    private async Task RunWriterAndReaderAsync(string? postgres)
    {
        var (genExit, genOutput) = await RunAsync("dotnet",
        [
            await CliUnderTest.DllAsync(),
            "generate",
            "--input", TestDataPath("order-fulfillment.json"),
            "--output", _scratchDir,
            "--host",
            "--dotnetcqrs-project", DotnetCqrsProjectPath(),
            "--aggregate-override", "notify-shipping-partner=ShippingNotification",
        ]);
        Assert.True(genExit == 0, $"generate --host exited {genExit}:\n{genOutput}");
        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        var buildDir = Path.Combine(_scratchDir, "bin", "Debug", "net10.0");
        Assert.True(buildExit == 0, $"generated host project did not compile:\n{buildOutput}");
        var writerDir = Path.Combine(_scratchDir, "writer");
        var readerDir = Path.Combine(_scratchDir, "reader");
        CopyDirectory(buildDir, writerDir);
        CopyDirectory(buildDir, readerDir);

        await using var facade = await StandInKmsFacade.StartAsync();
        var sharedEvents = Path.Combine(_scratchDir, "shared", "events.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sharedEvents)!);

        ProcessStartInfo Host(string dir, int port, int opsPort, string stateDir)
        {
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(dir, "OrderFulfillment.dll"));
            psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            psi.Environment["CQRS_OPS_PORT"] = opsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
            psi.Environment["CQRS_STATE_DIR"] = stateDir;
            psi.Environment["KMS_FACADE_URL"] = facade.BaseUrl;
            psi.Environment["DOTNETCQRS_POSTGRES"] = postgres ?? "";
            psi.Environment["DOTNETCQRS_EVENTS_PATH"] = sharedEvents;
            psi.Environment["DOTNETCQRS_STALE_THRESHOLD_SECONDS"] = "2";
            psi.Environment.Remove("CQRS_NODE_ID");
            psi.Environment.Remove("DOTNETCQRS_ROLE");
            psi.Environment.Remove("DOTNETCQRS_WRITER_URL");
            return psi;
        }

        static async Task<(HttpStatusCode Code, JsonElement Body)> ReadyzAsync(HttpClient ops)
        {
            using var response = await ops.GetAsync("/readyz");
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        static async Task<JsonElement> EventuallyReadyzAsync(HttpClient ops, string what, Func<HttpStatusCode, JsonElement, bool> cond, Func<string> log)
        {
            (HttpStatusCode Code, JsonElement Body) last = default;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    last = await ReadyzAsync(ops);
                    if (cond(last.Code, last.Body)) return last.Body;
                }
                catch (HttpRequestException)
                {
                    // not listening yet
                }
                await Task.Delay(500);
            }
            Assert.Fail($"timed out waiting for {what}; last /readyz: {(int)last.Code} {last.Body}\n{log()}");
            return default;
        }

        static string Reasons(JsonElement readyz) => string.Join(",", readyz.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        static string? Dependency(JsonElement readyz, string name) =>
            readyz.GetProperty("checks").GetProperty("dependencies").TryGetProperty(name, out var d) ? d.GetString() : null;

        var (writerPort, writerOpsPort, readerPort, readerOpsPort) = (FreeTcpPort(), FreeTcpPort(), FreeTcpPort(), FreeTcpPort());
        var writerLog = new System.Text.StringBuilder();
        var readerLog = new System.Text.StringBuilder();
        using var writer = StartHost(Host(writerDir, writerPort, writerOpsPort, Path.Combine(_scratchDir, "writer-state")), writerLog);
        Process? reader = null;
        try
        {
            using var writerOps = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{writerOpsPort}") };
            await EventuallyReadyzAsync(writerOps, "the writer to be ready", (code, _) => code == HttpStatusCode.OK, () => Tail(writerLog));

            // A reader with no writer to forward to, and a bad role, refuse to start or refuse commands.
            var badRole = Host(readerDir, readerPort, readerOpsPort, Path.Combine(_scratchDir, "reader-state"));
            badRole.Environment["DOTNETCQRS_ROLE"] = "secondary";
            var (badRoleExit, badRoleOutput) = await RunToExitAsync(badRole);
            Assert.Equal(1, badRoleExit);
            Assert.Contains("DOTNETCQRS_ROLE must be writer or reader", badRoleOutput);

            var readerPsi = Host(readerDir, readerPort, readerOpsPort, Path.Combine(_scratchDir, "reader-state"));
            readerPsi.Environment["DOTNETCQRS_ROLE"] = "reader";
            readerPsi.Environment["DOTNETCQRS_WRITER_URL"] = $"http://127.0.0.1:{writerPort}";
            reader = StartHost(readerPsi, readerLog);
            using var readerOps = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{readerOpsPort}") };
            using var readerTraffic = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{readerPort}") };

            // ---- Fresh: a ready reader, the writer a dependency
            var fresh = await EventuallyReadyzAsync(readerOps, "the reader to be ready",
                (code, body) => code == HttpStatusCode.OK && Dependency(body, "writer") == "up", () => Tail(readerLog));
            Assert.Equal("reader", fresh.GetProperty("role").GetString());
            Assert.Equal("", Reasons(fresh));
            Assert.Equal("up", Dependency(fresh, "event_store"));
            Assert.Equal("up", Dependency(fresh, "kms"));
            var healthz = await readerOps.GetFromJsonAsync<JsonElement>("/healthz");
            Assert.Equal("reader", healthz.GetProperty("role").GetString());
            Assert.Contains("role=reader", Tail(readerLog));

            // ---- A command sent to the reader is forwarded, decided and counted on the writer
            using (var forwarded = await readerTraffic.PostAsJsonAsync("/api/cqrs/order/o7/PlaceOrder",
                new { customerId = "11111111-1111-1111-1111-111111111111", items = new[] { new { sku = "widget", qty = 1 } } }))
            {
                Assert.True(forwarded.StatusCode == HttpStatusCode.OK, $"forwarded command: {(int)forwarded.StatusCode} {await forwarded.Content.ReadAsStringAsync()}\n{Tail(readerLog)}");
            }
            Assert.Contains("cqrs_commands_total{status=\"accepted\"} 1\n", await writerOps.GetStringAsync("/metrics"), StringComparison.Ordinal);
            Assert.Contains("cqrs_commands_total{status=\"accepted\"} 0\n", await readerOps.GetStringAsync("/metrics"), StringComparison.Ordinal);

            // ---- ...and the reader's own read model catches up with it
            var seen = false;
            for (var attempt = 0; attempt < 40 && !seen; attempt++)
            {
                using var query = await readerTraffic.GetAsync("/api/query/orderSummary?orderId=o7");
                seen = query.IsSuccessStatusCode && (await query.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength() == 1;
                if (!seen) await Task.Delay(500);
            }
            Assert.True(seen, $"the reader's orderSummary never showed o7\n{Tail(readerLog)}");

            // ---- Side effects happen once, on the writer: its reactor shipped the order, the reader's did not run
            var shipped = false;
            for (var attempt = 0; attempt < 30 && !shipped; attempt++)
            {
                shipped = Tail(writerLog).Contains("reaction dispatched: reactor=", StringComparison.Ordinal);
                if (!shipped) await Task.Delay(500);
            }
            Assert.True(shipped, $"the writer's reactor never dispatched\n{Tail(writerLog)}");
            Assert.DoesNotContain("reaction dispatched", Tail(readerLog));

            // ---- StaleWriterDown: the writer goes away; the reader stays in the pool as degraded
            writer.Kill(entireProcessTree: true);
            writer.WaitForExit(5000);
            var down = await EventuallyReadyzAsync(readerOps, "StaleWriterDown once the writer is gone",
                (code, body) => code == HttpStatusCode.OK && body.GetProperty("status").GetString() == "degraded"
                    && Reasons(body).Contains("replication_stale", StringComparison.Ordinal) && Dependency(body, "writer") == "down",
                () => Tail(readerLog));
            Assert.Contains("dependency_unavailable", Reasons(down));

            // ---- With nobody to forward to, a command is a forward failure (502), never decided here
            using (var orphaned = await readerTraffic.PostAsJsonAsync("/api/cqrs/order/o8/PlaceOrder",
                new { customerId = "11111111-1111-1111-1111-111111111111", items = Array.Empty<object>() }))
            {
                Assert.Equal(HttpStatusCode.BadGateway, orphaned.StatusCode);
            }
        }
        finally
        {
            foreach (var p in new[] { writer, reader })
            {
                if (p is { HasExited: false })
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            reader?.Dispose();
        }
    }

    [Fact(Timeout = 300000)]
    public async Task A_generated_reader_without_a_writer_url_refuses_commands_with_503()
    {
        var (genExit, genOutput) = await RunAsync("dotnet",
        [
            await CliUnderTest.DllAsync(), "generate", "--input", TestDataPath("minimal.json"), "--output", _scratchDir,
            "--host", "--dotnetcqrs-project", DotnetCqrsProjectPath(), "--aggregate-override", "place-order=Order",
        ]);
        Assert.True(genExit == 0, $"generate --host exited {genExit}:\n{genOutput}");
        var (buildExit, buildOutput) = await RunAsync("dotnet", ["build", _scratchDir, "-v", "quiet"]);
        var buildDir = Path.Combine(_scratchDir, "bin", "Debug", "net10.0");
        Assert.True(buildExit == 0, $"generated host project did not compile:\n{buildOutput}");

        // The writer creates the log the reader opens read-only; it need not keep running.
        var events = Path.Combine(_scratchDir, "shared", "events.db");
        Directory.CreateDirectory(Path.GetDirectoryName(events)!);
        await (await SqliteEventStore.OpenAsync(events)).DisposeAsync();

        var (port, opsPort) = (FreeTcpPort(), FreeTcpPort());
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Path.Combine(buildDir, "MinimalExample.dll"));
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["CQRS_OPS_PORT"] = opsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        psi.Environment["DOTNETCQRS_ROLE"] = "reader";
        psi.Environment["DOTNETCQRS_EVENTS_PATH"] = events;
        psi.Environment["DOTNETCQRS_POSTGRES"] = "";
        psi.Environment.Remove("DOTNETCQRS_WRITER_URL");
        psi.Environment.Remove("CQRS_NODE_ID");

        // DOTNETCQRS_WRITER_URL is only for a reader, and must be a URL.
        var badUrl = new ProcessStartInfo(psi.FileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in psi.ArgumentList) badUrl.ArgumentList.Add(a);
        foreach (var (k, v) in psi.Environment) badUrl.Environment[k] = v;
        badUrl.Environment["DOTNETCQRS_WRITER_URL"] = "not a url";
        var (badUrlExit, badUrlOutput) = await RunToExitAsync(badUrl);
        Assert.Equal(1, badUrlExit);
        Assert.Contains("DOTNETCQRS_WRITER_URL", badUrlOutput);

        var log = new System.Text.StringBuilder();
        using var process = StartHost(psi, log);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            HttpResponseMessage? response = null;
            for (var attempt = 0; attempt < 60 && response is null; attempt++)
            {
                await Task.Delay(500);
                try
                {
                    response = await client.PostAsync("/api/cqrs/order/o1/PlaceOrder", JsonContent.Create(new { }));
                }
                catch (HttpRequestException)
                {
                    // not listening yet
                }
            }
            Assert.True(response is not null, $"the reader never answered:\n{Tail(log)}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response!.StatusCode);
            Assert.Contains("read-only node", await response.Content.ReadAsStringAsync());

            // Health/telemetry section 7: counted here, as unavailable.
            using var ops = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opsPort}") };
            Assert.Contains("cqrs_commands_total{status=\"unavailable\"} 1\n", await ops.GetStringAsync("/metrics"), StringComparison.Ordinal);
            // No writer has ever beaten into this log: nothing trustworthy to serve.
            using var readyz = await ops.GetAsync("/readyz");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyz.StatusCode);
            Assert.Contains("replication_unknown", await readyz.Content.ReadAsStringAsync());
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

    private static async Task<(int ExitCode, string Output)> RunToExitAsync(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the host should have refused to start, and is still running");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
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
    public Task A_generated_pii_host_encrypts_on_write_reveals_on_read_and_redacts_after_erasure()
        => RunPiiHostAsync(postgres: null);

    [SkippableFact(Timeout = 600000)]
    public async Task A_generated_pii_host_encrypts_on_write_reveals_on_read_and_redacts_after_erasure_on_postgres()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        await RunPiiHostAsync(await _pg.NewDatabaseAsync());
    }

    private async Task RunPiiHostAsync(string? postgres)
    {
        var inputPath = Path.Combine(_scratchDir, "..", $"pii-host-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, PiiHostJson);
        try
        {
            var (genExit, genOutput) = await RunAsync("dotnet",
            [
                await CliUnderTest.DllAsync(),
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
        psi.Environment["CQRS_OPS_PORT"] = "0";
        psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        psi.Environment["KMS_FACADE_URL"] = facade.BaseUrl;
        psi.Environment["KMS_INDEX_KEY"] = "pii-host-test";
        psi.Environment["DOTNETCQRS_POSTGRES"] = postgres ?? "";
        // This test erases through the gateway; a generated host refuses erasure unless authorised,
        // so the throwaway local-run switch is set explicitly (the default is covered by
        // The_generated_pii_host_refuses_erasure_unless_an_authorize_policy_is_wired).
        psi.Environment["DOTNETCQRS_ALLOW_UNAUTHORIZED_ERASURE"] = "1";

        // Reassigned by the restart below; local functions capture the variables.
        var hostLog = new System.Text.StringBuilder();
        var process = StartHost(psi, hostLog);
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
            Assert.False(process.HasExited, $"generated host exited early (code {(process.HasExited ? process.ExitCode : (int?)null)}):\n{Tail(hostLog)}");
            Assert.NotNull(registered);
            Assert.True(registered!.IsSuccessStatusCode, await registered.Content.ReadAsStringAsync());

            await using (var store = await OpenEventStoreAsync(postgres))
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

            // Search: the pii contains filter is served from the search store (search.db, attached;
            // or the Postgres search schema), which the host fills through the consumer engine.
            async Task<int> SearchHitsAsync(string term) =>
                (await client.GetFromJsonAsync<JsonElement>($"/api/query/customers?emailSearch={term}")).GetArrayLength();
            var found = 0;
            for (var attempt = 0; attempt < 40 && found == 0; attempt++)
            {
                found = await SearchHitsAsync("ALICE");
                if (found == 0) await Task.Delay(500);
            }
            Assert.Equal(1, found);
            if (postgres is null)
            {
                var eventsDbPath = Directory.GetFiles(_scratchDir, "events.db", SearchOption.AllDirectories).Single();
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(eventsDbPath)!, "search.db")), "search.db should sit beside events.db");
            }
            else
            {
                // Every table holding the plaintext index, its hashes or their checkpoints is
                // unlogged: out of the WAL, physical backups and replicas.
                Assert.True(Convert.ToInt64(await PgScalarAsync(postgres,
                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'search' AND c.relkind = 'r'")) >= 4);
                Assert.Equal(0L, Convert.ToInt64(await PgScalarAsync(postgres,
                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'search' AND c.relkind = 'r' AND c.relpersistence <> 'u'")));
            }

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
            psi.Environment["CQRS_OPS_PORT"] = "0";
            psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
            process = StartHost(psi, hostLog);
            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(500);
                try { (await client.GetAsync("/api/query/customers")).EnsureSuccessStatusCode(); break; }
                catch (HttpRequestException) { /* not listening yet -- retry */ }
            }
            Assert.False(process.HasExited, $"restarted host exited early:\n{Tail(hostLog)}");
            int? indexVersion = null;
            for (var attempt = 0; attempt < 40 && indexVersion != 2; attempt++)
            {
                await using (var search = await OpenSearchStoreAsync(postgres))
                    indexVersion = await search.IndexVersionAsync("customers:hashed");
                if (indexVersion != 2) await Task.Delay(500);
            }
            Assert.Equal(2, indexVersion);
            Assert.Equal(1, await ExactHitsAsync("alice@example.com"));
            Assert.Equal(2, facade.Handler.HmacKeyVersions[^1]); // the search now hashes at version 2
            Assert.DoesNotContain(null, facade.Handler.HmacKeyVersions); // every call pinned a version

            // On Postgres an erasure rewrites the index tables (VACUUM FULL), so no deleted row
            // version survives in their files: the rewrite gives the table a new file.
            const string containsTable = "search.customers__match_email_email";
            var fileBeforeErasure = postgres is null ? null
                : await PgScalarAsync(postgres, $"SELECT pg_relation_filenode('{containsTable}')");

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
            if (postgres is not null)
            {
                object? fileAfterErasure = fileBeforeErasure;
                for (var attempt = 0; attempt < 40 && Equals(fileAfterErasure, fileBeforeErasure); attempt++)
                {
                    fileAfterErasure = await PgScalarAsync(postgres, $"SELECT pg_relation_filenode('{containsTable}')");
                    if (Equals(fileAfterErasure, fileBeforeErasure)) await Task.Delay(500);
                }
                Assert.NotEqual(fileBeforeErasure, fileAfterErasure);
            }

            // A returning person is a new subject: the guard refuses the old id.
            using var again = await client.PostAsJsonAsync("/api/cqrs/customer/c1b/RegisterCustomer",
                new { customerId = "c1", email = "alice@example.com" });
            Assert.False(again.IsSuccessStatusCode, "PII for an erased subject id must be refused");

            // Restore drill: the search store comes back from a backup taken before the erasure,
            // with the erased person's row and the ledger position it had then. The log gives
            // the index nothing to replay (its consumer is past the SubjectErased event), so only
            // the startup purge against the key service's erasure ledger can remove the row.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            process.Dispose();
            await using (var search = await OpenSearchStoreAsync(postgres))
            {
                await using var restore = search.Connection.CreateCommand();
                restore.CommandText = "INSERT INTO customers__match_email_email (row_key, term, subject) VALUES ('c1', 'alice@example.com', 'c1')";
                await restore.ExecuteNonQueryAsync();
                await search.SaveCheckpointAsync(ISearchIndexStore.ErasureLedgerCheckpoint, 0);
            }
            port = FreeTcpPort();
            psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            psi.Environment["CQRS_OPS_PORT"] = "0";
            psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
            process = StartHost(psi, hostLog);
            client.Dispose();
            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            int? restoredHits = null;
            for (var attempt = 0; attempt < 60 && restoredHits is null; attempt++)
            {
                await Task.Delay(500);
                try { restoredHits = await SearchHitsAsync("alice"); }
                catch (HttpRequestException) { /* not listening yet -- retry */ }
            }
            Assert.False(process.HasExited, $"host restarted after the restore exited early:\n{Tail(hostLog)}");
            // The first answer the restarted host gives is already purged: the purge runs before
            // the routes open.
            Assert.True(restoredHits == 0, $"expected 0 hits after the restore, got {restoredHits?.ToString() ?? "no answer"}:\n{Tail(hostLog)}");
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
