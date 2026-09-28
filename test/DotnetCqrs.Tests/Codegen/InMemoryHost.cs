using System.Net;
using System.Net.Sockets;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Verification;
using Microsoft.AspNetCore.Builder;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Runs generated code as a real web host, over real HTTP on a free loopback port, but
/// compiled in memory and started in this process (<see cref="InProcessHarness"/>) instead of
/// a scratch project built and run with <c>dotnet build</c>/<c>dotnet run</c> and polled until
/// it answered: seconds rather than minutes.
///
/// <para>A test program builds its <c>WebApplication</c> as usual and ends with
/// <c>await DotnetCqrs.Tests.Codegen.InMemoryHost.ServeAsync(app);</c> in place of
/// <c>await app.RunAsync();</c>. <see cref="StartAsync"/> returns once it is listening;
/// disposing the result stops it. Settings reach the program through <see cref="Setting"/>,
/// not environment variables, which are one per process.</para>
/// </summary>
public static class InMemoryHost
{
    // What Microsoft.NET.Sdk.Web adds to Microsoft.NET.Sdk's implicit usings (InProcessHarness
    // supplies those): the scratch projects were web projects, and generated routes rely on it.
    private const string WebImplicitUsings = """
        global using System.Net.Http.Json;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    // Flows into the program's own execution (InProcessHarness runs it via Task.Run, which
    // captures it), so several hosts can run at once without sharing anything.
    private static readonly AsyncLocal<Session?> Current = new();

    internal sealed class Session(string url, IReadOnlyDictionary<string, string> settings)
    {
        public string Url { get; } = url;
        public IReadOnlyDictionary<string, string> Settings { get; } = settings;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Stop { get; } = new();
    }

    /// <summary>For the program: serves <paramref name="app"/> on this run's URL until the test
    /// is done with it.</summary>
    public static async Task ServeAsync(WebApplication app)
    {
        var session = Current.Value
            ?? throw new InvalidOperationException("InMemoryHost.ServeAsync called outside InMemoryHost.StartAsync");
        app.Urls.Clear();
        app.Urls.Add(session.Url);
        await app.StartAsync();
        session.Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, session.Stop.Token);
        }
        catch (OperationCanceledException)
        {
            // the test is done
        }
        await app.StopAsync();
        await app.DisposeAsync();
    }

    /// <summary>For the program: a setting this run was started with, or null.</summary>
    public static string? Setting(string name) => Current.Value?.Settings.GetValueOrDefault(name);

    /// <summary>Compiles <paramref name="files"/> with <paramref name="programCs"/> and starts
    /// it; returns once it is serving. Throws with the compiler's errors, or with the program's
    /// exception if it ends before serving.</summary>
    public static async Task<Running> StartAsync(
        IEnumerable<GeneratedFile> files, string programCs, IReadOnlyDictionary<string, string>? settings = null)
    {
        var session = new Session($"http://127.0.0.1:{FreeTcpPort()}", settings ?? new Dictionary<string, string>());
        Current.Value = session;
        var sources = files
            .Select(f => (f.Name, f.Source))
            .Append(("WebImplicitUsings.g.cs", WebImplicitUsings))
            .Append(("Program.cs", programCs));
        var program = InProcessHarness.RunAsync(sources, [], CancellationToken.None, alsoReference: [typeof(InMemoryHost).Assembly]);

        var first = await Task.WhenAny(session.Started.Task, program, Task.Delay(TimeSpan.FromMinutes(2)));
        if (first == program)
        {
            await program; // rethrows the compiler's errors or the program's exception
            throw new InvalidOperationException("the program ended before it started serving");
        }
        if (first != session.Started.Task)
        {
            await session.Stop.CancelAsync();
            throw new TimeoutException("the program did not start serving within 2 minutes");
        }
        return new Running(session, program);
    }

    /// <summary>A started host: <see cref="Client"/> talks to it; disposing stops it.</summary>
    public sealed class Running : IAsyncDisposable
    {
        private readonly Session _session;
        private readonly Task _program;

        internal Running(Session session, Task program)
        {
            _session = session;
            _program = program;
            Client = new HttpClient { BaseAddress = new Uri(session.Url) };
        }

        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _session.Stop.CancelAsync();
            await _program.WaitAsync(TimeSpan.FromSeconds(30));
            _session.Stop.Dispose();
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
