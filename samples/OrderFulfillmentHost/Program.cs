// The runnable counterpart to samples/OrderFulfillment: the SAME domain code
// (Orders/Tasks deciders), hosted for real over HTTP via DotnetCqrs.Host's gateway,
// with JWT bearer auth and local-disk file storage wired in -- proving the pieces
// this issue built (Milestones 1-3) actually compose, not just individually inside
// TestServer. Run with `dotnet run` from this directory; each run starts fresh.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using DotnetCqrs.Host.FileStorage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OrderFulfillment;

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var eventsPath = Path.Combine(dataDir, "events.db");
foreach (var f in Directory.GetFiles(dataDir, Path.GetFileName(eventsPath) + "*"))
    File.Delete(f);

var builder = WebApplication.CreateBuilder(args);

// --- Auth ---
// A real deployment points this at Entra ID (or any OAuth2/OIDC provider) instead,
// e.g.:
//
//   builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
//       .AddJwtBearer(options =>
//       {
//           options.Authority = "https://login.microsoftonline.com/{tenant}/v2.0";
//           options.Audience = "api://<app-id-uri>";
//           options.MapInboundClaims = false; // see DotnetCqrs.Host's own gotcha note
//       });
//
// This sample uses a locally-signed test key instead, so it runs without any real
// tenant or credentials -- get a token from POST /dev/token (development only; see
// the guard below -- NEVER ship a token-minting endpoint like that for real).
const string Issuer = "https://dotnetcqrs-sample.local";
const string Audience = "order-fulfillment-host";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("sample-only-signing-key-do-not-use-for-real!"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();

// --- CQRS write side ---
var eventStore = await SqliteEventStore.OpenAsync(eventsPath);
var registry = new DeciderRegistry(eventStore);
registry.Register(Orders.Aggregate, Orders.Decider());
registry.Register(Tasks.Aggregate, Tasks.Decider());
builder.Services.AddSingleton(registry);

// --- File storage ---
builder.Services.AddSingleton<IFileStore>(new LocalDiskFileStore(Path.Combine(dataDir, "files")));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapCqrsGateway().RequireAuthorization();

app.MapPost("/files/{key}", async (string key, HttpRequest request, IFileStore store, CancellationToken ct) =>
{
    try
    {
        await store.PutAsync(key, request.Body, ct);
        return Results.NoContent();
    }
    catch (ArgumentException ex)
    {
        // LocalDiskFileStore rejects a key that resolves outside its root (path
        // traversal, an absolute path, ...) -- a malformed/malicious caller input,
        // not a server fault, so 400 rather than letting it surface as a 500.
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
}).RequireAuthorization();

app.MapGet("/files/{key}", async (string key, IFileStore store, CancellationToken ct) =>
{
    try
    {
        var content = await store.GetAsync(key, ct);
        return content is null ? Results.NotFound() : Results.Stream(content);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
}).RequireAuthorization();

// Local-dev-only convenience: mints a token signed with this sample's own test key,
// so `curl`/Postman can exercise the guarded routes without a real identity
// provider. Gated on IsDevelopment() because a token-minting endpoint is a severe
// hole in anything that isn't a throwaway local sample -- copy the auth block above,
// never this one, into a real host.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/token", (string? oid) =>
    {
        var token = new JwtSecurityTokenHandler().CreateJwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            subject: new ClaimsIdentity([new Claim("oid", oid ?? "dev-user")]),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return Results.Text(new JwtSecurityTokenHandler().WriteToken(token));
    });
}

app.Run();
