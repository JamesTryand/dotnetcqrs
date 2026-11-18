using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using Npgsql;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// The fault-injection floor (decision 0003 in the venture-overview hub) for a generated host:
/// the host is a real process, killed while work is in flight, restarted on the same data, and
/// checked for anything lost, corrupted or applied twice.
///
/// <para>The host is generated from <c>kill-tests.json</c>: a note is created once, a projection
/// lists notes, an automation stamps every note on a stamp stream derived from it, and another
/// touches every note on its own stream. A
/// projection saves its checkpoint once per batch and a reactor after each event
/// (<see cref="Consumers.ConsumerEngine"/>), so a kill replays events into both: the projection
/// must be idempotent, and a replayed reaction must be refused by the stamp's own "already
/// exists" rule.</para>
///
/// <para>Each round checks its kill landed part way through, so a badly timed run fails instead
/// of passing without testing anything. On SQLite, and on Postgres when <c>DOTNETCQRS_PG</c> is set
/// (<c>ops/postgres</c>): each Postgres round runs in a database of its own, made afresh.</para>
/// </summary>
[Collection(CompilesCollection.Name)]
[Trait("Category", "Slow")]
public class HostKillTests(KillHostFixture host) : IClassFixture<CliUnderTestFixture>, IClassFixture<KillHostFixture>
{
    /// <summary>Commands sent concurrently; the host is killed a few milliseconds after the first
    /// is acknowledged. Every acknowledged command must then have exactly one event, and no note
    /// more than one.</summary>
    [Fact(Timeout = 600000)]
    public Task Acknowledged_commands_survive_a_real_process_kill() => AcknowledgedCommandsAsync(postgres: false);

    [SkippableFact(Timeout = 600000)]
    public Task Acknowledged_commands_survive_a_real_process_kill_on_postgres()
    {
        Skip.If(host.PostgresBase is null, "DOTNETCQRS_PG is not set");
        return AcknowledgedCommandsAsync(postgres: true);
    }

