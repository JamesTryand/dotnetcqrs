// dotnetcqrs-multi-node Milestone 5: the dotnetcqrs end of the dotnetcqrs <-> pocketcqrs
// interop worked example. A minimal DotnetCqrs.Host gateway for the `task` aggregate --
// same decider shape MultiNode.Primary (Milestone 3) and ExtCallerRemoteDispatchTests
// (Milestone 4) already use -- with one thing those don't have: a real auth check on the
// command route, so direction B of the round trip (pocketcqrs -> dotnetcqrs) proves an
// actual token was validated, not just that the bytes were accepted.
//
// The check is a hand-rolled HS256 JWT verifier (System.Security.Cryptography only), not
// Microsoft.AspNetCore.Authentication.JwtBearer: a shared symmetric key is the smallest
// thing that makes "a shared token issuer" concrete without standing up an OIDC server,
// and hand-rolling a ~40-line verifier over a NuGet dependency for one route matches this
// project's established call (see CqrsGatewayEndpoints.ForwardTo's own comment). The Go
// driver in ../into-dotnetcqrs mints the matching token the same hand-rolled way.
//
// dotnetcqrs has no ExternalCallerCollection allow-list the way pocketcqrs gates
// causation/correlation provenance (see docs/interop.md) -- MapCqrsGateway threads the
// Causation-Id/Correlation-Id headers into event metadata for any authenticated caller.
// This sample just requires *a* valid shared-issuer token; a real deployment layers
// whatever scoping it wants on top, the ordinary ASP.NET Core way.
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;

var eventsPath = Environment.GetEnvironmentVariable("EVENTS_DB_PATH")
    ?? Path.Combine(Path.GetTempPath(), "dotnetcqrs-interop-events.db");
var listenUrl = Environment.GetEnvironmentVariable("LISTEN_URL") ?? "http://127.0.0.1:8891";

// The shared "issuer": a symmetric key plus the iss/aud both ends agree on. All three
// must match what ../into-dotnetcqrs/main.go signs with.
var jwtKey = Environment.GetEnvironmentVariable("INTEROP_JWT_KEY")
    ?? throw new InvalidOperationException("INTEROP_JWT_KEY must be set (shared HS256 signing key)");
var jwtIssuer = Environment.GetEnvironmentVariable("INTEROP_JWT_ISSUER") ?? "interop-issuer";
var jwtAudience = Environment.GetEnvironmentVariable("INTEROP_JWT_AUDIENCE") ?? "dotnetcqrs-interop";

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(eventsPath))!);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listenUrl);

var eventStore = await SqliteEventStore.OpenAsync(eventsPath);
var registry = new DeciderRegistry(eventStore);
registry.Register("task", TaskDecider());
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(eventStore);

var app = builder.Build();

// Auth check, scoped to the command route only -- the read/health endpoints below stay
// open so verify.sh can inspect what landed without a token.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/cqrs"))
    {
        var principal = ValidateBearer(ctx.Request.Headers.Authorization.ToString(),
            jwtKey, jwtIssuer, jwtAudience);
        if (principal is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "missing or invalid bearer token" });
            return;
        }
        ctx.User = principal;
    }
    await next();
});

app.MapCqrsGateway();

// Read helper: the stream's events as [{type, metadata}], for verify.sh's assertions.
app.MapGet("/events/{aggregate}/{id}", async (string aggregate, string id, SqliteEventStore store) =>
{
    var events = await store.LoadStreamAsync(aggregate, id);
    return Results.Ok(events.Select(e => new
    {
        e.Type,
        e.Sequence,
        Metadata = JsonSerializer.Deserialize<JsonElement>(e.Metadata),
    }));
});
app.MapGet("/healthz", () => Results.Ok("dotnetcqrs-interop-host"));

app.Run();

static Decider<bool> TaskDecider() => new()
{
    InitialState = () => false,
    Decide = (exists, cmd) => cmd.Name switch
    {
        "CreateTask" when exists => throw new InvalidOperationException("task already exists"),
        "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
        _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
    },
    Evolve = (_, ev) => ev.Type == "TaskCreated",
};

// Minimal HS256 JWT verification: signature, iss, aud, exp. Returns an authenticated
// principal (NameIdentifier = the token's `sub`, so CqrsGatewayEndpoints.DefaultResolveActor
// stamps meta["actor"] from it) or null.
static ClaimsPrincipal? ValidateBearer(string authorizationHeader, string key, string issuer, string audience)
{
    const string prefix = "Bearer ";
    if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
        return null;
    var token = authorizationHeader[prefix.Length..].Trim();
    var parts = token.Split('.');
    if (parts.Length != 3) return null;

    var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
    var expectedSig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), signingInput);
    byte[] actualSig;
    try { actualSig = Base64UrlDecode(parts[2]); }
    catch { return null; }
    if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig)) return null;

    JsonElement claims;
    try { claims = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecode(parts[1])); }
    catch { return null; }

    if (!claims.TryGetProperty("iss", out var iss) || iss.GetString() != issuer) return null;
    if (!AudienceMatches(claims, audience)) return null;
    if (claims.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var expUnix)
        && DateTimeOffset.FromUnixTimeSeconds(expUnix) < DateTimeOffset.UtcNow)
        return null;

    var sub = claims.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
    var identity = new ClaimsIdentity("interop-jwt");
    if (sub.Length > 0) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
    return new ClaimsPrincipal(identity);
}

static bool AudienceMatches(JsonElement claims, string audience)
{
    if (!claims.TryGetProperty("aud", out var aud)) return false;
    if (aud.ValueKind == JsonValueKind.String) return aud.GetString() == audience;
    if (aud.ValueKind == JsonValueKind.Array)
        return aud.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == audience);
    return false;
}

static byte[] Base64UrlDecode(string input)
{
    var s = input.Replace('-', '+').Replace('_', '/');
    s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
    return Convert.FromBase64String(s);
}
