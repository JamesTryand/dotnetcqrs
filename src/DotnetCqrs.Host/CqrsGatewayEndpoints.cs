using System.Security.Claims;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DotnetCqrs.Host;

/// <summary>
/// The HTTP gateway: turns a <see cref="DeciderRegistry"/> already wired up by the
/// consuming app into a runnable command endpoint, same shape as pocketcqrs's
/// <c>gateway.go</c> — <c>POST /{aggregate}/{id}/{command}</c>. This is gateway *core*
/// plus the auth plugin seam; idempotency/forwarding/batching are not ported (see this
/// issue's "Host + plugins" survey for why).
///
/// Auth is deliberately not built into this library: <see cref="MapCqrsGateway"/>
/// returns the ordinary ASP.NET Core route builder, so the consuming app decides
/// whether/how to require authentication the standard way —
/// <c>app.MapCqrsGateway().RequireAuthorization()</c> — and configures whichever
/// authentication scheme it wants (JWT bearer against Entra ID or any other
/// OAuth2/OIDC provider, cookies, API keys, ...). Whatever scheme runs populates
/// <c>HttpContext.User</c>; this gateway reads it via <paramref name="resolveActor"/>
/// (a pluggable delegate, default below) and stamps the result into
/// <c>meta["actor"]</c> before calling <see cref="DeciderRegistry.HandleWithMetaAsync"/>
/// — the same seam <c>ReactorConsumer</c>/<c>ExtCallerConsumer</c> already use. No auth
/// configured at all (the Milestone 1 shape) means an unauthenticated
/// <see cref="ClaimsPrincipal"/>, <see cref="DefaultResolveActor"/> returns "", and
/// behavior is unchanged from before this milestone.
/// </summary>
public static class CqrsGatewayEndpoints
{
    /// <summary>Maps <c>POST {prefix}/{aggregate}/{aggregateId}/{command}</c>, reading
    /// the request body as the command's JSON payload (empty body → <c>"{}"</c>) and
    /// dispatching through a <see cref="DeciderRegistry"/> resolved from DI. Returns
    /// the route builder so the caller can chain <c>.RequireAuthorization()</c> and
    /// friends.</summary>
    public static RouteHandlerBuilder MapCqrsGateway(
        this IEndpointRouteBuilder endpoints, string prefix = "/api/cqrs", Func<ClaimsPrincipal, string>? resolveActor = null)
    {
        resolveActor ??= DefaultResolveActor;
        return endpoints.MapPost($"{prefix}/{{aggregate}}/{{aggregateId}}/{{command}}",
            (string aggregate, string aggregateId, string command, HttpRequest request, HttpContext httpContext,
             DeciderRegistry registry, CancellationToken ct) =>
                HandleAsync(aggregate, aggregateId, command, request, httpContext, registry, resolveActor, ct));
    }

    /// <summary>Checks well-known claim types rather than assuming one identity
    /// provider's shape: <c>oid</c> (Entra ID — the signed-in user's object id),
    /// <c>azp</c>/<c>appid</c> (Entra ID — the calling application's id, under a
    /// client-credentials/app-to-app flow), then <see cref="ClaimTypes.NameIdentifier"/>
    /// and <see cref="ClaimsIdentity.Name"/> as generic fallbacks. Returns "" when
    /// unauthenticated.
    ///
    /// <para>When wiring JWT bearer against Entra ID (or building a custom
    /// <paramref name="resolveActor"/> that expects the token's own claim names), set
    /// <c>JwtBearerOptions.MapInboundClaims = false</c>. Without it, ASP.NET Core's
    /// legacy inbound claim mapping silently rewrites short claim names to long
    /// WS-Fed-style URIs before <c>HttpContext.User</c> ever sees them — "oid" becomes
    /// "http://schemas.microsoft.com/identity/claims/objectidentifier" — and every
    /// claim lookup here returns null.</para></summary>
    public static string DefaultResolveActor(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return "";
        return user.FindFirstValue("oid")
            ?? user.FindFirstValue("azp")
            ?? user.FindFirstValue("appid")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.Identity.Name
            ?? "";
    }

    private static async Task<IResult> HandleAsync(
        string aggregate, string aggregateId, string command, HttpRequest request, HttpContext httpContext,
        DeciderRegistry registry, Func<ClaimsPrincipal, string> resolveActor, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        var actor = resolveActor(httpContext.User);
        var meta = string.IsNullOrEmpty(actor) ? null : new Dictionary<string, object> { ["actor"] = actor };

        try
        {
            var events = await registry.HandleWithMetaAsync(aggregate, aggregateId, new Command(command, payload), meta, ct);
            return Results.Ok(events);
        }
        catch (UnknownAggregateException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ConcurrencyException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex)
        {
            // A domain rejection from Decide -- an untyped exception, same as
            // pocketcqrs's Decide returning a plain error. No way to distinguish
            // "bad request" from other business-rule failures at this layer, so
            // 400 covers all of them, matching the gateway's own job: refuse the
            // command, don't guess why more precisely than the decider said.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
