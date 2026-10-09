using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Consumers;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>D27 live views, L2: the generated live route. A viewer gets the current result, then a new one each
/// time a projection changes what that viewer may see, with the viewer's own access rules applied every time.
/// Nothing polls: the engine's fallback tick is an hour in these hosts, so every push follows a commit.</summary>
public class LiveViewTests
{
    private static string ReadAccessJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", "read-access.json"));

    // The read-access example's generated routes over a real engine. /test/append stands in for a decider: it
    // commits events to the store, which is exactly what a command's handler does once decided (the example's
    // generated deciders are scaffolds that can't create a project). The commit nudges the engine, the projection
    // applies, the change feed fires, and live viewers are pushed the new result.
    private const string ProgramCsTemplate = """"
        using System.Security.Claims;
        using DotnetCqrs.Consumers;
        using DotnetCqrs.EventStore;
        using DotnetCqrs.ReadModels;
        using Generated.Project;
        using Generated.TimeEntry;

        var eventStore = await SqliteEventStore.OpenAsync(":memory:");
        IReadModelStore store = await SqliteReadModelStore.OpenAsync(":memory:");
        var entries = new TimeEntriesProjection(store);
        await entries.InitAsync();
        var managers = new ProjectManagersProjection(store);
        await managers.InitAsync();
        var engine = new ConsumerEngine(eventStore, eventStore, tick: TimeSpan.FromHours(1));
        engine.Register(entries);
        engine.Register(managers);
        var feed = new CountingFeed(engine);

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IReadModelStore>(store);
        builder.Services.AddSingleton<IReadModelChangeFeed>(feed);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var claims = new List<Claim>();
            if (context.Request.Headers["X-Test-Role"].ToString() is { Length: > 0 } role) claims.Add(new Claim("role", role));
            if (context.Request.Headers["X-Test-Subject"].ToString() is { Length: > 0 } subject) claims.Add(new Claim("sub", subject));
            if (context.Request.Headers["X-Test-Exp"].ToString() is { Length: > 0 } exp) claims.Add(new Claim("exp", exp));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
            await next();
        });
        string ResolveOwnRole(ClaimsPrincipal user) => user.FindFirst("role")?.Value ?? "";
        string? ResolveSubjectId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;

        app.MapPost("/test/append/{aggregate}/{id}/{version:long}/{type}", async (string aggregate, string id, long version, string type, HttpRequest request) =>
        {
            using var body = new StreamReader(request.Body);
            await eventStore.AppendAsync(aggregate, id, version, [new NewEvent(type, await body.ReadToEndAsync())]);
            return Results.Ok();
        });
        // Answers once no live view is subscribed any more: a callback from the subscription's disposal, not a poll.
        app.MapGet("/test/no-subscribers", () => feed.NoneAsync());

        await eventStore.AppendAsync("project", "p2:s1", 0, [new NewEvent("ProjectManagerAssigned", """{"assignmentId":"p2:s1","projectId":"p2","staffId":"s1"}""")]);
        await eventStore.AppendAsync("timeEntry", "e1", 0, [new NewEvent("TimeLogged", """{"entryId":"e1","projectId":"p1","staffId":"s1","hours":1}""")]);
        await eventStore.AppendAsync("timeEntry", "e2", 0, [new NewEvent("TimeLogged", """{"entryId":"e2","projectId":"p2","staffId":"s2","hours":2}""")]);
        await eventStore.AppendAsync("timeEntry", "e3", 0, [new NewEvent("TimeLogged", """{"entryId":"e3","projectId":"p3","staffId":"s3","hours":3}""")]);
        await engine.RunOnceAsync(); // seeded before serving; from here on only commits move the engine
        using var stop = new CancellationTokenSource();
        var loop = engine.StartAsync(stop.Token);

        {{MAP_CALL}}
        await DotnetCqrs.Tests.Codegen.InMemoryHost.ServeAsync(app);
        await engine.StopAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();

        sealed class CountingFeed(IReadModelChangeFeed inner) : IReadModelChangeFeed
        {
            private int _active;
            private TaskCompletionSource _none = Done();

            private static TaskCompletionSource Done()
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                done.SetResult();
                return done;
            }

            public Task NoneAsync() { lock (this) return _none.Task; }

            public IDisposable Subscribe(IReadOnlyCollection<string> tables, Action<ReadModelChanged> onChanged)
            {
                lock (this)
                {
                    if (_active++ == 0) _none = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return new Subscription(this, inner.Subscribe(tables, onChanged));
            }

            private sealed class Subscription(CountingFeed owner, IDisposable inner) : IDisposable
            {
                private int _disposed;

                public void Dispose()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                    inner.Dispose();
                    lock (owner)
                    {
                        if (--owner._active == 0) owner._none.TrySetResult();
                    }
                }
            }
        }
        """";

