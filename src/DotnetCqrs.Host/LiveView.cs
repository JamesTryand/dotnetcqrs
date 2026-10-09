using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotnetCqrs.Consumers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotnetCqrs.Host;

/// <summary>
/// Serves a live view (D27): a read model's query, kept current for one viewer for as long as the view is shown,
/// as a Server-Sent Events stream. It is the last step of the data flow: command → decider → events → projection
/// applies → read model changed (<see cref="IReadModelChangeFeed"/>) → the new result is pushed to whoever is
/// showing that view. Nothing polls.
///
/// <list type="number">
/// <item>The viewer's own query runs once, with the viewer's own access rules. If it is refused, or the request
/// is bad, the refusal is answered as a plain HTTP response, exactly as the query route answers it, and no stream
/// starts. A stream never shows more than the query would.</item>
/// <item>Otherwise the current result goes out as <c>event: view</c>, <c>data: {"position":null,"rows":[...]}</c>.</item>
/// <item>Each change to one of <c>tables</c> marks the view dirty. One worker re-runs the viewer's query and
/// pushes the result only when it differs from the last one sent. At most one re-run is in flight; changes
/// arriving meanwhile collapse into one more. <c>position</c> is then the change the result reflects (at
/// least).</item>
/// <item>The stream ends when the viewer disconnects, which ends the subscription. It also ends when the
/// caller's token expires (its <c>exp</c> claim), after an <c>event: expired</c>: the client reconnects with
/// a fresh token, which also picks up a changed role. A re-run that is refused (it can't be, for the same
/// caller and params, but the rule is checked every time) ends it after an <c>event: refused</c>.</item>
/// </list>
///
/// <para>A comment line goes out after <see cref="DefaultKeepAlive"/> of quiet, so proxies don't close an idle
/// stream. That timer is transport upkeep, not polling for data: it never re-runs the query.</para>
/// </summary>
public static class LiveView
{
    /// <summary>How long a stream may be quiet before a keep-alive comment is sent.</summary>
    public static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromSeconds(25);

    /// <summary>One run of a view's query for one caller: the rows, or a refusal to answer instead.</summary>
    public readonly record struct Outcome(IResult? Refusal, object? Rows)
    {
        public static Outcome Refused(IResult refusal) => new(refusal, null);
        public static Outcome Of(object rows) => new(null, rows);
    }

    /// <summary>Streams <paramref name="query"/>'s result to the caller of <paramref name="http"/>, pushing a new
    /// result whenever a projection changes one of <paramref name="tables"/>, until the caller goes away.</summary>
    public static async Task StreamAsync(
        HttpContext http, IReadModelChangeFeed feed, IReadOnlyCollection<string> tables,
        Func<CancellationToken, Task<Outcome>> query, TimeSpan? keepAlive = null)
    {
        var quiet = keepAlive ?? DefaultKeepAlive;
        var expiresIn = TokenLifetimeLeft(http.User);
        if (expiresIn <= TimeSpan.Zero)
        {
            await Results.Unauthorized().ExecuteAsync(http);
            return;
        }
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        if (expiresIn is { } left)
            expiry.CancelAfter(left);
        var ct = expiry.Token;

        // One pending signal at most: a change arriving while one is already pending is the same news. A change
        // arriving during a re-run leaves one signal pending, so the result after it is always sent.
        var dirty = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        long latest = 0;
        // Subscribe before the first run, so a change landing between the two is not missed.
        using var subscription = feed.Subscribe(tables, change =>
        {
            long seen;
            while ((seen = Interlocked.Read(ref latest)) < change.Position
                   && Interlocked.CompareExchange(ref latest, change.Position, seen) != seen)
            {
            }
            dirty.Writer.TryWrite(true);
        });

        // The same serializer settings the query route's Results.Ok uses, so both answer the same JSON.
        var json = http.RequestServices?.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = await query(ct);
        if (first.Refusal is not null)
        {
            await first.Refusal.ExecuteAsync(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var sent = JsonSerializer.Serialize(first.Rows, json);
        try
        {
            await SendAsync(http, "view", $"{{\"position\":null,\"rows\":{sent}}}", ct);
            while (true)
            {
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(quiet);
                    try
                    {
                        await dirty.Reader.ReadAsync(idle.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        continue;
                    }
                }

                var position = Interlocked.Read(ref latest);
                var next = await query(ct);
                if (next.Refusal is not null)
                {
                    await SendAsync(http, "refused", "{}", ct);
                    return;
                }
                var rows = JsonSerializer.Serialize(next.Rows, json);
                if (rows == sent)
                    continue;
                sent = rows;
                await SendAsync(http, "view", $"{{\"position\":{position},\"rows\":{rows}}}", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The viewer went away (nothing more to say), or the token expired (say so, if it's still there).
            if (!http.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await SendAsync(http, "expired", "{}", http.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    // gone meanwhile
                }
            }
        }
        catch (IOException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The viewer went away mid-write.
        }
    }

    private static async Task SendAsync(HttpContext http, string name, string data, CancellationToken ct)
    {
        // data is one line of JSON (the serializer escapes newlines inside strings), so one data: line.
        await http.Response.WriteAsync($"event: {name}\ndata: {data}\n\n", Encoding.UTF8, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>How long the caller's token has left, from its <c>exp</c> claim (seconds since the epoch), or
    /// null when it has none.</summary>
    private static TimeSpan? TokenLifetimeLeft(ClaimsPrincipal user) =>
        user.FindFirst("exp")?.Value is { } exp && long.TryParse(exp, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow
            : null;
}