    private async Task AcknowledgedCommandsAsync(bool postgres)
    {
        const int rounds = 5;
        const int commands = 200;
        var rng = new Random(1);
        var midBurst = 0;
        for (var round = 0; round < rounds; round++)
        {
            host.ResetData(postgres);
            var acked = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
            var firstAck = new TaskCompletionSource();
            using (var node = await host.StartAsync(postgres))
            {
                using var client = new HttpClient { BaseAddress = node.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
                var burst = Enumerable.Range(0, commands).Select(async i =>
                {
                    var id = $"k{i}";
                    try
                    {
                        using var response = await client.PostAsJsonAsync($"/api/cqrs/note/{id}/CreateNote", new { noteId = id, title = id });
                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            acked[id] = true;
                            firstAck.TrySetResult();
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // the connection lost to the kill: not acknowledged, not a failure
                    }
                }).ToList();
                var landed = await Task.WhenAny(firstAck.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.True(landed == firstAck.Task, $"no command was acknowledged within 30s:\n{node.Tail()}");
                await Task.Delay(rng.Next(20));
                node.Kill();
                await Task.WhenAll(burst);
            }
            if (acked.Count is > 0 and < commands) midBurst++;

            using (await host.StartAsync(postgres))
            {
                await using var store = await host.OpenStoreAsync(postgres);
                for (var i = 0; i < commands; i++)
                {
                    var id = $"k{i}";
                    // the touch reaction adds its own event to the note's stream: count the creates
                    var created = (await store.LoadStreamAsync("note", id)).Count(e => e.Type == "NoteCreated");
                    if (acked.ContainsKey(id))
                        Assert.True(created == 1, $"round {round}: {id} was acknowledged before the kill but was created {created} times");
                    else
                        Assert.True(created <= 1, $"round {round}: {id} was created {created} times");
                }
            }
        }
        Assert.True(midBurst > 0, $"no kill landed part way through a burst of {commands}; the timings need adjusting");
    }

    /// <summary>A backlog of notes is in the log before the host starts; the host is killed while
    /// each consumer is catching up: the projection, the stamp reactor (a new stream per note, so
    /// a repeat is refused as "already exists") and the touch reactor (onto the note's own stream,
    /// where nothing but the reaction's command id refuses a repeat). After a restart every note
    /// has one row with its title, exactly one stamp and exactly one touch.</summary>
    [Fact(Timeout = 900000)]
    public Task Consumers_catch_up_once_after_a_real_process_kill() => ConsumersCatchUpAsync(postgres: false);

    [SkippableFact(Timeout = 900000)]
    public Task Consumers_catch_up_once_after_a_real_process_kill_on_postgres()
    {
        Skip.If(host.PostgresBase is null, "DOTNETCQRS_PG is not set");
        return ConsumersCatchUpAsync(postgres: true);
    }

    private async Task ConsumersCatchUpAsync(bool postgres)
    {
        const int rounds = 3;
        const int backlog = 1500;
        var rng = new Random(7);
        for (var round = 0; round < rounds; round++)
        {
            host.ResetData(postgres);
            // the backlog, written with the host down, as the decider would write it
            await using (var store = await host.OpenStoreAsync(postgres, readOnly: false))
            {
                for (var i = 0; i < backlog; i++)
                {
                    var id = $"n{i}";
                    await store.AppendAsync("note", id, 0,
                        [new NewEvent("NoteCreated", JsonSerializer.Serialize(new { noteId = id, title = "title " + id }))]);
                }
            }

            // The engine catches its consumers up one at a time, in the order they are
            // registered, so each gets its own kill, part way through its own catch-up.
            var rowsAtKill = await KillPartWayAsync(round, "the projection", async node =>
            {
                using var client = new HttpClient { BaseAddress = node.BaseAddress };
                return await RowCountAsync(client);
            });
            var stampsAtKill = await KillPartWayAsync(round, "the stamp reactor", async _ => (await CountAsync(postgres, "StampCreated")).Count);
            var touchesAtKill = await KillPartWayAsync(round, "the touch reactor", async _ => (await CountAsync(postgres, "NoteTouched")).Count);

            using (var node = await host.StartAsync(postgres))
            {
                using var client = new HttpClient { BaseAddress = node.BaseAddress };
                // every consumer finishing, or settling short of the backlog: lost or stuck
                var last = (-1, -1, -1);
                var since = DateTime.UtcNow;
                while (true)
                {
                    var now = (await RowCountAsync(client), (await CountAsync(postgres, "StampCreated")).Count, (await CountAsync(postgres, "NoteTouched")).Count);
                    if (now.Item1 >= backlog && now.Item2 >= backlog && now.Item3 >= backlog) break;
                    if (now != last) (last, since) = (now, DateTime.UtcNow);
                    else Assert.True(DateTime.UtcNow - since < TimeSpan.FromSeconds(15),
                        $"round {round}: after the restart the consumers stopped at {now.Item1} rows, {now.Item2} stamps and {now.Item3} touches of {backlog}:\n{node.Tail()}");
                    await Task.Delay(500);
                }

                var rows = await client.GetFromJsonAsync<JsonElement>("/api/query/noteList");
                var titles = new Dictionary<string, string?>();
                foreach (var row in rows.EnumerateArray())
                {
                    // the generic projection keys a row by its stream's id, in note_id
                    var id = (row.TryGetProperty("noteId", out var key) ? key : row.GetProperty("note_id")).GetString()!;
                    Assert.True(titles.TryAdd(id, row.GetProperty("title").GetString()), $"round {round}: note {id} has more than one row");
                }
                Assert.Equal(backlog, titles.Count);
                for (var i = 0; i < backlog; i++)
                    Assert.Equal($"title n{i}", titles[$"n{i}"]);

                // settle anything still in flight, then count every reaction's event
                Dictionary<string, int> stamps, touches;
                var previous = -1L;
                while (true)
                {
                    stamps = await CountAsync(postgres, "StampCreated");
                    touches = await CountAsync(postgres, "NoteTouched");
                    var total = stamps.Values.Sum() + touches.Values.Sum();
                    if (total == previous) break;
                    previous = total;
                    await Task.Delay(500);
                }
                Assert.Equal(backlog, stamps.Count);
                Assert.Equal(backlog, touches.Count);
                for (var i = 0; i < backlog; i++)
                {
                    var stamp = $"n{i}"; // the stamp stream the note's noteId names (targetIdField)
                    Assert.True(stamps.TryGetValue(stamp, out var n) && n == 1,
                        $"round {round}: {stamp} has {stamps.GetValueOrDefault(stamp)} events, want exactly 1");
                    Assert.True(touches.GetValueOrDefault($"n{i}") == 1,
                        $"round {round}: note n{i} was touched {touches.GetValueOrDefault($"n{i}")} times, want exactly 1");
                }
                Console.WriteLine($"round {round}: killed with {rowsAtKill} rows, {stampsAtKill} stamps and {touchesAtKill} touches of {backlog}");
            }
        }

        async Task<int> KillPartWayAsync(int round, string consumer, Func<HostProcess, Task<int>> progress)
        {
            int atKill;
            using (var node = await host.StartAsync(postgres))
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (await progress(node) == 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, $"{consumer} never started catching up:\n{node.Tail()}");
                    await Task.Delay(20);
                }
                await Task.Delay(rng.Next(150));
                atKill = await progress(node);
                node.Kill();
            }
            Assert.True(atKill < backlog, $"round {round}: the kill landed after {consumer} had finished ({atKill} of {backlog})");
            return atKill;
        }

        static async Task<int> RowCountAsync(HttpClient client)
        {
            try
            {
                var rows = await client.GetFromJsonAsync<JsonElement>("/api/query/noteList");
                return rows.GetArrayLength();
            }
            catch (HttpRequestException)
            {
                return 0;
            }
        }
    }

