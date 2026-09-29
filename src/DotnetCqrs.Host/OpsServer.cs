using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetCqrs.Host;

/// <summary>
/// The ops port (health/telemetry contract section 2): <c>/healthz</c>, <c>/readyz</c> and
/// <c>/metrics</c>, on a port of their own, never the traffic port. It is a separate, minimal
/// Kestrel app so it can bind first, before configuration is validated or any store opens, and a
/// booting node answers instead of refusing connections.
///
/// <para>The port is <c>CQRS_OPS_PORT</c>, default <see cref="DefaultPort"/> (provisional until it
/// is registered on the Prometheus wiki). Several nodes on one machine must each set it: only one
/// process can bind the default, and a node that cannot bind its ops port does not start. The
/// endpoints are unauthenticated; the network path is the security boundary, so the ops port must
/// never be an ingress target.</para>
/// </summary>
public sealed class OpsServer : IAsyncDisposable
{
    public const string PortVariable = "CQRS_OPS_PORT";
    public const int DefaultPort = 10056;

    /// <summary>The address the ops port binds. Unset means every interface, which a real node
    /// needs so an orchestrator can reach it; <c>127.0.0.1</c> keeps it local (tests use this,
    /// which also avoids a Windows firewall prompt per test binary).</summary>
    public const string BindVariable = "CQRS_OPS_BIND";

    /// <summary>The ops port's base URL as readers reach it, which the writer puts in its heartbeat
    /// row (<c>writer_ops_url</c>) so a stale reader can ask whether the writer is up. Unset means
    /// <c>http://&lt;host&gt;:&lt;ops port&gt;</c>; set it wherever that hostname is not what readers
    /// can reach (NAT, container networks).</summary>
    public const string UrlVariable = "CQRS_OPS_URL";

    /// <summary><c>CQRS_OPS_URL</c> (<paramref name="configured"/>) if set, else
    /// <c>http://{bind}:{port}</c> when <paramref name="bind"/> (<c>CQRS_OPS_BIND</c>) names a specific
    /// address, since nothing else reaches it, else <c>http://{host}:{port}</c>. Throws
    /// <see cref="InvalidOpsUrlException"/> for anything but an absolute http or https URL, which
    /// fails the boot like any other bad setting.</summary>
    public static string AdvertisedUrl(string? configured, string? bind, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!string.IsNullOrWhiteSpace(bind) && bind is not ("*" or "0.0.0.0" or "::"))
                host = bind.Contains(':') && !bind.StartsWith('[') ? $"[{bind}]" : bind;
            return $"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        }
        return Uri.TryCreate(configured, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? configured.TrimEnd('/')
            : throw new InvalidOpsUrlException(configured);
    }

    private readonly WebApplication _app;

    private OpsServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    /// <summary>Where it is listening (the real port, when started on port 0).</summary>
    public Uri Address { get; }

    /// <summary><c>CQRS_OPS_PORT</c> if set, else <see cref="DefaultPort"/>. Throws
    /// <see cref="InvalidOpsPortException"/> for anything but 0-65535, which fails the boot like any
    /// other invalid setting.</summary>
    public static int PortFromEnvironment() => ParsePort(Environment.GetEnvironmentVariable(PortVariable));

    public static int ParsePort(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultPort;
        return int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= 65535
            ? port
            : throw new InvalidOpsPortException(configured);
    }

    /// <summary>Binds the ops port on <paramref name="bind"/> (null or empty: every interface)
    /// and starts answering. Port 0 picks a free port (tests). Throws if the port cannot be bound:
    /// the node must not start without it.</summary>
    public static async Task<OpsServer> StartAsync(NodeHealth health, int port, string? bind = null, CancellationToken ct = default)
    {
        var host = string.IsNullOrWhiteSpace(bind) ? "*" : bind.Contains(':') && !bind.StartsWith('[') ? $"[{bind}]" : bind;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Json(health.HealthzBody()));
        app.MapGet("/readyz", () =>
        {
            var (statusCode, body) = health.Readyz();
            return Results.Json(body, statusCode: statusCode);
        });
        app.MapGet("/metrics", async (CancellationToken ct) =>
            Results.Text(await health.Metrics.RenderAsync(health, ct), "text/plain; version=0.0.4; charset=utf-8"));

        await app.StartAsync(ct);
        var bound = new Uri(app.Urls.First().Replace("*", "localhost", StringComparison.Ordinal)
            .Replace("[::]", "localhost", StringComparison.Ordinal));
        return new OpsServer(app, bound);
    }

    /// <summary>Starts on <see cref="PortFromEnvironment"/> and <c>CQRS_OPS_BIND</c>.</summary>
    public static Task<OpsServer> StartAsync(NodeHealth health, CancellationToken ct = default) =>
        StartAsync(health, PortFromEnvironment(), Environment.GetEnvironmentVariable(BindVariable), ct);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>An invalid <c>CQRS_OPS_URL</c>: the boot fails, like any other invalid setting.</summary>
public sealed class InvalidOpsUrlException(string value) : Exception(
    $"{OpsServer.UrlVariable} '{value}' is not a valid URL: use the ops port's base URL as readers reach it, e.g. http://node-3:10056.")
{
    public string Value { get; } = value;
}

/// <summary>An invalid <c>CQRS_OPS_PORT</c>: the boot fails, like any other invalid setting.</summary>
public sealed class InvalidOpsPortException(string value) : Exception(
    $"{OpsServer.PortVariable} '{value}' is not a valid port: use a number from 0 to 65535.")
{
    public string Value { get; } = value;
}
