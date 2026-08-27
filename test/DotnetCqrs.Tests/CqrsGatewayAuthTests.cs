using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Host;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DotnetCqrs.Tests;

/// <summary>
/// Proves the auth plugin seam end to end: JWT bearer authentication (the mechanism
/// Entra ID and any other OAuth2/OIDC provider use) wired via ASP.NET Core's own
/// AddJwtBearer, gating the gateway with the standard .RequireAuthorization(), and the
/// validated caller's "oid" claim (Entra's convention for a signed-in user) landing in
/// the appended event's metadata as actor. Uses a locally-signed test token rather than
/// a real Entra tenant: the wiring under test is provider-agnostic, and Entra-specific
/// concerns (issuer/JWKS validation against a real tenant) are Microsoft.Identity.Web's
/// own well-tested job, not this library's.
/// </summary>
public class CqrsGatewayAuthTests : IAsyncDisposable
{
    private const string TestIssuer = "https://test-issuer.example";
    private const string TestAudience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("dotnetcqrs-test-signing-key-32-bytes-min"));

    private static Decider<bool> TaskDecider() => new()
    {
        InitialState = () => false,
        Decide = (_, cmd) => [new NewEvent("TaskCreated", cmd.Payload)],
        Evolve = (_, ev) => ev.Type == "TaskCreated",
    };

    private readonly SqliteEventStore _store;
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public CqrsGatewayAuthTests()
    {
        _store = SqliteEventStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        var registry = new DeciderRegistry(_store);
        registry.Register("task", TaskDecider());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(registry);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Without this, ASP.NET Core's legacy inbound claim mapping rewrites
                // well-known short claim names (including "oid") to long WS-Fed-style
                // URIs before HttpContext.User ever sees them -- e.g. "oid" becomes
                // "http://schemas.microsoft.com/identity/claims/objectidentifier".
                // DefaultResolveActor checks the literal claim names Entra actually
                // puts in the token, so any real host wiring Entra needs this too.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TestIssuer,
                    ValidAudience = TestAudience,
                    IssuerSigningKey = SigningKey,
                    ValidateLifetime = true,
                };
            });
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapCqrsGateway().RequireAuthorization();
        _app.StartAsync().GetAwaiter().GetResult();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _store.DisposeAsync();
    }

    private static string MintToken(string oidClaimValue)
    {
        var token = new JwtSecurityTokenHandler().CreateJwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            subject: new ClaimsIdentity([new Claim("oid", oidClaimValue)]),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Without_a_token_a_guarded_route_returns_401()
    {
        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected_with_401()
    {
        var expired = new JwtSecurityTokenHandler().CreateJwtSecurityToken(
            issuer: TestIssuer, audience: TestAudience,
            subject: new ClaimsIdentity([new Claim("oid", "user-1")]),
            notBefore: DateTime.UtcNow.AddMinutes(-10),
            expires: DateTime.UtcNow.AddMinutes(-5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(expired));

        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_tokens_oid_claim_becomes_the_appended_events_actor()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MintToken("user-123"));

        var response = await _client.PostAsync("/api/cqrs/task/t1/CreateTask", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stream = await _store.LoadStreamAsync("task", "t1");
        var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream.Single().Metadata)!;
        Assert.Equal("user-123", meta["actor"].GetString());
    }
}