    private const string WiredMapCall =
        "app.MapTimeEntriesLiveRoute(resolveOwnRole: ResolveOwnRole, resolveSubjectId: ResolveSubjectId);";

    private static GeneratedFile[] GenerateReadAccessHost()
    {
        var mapped = DocumentMapper.Map(DocumentLoader.Parse(ReadAccessJson));
        var files = new List<GeneratedFile>();
        foreach (var domain in mapped.Domains)
        {
            files.AddRange(CSharpGenerator.Generate(domain));
            files.AddRange(domain.ReadModels.Select(rm => ReadModelQueryGenerator.Generate(domain, rm)));
        }
        return files.ToArray();
    }

    private static async Task AppendAsync(HttpClient client, string aggregate, string id, long version, string type, string data)
    {
        using var response = await client.PostAsync($"/test/append/{aggregate}/{id}/{version}/{type}",
            new StringContent(data, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task LogTimeAsync(HttpClient client, string entry, string project, string staff) =>
        AppendAsync(client, "timeEntry", entry, 0, "TimeLogged",
            $$"""{"entryId":"{{entry}}","projectId":"{{project}}","staffId":"{{staff}}","hours":1}""");

    private static string[] EntryIds(JsonElement view) =>
        view.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("entry_id").GetString()!).Order().ToArray();

    /// <summary>An open live view, read as Server-Sent Events.</summary>
    private sealed class LiveStream : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private LiveStream(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<(HttpStatusCode Status, LiveStream? Stream)> OpenAsync(
            HttpClient client, string? role, string? subject, string query = "", string? exp = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/query/timeEntries/live" + query);
            if (role is not null) request.Headers.Add("X-Test-Role", role);
            if (subject is not null) request.Headers.Add("X-Test-Subject", subject);
            if (exp is not null) request.Headers.Add("X-Test-Exp", exp);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                return (response.StatusCode, null);
            }
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            return (response.StatusCode, new LiveStream(response, new StreamReader(await response.Content.ReadAsStreamAsync())));
        }

        /// <summary>The next event (keep-alive comments skipped), or null when the server ended the stream.</summary>
        public async Task<(string Name, JsonElement Data)?> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? name = null;
            var data = new StringBuilder();
            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (name is null) continue;
                    return (name, JsonDocument.Parse(data.ToString()).RootElement.Clone());
                }
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event: ")) name = line["event: ".Length..];
                else if (line.StartsWith("data: ")) data.Append(line["data: ".Length..]);
            }
            return null;
        }

        public async Task<JsonElement> NextViewAsync()
        {
            var next = await NextAsync();
            Assert.NotNull(next);
            Assert.Equal("view", next.Value.Name);
            return next.Value.Data;
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task A_live_view_pushes_each_change_the_viewer_may_see_and_only_that()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}", WiredMapCall));
        var client = host.Client;

        var (status, opened) = await LiveStream.OpenAsync(client, "staff", "s1");
        Assert.Equal(HttpStatusCode.OK, status);
        await using var staff = opened!;
        var (_, managerOpened) = await LiveStream.OpenAsync(client, "manager", "m1");
        await using var manager = managerOpened!;

        // The current result straight away: s1's own entry, plus e2 on p2, which s1 manages. Never e3.
        var first = await staff.NextViewAsync();
        Assert.Equal(JsonValueKind.Null, first.GetProperty("position").ValueKind);
        Assert.Equal(["e1", "e2"], EntryIds(first));
        Assert.Equal(["e1", "e2", "e3"], EntryIds(await manager.NextViewAsync()));

        // Someone else's entry, on a project s1 doesn't manage: the manager is pushed it, s1 is pushed nothing.
        await LogTimeAsync(client, "e4", "p3", "s3");
        Assert.Equal(["e1", "e2", "e3", "e4"], EntryIds(await manager.NextViewAsync()));
        // s1's own entry: s1's next push is this one, so the e4 change above sent s1 nothing.
        await LogTimeAsync(client, "e5", "p1", "s1");
        var own = await staff.NextViewAsync();
        Assert.Equal(["e1", "e2", "e5"], EntryIds(own));
        Assert.True(own.GetProperty("position").GetInt64() > 0);

        // A grant change is re-evaluated: unassigning s1 from p2 changes only projectManagers, yet s1 loses e2.
        await AppendAsync(client, "project", "p2:s1", 1, "ProjectManagerUnassigned",
            """{"assignmentId":"p2:s1","projectId":"p2","staffId":"s1"}""");
        Assert.Equal(["e1", "e5"], EntryIds(await staff.NextViewAsync()));
        // And a new grant adds a project's entries.
        await AppendAsync(client, "project", "p3:s1", 0, "ProjectManagerAssigned",
            """{"assignmentId":"p3:s1","projectId":"p3","staffId":"s1"}""");
        Assert.Equal(["e1", "e3", "e4", "e5"], EntryIds(await staff.NextViewAsync()));
    }

    [Fact(Timeout = 300000)]
    [Trait("Category", "Compiles")]
    public async Task A_live_view_is_refused_exactly_as_the_query_would_be_and_ends_with_the_viewer()
    {
        await using var host = await InMemoryHost.StartAsync(GenerateReadAccessHost(), ProgramCsTemplate.Replace("{{MAP_CALL}}",
            WiredMapCall + "\napp.MapTimeEntriesLiveRoute(prefix: \"/unwired\", resolveOwnRole: ResolveOwnRole);"));
        var client = host.Client;

        // A param that names no column is the query route's 400, before any stream starts.
        Assert.Equal(HttpStatusCode.BadRequest, (await LiveStream.OpenAsync(client, "staff", "s1", "?nope=1")).Status);
        // No resolveSubjectId wired: a restricted viewer is refused (fail closed), not streamed every row.
        using (var unwired = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/unwired/timeEntries/live")
               {
                   Headers = { { "X-Test-Role", "staff" }, { "X-Test-Subject", "s1" } },
               }))
            Assert.Equal(HttpStatusCode.Forbidden, unwired.StatusCode);
        // A token that has already expired gets no stream.
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LiveStream.OpenAsync(client, "staff", "s1", exp: past)).Status);
        // None of those left a subscription behind.
        await client.GetAsync("/test/no-subscribers").WaitAsync(TimeSpan.FromSeconds(15));

        // The viewer going away ends the subscription.
        var (_, opened) = await LiveStream.OpenAsync(client, "staff", "s1");
        var stream = opened!;
        await stream.NextViewAsync();
        var none = client.GetAsync("/test/no-subscribers");
        Assert.False(none.IsCompleted);
        await stream.DisposeAsync();
        using (var gone = await none.WaitAsync(TimeSpan.FromSeconds(15)))
            Assert.Equal(HttpStatusCode.OK, gone.StatusCode);

        // A token expiring ends the stream, saying so; the client reconnects with a fresh one.
        var soon = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds().ToString();
        var (_, expiring) = await LiveStream.OpenAsync(client, "staff", "s1", exp: soon);
        await using var expiringStream = expiring!;
        await expiringStream.NextViewAsync();
        var end = await expiringStream.NextAsync();
        Assert.Equal("expired", end?.Name);
        Assert.Null(await expiringStream.NextAsync());
    }

    // LiveView itself, without a host: what can only be shown by holding a re-run open.

    private sealed class ManualFeed : IReadModelChangeFeed
    {
        private readonly List<Action<ReadModelChanged>> _subscribers = [];
        public int Active { get { lock (_subscribers) return _subscribers.Count; } }

        public IDisposable Subscribe(IReadOnlyCollection<string> tables, Action<ReadModelChanged> onChanged)
        {
            lock (_subscribers) _subscribers.Add(onChanged);
            return new Unsubscribe(() => { lock (_subscribers) _subscribers.Remove(onChanged); });
        }

        public void Publish(long position)
        {
            Action<ReadModelChanged>[] now;
            lock (_subscribers) now = [.. _subscribers];
            foreach (var s in now) s(new ReadModelChanged("t", position));
        }

        private sealed class Unsubscribe(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    /// <summary>Response body that lets a test wait for what has been written so far.</summary>
    private sealed class WatchedBody : Stream
    {
        private readonly StringBuilder _text = new();
        private readonly List<(Func<string, bool> Until, TaskCompletionSource Done)> _waiters = [];

        public string Text { get { lock (_text) return _text.ToString(); } }

        public Task WaitForAsync(Func<string, bool> until)
        {
            lock (_text)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (until(_text.ToString())) done.SetResult();
                else _waiters.Add((until, done));
                return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_text)
            {
                _text.Append(Encoding.UTF8.GetString(buffer, offset, count));
                var text = _text.ToString();
                foreach (var w in _waiters.Where(w => w.Until(text)).ToList())
                {
                    _waiters.Remove(w);
                    w.Done.SetResult();
                }
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static (DefaultHttpContext Http, WatchedBody Body, CancellationTokenSource Abort) Context(ClaimsPrincipal? user = null)
    {
        var body = new WatchedBody();
        var abort = new CancellationTokenSource();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            RequestAborted = abort.Token,
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        http.Response.Body = body;
        return (http, body, abort);
    }

    [Fact(Timeout = 60000)]
    public async Task A_burst_of_changes_during_a_re_run_collapses_into_one_more_re_run()
    {
        var feed = new ManualFeed();
        var (http, body, abort) = Context();
        var runs = 0;
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<LiveView.Outcome> Query(CancellationToken ct)
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 2)
            {
                secondStarted.SetResult();
                await releaseSecond.Task.WaitAsync(ct);
            }
            return LiveView.Outcome.Of(new[] { new { run } });
        }

        var stream = LiveView.StreamAsync(http, feed, ["t"], Query, keepAlive: TimeSpan.FromMilliseconds(300));
        await body.WaitForAsync(t => t.Contains("\"run\":1"));

        feed.Publish(1);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        for (var p = 2; p <= 50; p++) feed.Publish(p); // 49 changes while the second run is in flight
        releaseSecond.SetResult();

        // The 49 collapse into one more run. The keep-alive after it only comes once the worker is idle, so by
        // then every run there was going to be has happened: three, not fifty-one.
        await body.WaitForAsync(t => t.Contains("\"run\":3"));
        var afterThird = body.Text.Length;
        await body.WaitForAsync(t => t.IndexOf(": keep-alive", afterThird, StringComparison.Ordinal) >= 0);
        Assert.Equal(3, Volatile.Read(ref runs));
        Assert.Contains("event: view\ndata: {\"position\":50,\"rows\":[{\"run\":3}]}\n\n", body.Text);

        await abort.CancelAsync();
        await stream.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, feed.Active);
    }

    [Fact(Timeout = 60000)]
    public async Task An_unchanged_result_is_not_pushed_again()
    {
        var feed = new ManualFeed();
        var (http, body, abort) = Context();
        var runs = 0;
        var rows = new[] { new { id = "a" } };

        Task<LiveView.Outcome> Query(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(LiveView.Outcome.Of(rows));
        }

        var stream = LiveView.StreamAsync(http, feed, ["t"], Query, keepAlive: TimeSpan.FromMilliseconds(300));
        await body.WaitForAsync(t => t.Contains("event: view"));
        feed.Publish(1);
        // Idle again after the re-run: it ran, found the same result, and sent nothing.
        var before = body.Text.Length;
        await body.WaitForAsync(t => t.IndexOf(": keep-alive", before, StringComparison.Ordinal) >= 0);
        Assert.Equal(2, Volatile.Read(ref runs));
        Assert.Single(body.Text.Split("event: view").Skip(1));

        await abort.CancelAsync();
        await stream.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, feed.Active);
    }
}