    /// <summary>Events of <paramref name="type"/> by stream id, read from the whole log.</summary>
    private async Task<Dictionary<string, int>> CountAsync(bool postgres, string type)
    {
        var counts = new Dictionary<string, int>();
        await using var store = await host.OpenStoreAsync(postgres);
        long after = 0;
        while (true)
        {
            var batch = await store.PollAsync(after, 1000);
            if (batch.Count == 0) break;
            foreach (var ev in batch)
            {
                if (ev.Type == type)
                    counts[ev.AggregateId] = counts.GetValueOrDefault(ev.AggregateId) + 1;
            }
            after = batch[^1].Position;
        }
        return counts;
    }
}

/// <summary>Generates and builds the kill-tests host once for the class, and starts it as a real
/// process (its own <c>dotnet KillTests.dll</c>, so a kill reaches the host itself).</summary>
public sealed class KillHostFixture : IDisposable
{
    private readonly string _dir;
    private readonly string _dll;

    public KillHostFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-kill-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "dotnetcqrs.slnx"))) root = root.Parent;
        var project = Path.Combine(root!.FullName, "src", "DotnetCqrs", "DotnetCqrs.csproj");
        var document = Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "kill-tests.json");

        Run("dotnet", [CliUnderTest.DllAsync().GetAwaiter().GetResult(), "generate", "--input", document,
            "--output", _dir, "--host", "--dotnetcqrs-project", project]);
        Run("dotnet", ["build", _dir, "-v", "quiet"]);
        _dll = Directory.GetFiles(Path.Combine(_dir, "bin"), "KillTests.dll", SearchOption.AllDirectories).Single();
    }

    /// <summary>The host's data directory, beside its build output.</summary>
    public string DataDir => Path.Combine(Path.GetDirectoryName(_dll)!, "data");

    public string EventsPath => Path.Combine(DataDir, "events.db");

    /// <summary>The Postgres server the Postgres rounds use (<c>DOTNETCQRS_PG</c>), or null.</summary>
    public string? PostgresBase { get; } =
        Environment.GetEnvironmentVariable("DOTNETCQRS_PG") is { Length: > 0 } pg ? pg : null;

    /// <summary>The database a Postgres round's host keeps everything in.</summary>
    public string PostgresDatabase =>
        new NpgsqlConnectionStringBuilder(PostgresBase) { Database = "dotnetcqrs_kill" }.ConnectionString;

    /// <summary>The host's event log, opened from outside it (on SQLite read-only unless the backlog is written).</summary>
    public async Task<IEventStore> OpenStoreAsync(bool postgres, bool readOnly = true) =>
        postgres ? await PostgresEventStore.OpenAsync(PostgresDatabase)
        : readOnly ? await SqliteEventStore.OpenReadOnlyAsync(EventsPath) : await SqliteEventStore.OpenAsync(EventsPath);

    /// <summary>Clears the host's data for a new round; the host must not be running.</summary>
    public void ResetData(bool postgres = false)
    {
        if (postgres)
        {
            using var connection = new NpgsqlConnection(PostgresBase);
            connection.Open();
            using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS dotnetcqrs_kill WITH (FORCE)", connection))
                drop.ExecuteNonQuery();
            using (var create = new NpgsqlCommand("CREATE DATABASE dotnetcqrs_kill", connection))
                create.ExecuteNonQuery();
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
                Directory.CreateDirectory(DataDir);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Starts the host on a free port and waits until it answers.</summary>
    public async Task<HostProcess> StartAsync(bool postgres = false)
    {
        var port = FreePort();
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(_dll);
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["CQRS_OPS_PORT"] = "0";
        psi.Environment["CQRS_OPS_BIND"] = "127.0.0.1";
        psi.Environment["CQRS_STATE_DIR"] = Path.Combine(_dir, "node-state");
        psi.Environment["DOTNETCQRS_POSTGRES"] = postgres ? PostgresDatabase : "";
        psi.Environment.Remove("CQRS_NODE_ID");
        var node = new HostProcess(Process.Start(psi)!, new Uri($"http://127.0.0.1:{port}"));
        using var client = new HttpClient { BaseAddress = node.BaseAddress };
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/api/query/noteList");
                if (response.IsSuccessStatusCode) return node;
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }
            await Task.Delay(250);
        }
        var tail = node.Tail();
        node.Dispose();
        throw new InvalidOperationException($"the host never answered:\n{tail}");
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited {process.ExitCode}:\n{stdout}{stderr.Result}");
    }
}

/// <summary>A running host: killed (a real process kill) on <see cref="Kill"/> or disposal.</summary>
public sealed class HostProcess : IDisposable
{
    private readonly Process _process;
    private readonly System.Text.StringBuilder _log = new();

    public HostProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_log) _log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_log) _log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public Uri BaseAddress { get; }

    public void Kill()
    {
        if (_process.HasExited) return;
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit(10000);
    }

    public string Tail()
    {
        lock (_log) return _log.Length <= 6000 ? _log.ToString() : _log.ToString(_log.Length - 6000, 6000);
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
