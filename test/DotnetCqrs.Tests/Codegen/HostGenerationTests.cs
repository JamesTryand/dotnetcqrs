using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotnetCqrs.EventStore;

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
}
