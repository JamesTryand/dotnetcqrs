using System.Security.Claims;
using System.Text.Json;
using DotnetCqrs.Crypto;
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
    /// friends.
    ///
    /// <para><paramref name="forward"/> is dotnetcqrs-multi-node Milestone 2's
    /// write-forwarding: when set, every request is handed to it and proxied whole
    /// instead of decided locally — the shape a same-host secondary
    /// (<see cref="DotnetCqrs.EventStore.SqliteEventStore.OpenReadOnlyAsync"/>) uses to
    /// reach its master, matching pocketcqrs's own <c>Config.Forward http.Handler</c>
    /// (<c>gateway.go</c>) checked before any local Mode/auth/decide logic. Use
    /// <see cref="ForwardTo"/> to build one from an <see cref="HttpClient"/> whose
    /// <c>BaseAddress</c> is the master's gateway. Unlike pocketcqrs, this library does
    /// not suppress <c>.RequireAuthorization()</c> when <paramref name="forward"/> is
    /// set: dotnetcqrs's auth is a provider-agnostic bearer token validated
    /// independently against an external issuer's OIDC metadata, not a
    /// per-node-signed token only the master can verify (pocketcqrs's F-13), so a
    /// secondary validating its own inbound request locally before forwarding is
    /// correct, not broken — see this issue's README for the finding.</para>
    ///
    /// <para><paramref name="authorize"/> is the pluggable hook a generated
    /// <c>Generated.CommandAuthorization.AuthorizeAsync</c> (schema 2.5.0's command
    /// role/ownership/scope declarations, see <c>CommandAuthorizationGenerator</c>)
    /// plugs into, same "auth is not built into this library" precedent as
    /// <paramref name="resolveActor"/> — default <c>null</c> means no check, exactly
    /// today's unchanged behavior. Deliberately takes no <c>IReadModelStore</c>
    /// parameter of its own: an earlier draft did, but that would make EVERY consumer
    /// of this shared method register one in DI or risk minimal API's parameter-source
    /// inference silently mis-binding it as a request body -- the exact class of bug
    /// this project already hit once for a read-model query route (an unregistered
    /// <c>IReadModelStore</c> parameter 500s not just its own route but every route on
    /// the composite endpoint data source). Instead, whatever store a real
    /// <c>authorize</c> delegate needs is captured in its own closure by whoever wires
    /// it up — e.g. <c>authorize: (user, agg, cmd, id, payload, ct) =>
    /// Generated.CommandAuthorization.AuthorizeAsync(user, agg, cmd, id, payload,
    /// myReadModelStore, resolveOwnRole, resolveOwnStaffId, ct)</c> — so this library
    /// stays exactly as decoupled from <c>DotnetCqrs.ReadModels</c> as it is today.
    /// When supplied and it returns <c>false</c>, the request is refused with 403
    /// before <see cref="DeciderRegistry.HandleWithMetaAsync"/> ever loads any
    /// aggregate state.</para></summary>
    public static RouteHandlerBuilder MapCqrsGateway(
        this IEndpointRouteBuilder endpoints, string prefix = "/api/cqrs",
        Func<ClaimsPrincipal, string>? resolveActor = null,
        Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>>? authorize = null,
        RequestDelegate? forward = null)
    {
        resolveActor ??= DefaultResolveActor;
        return endpoints.MapPost($"{prefix}/{{aggregate}}/{{aggregateId}}/{{command}}",
            async (string aggregate, string aggregateId, string command, HttpRequest request, HttpContext httpContext,
             DeciderRegistry registry, CancellationToken ct) =>
            {
                if (forward is not null)
                {
                    await forward(httpContext);
                    return Results.Empty;
                }
                return await HandleAsync(aggregate, aggregateId, command, request, httpContext, registry, resolveActor, authorize, ct);
            });
    }

    /// <summary>Builds a <see cref="RequestDelegate"/> for <see cref="MapCqrsGateway"/>'s
    /// <c>forward</c> parameter that proxies the whole request to
    /// <paramref name="masterClient"/>'s <c>BaseAddress</c>: same method, path and query,
    /// body, and <c>Authorization</c> header (the master authenticates it, not this
    /// node — see <see cref="MapCqrsGateway"/>'s doc comment), copying the response back
    /// verbatim. No reverse-proxy package for one route shape, matching this project's
    /// own established call on hand-rolled code over a dependency for a small surface
    /// (<c>System.CommandLine</c> aside, which is stdlib-adjacent) — see
    /// dotnetcqrs-multi-node's Milestone 2 note.</summary>
    public static RequestDelegate ForwardTo(HttpClient masterClient) => async httpContext =>
    {
        var request = httpContext.Request;
        using var forwardRequest = new HttpRequestMessage(HttpMethod.Post, $"{request.Path}{request.QueryString}")
        {
            Content = new StreamContent(request.Body),
        };
        if (request.ContentType is not null)
            forwardRequest.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        if (request.Headers.Authorization.Count > 0)
            forwardRequest.Headers.TryAddWithoutValidation("Authorization", (IEnumerable<string?>)request.Headers.Authorization);

        using var response = await masterClient.SendAsync(
            forwardRequest, HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);

        httpContext.Response.StatusCode = (int)response.StatusCode;
        httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    };

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
        DeciderRegistry registry, Func<ClaimsPrincipal, string> resolveActor,
        Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>>? authorize, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        if (ContainsPiiEnvelope(payload))
            return Results.Problem(
                $"command payload contains a \"{PiiEnvelopeKey}\" object; send personal data as plaintext, " +
                "the host encrypts it", statusCode: StatusCodes.Status400BadRequest);

        if (authorize is not null)
        {
            // Parsed once, here, alongside the raw string still used for dispatch below
            // -- no second body read. fieldGatedRole (schema 2.5.0) is the one
            // authorization declaration that needs the payload itself, not just
            // aggregate/command/aggregateId; every other declaration simply ignores it.
            using var payloadDocument = JsonDocument.Parse(payload);
            var authorized = await authorize(httpContext.User, aggregate, command, aggregateId, payloadDocument.RootElement, ct);
            if (!authorized)
                return Results.Problem("not authorized", statusCode: StatusCodes.Status403Forbidden);
        }

        var actor = resolveActor(httpContext.User);
        var meta = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(actor))
            meta["actor"] = actor;

        // Provenance headers an out-of-process dispatcher sends -- dotnetcqrs-multi-node
        // Milestone 4's GatewayFollowUpDispatcher, or pocketcqrs's gatewayclient -- so a
        // reaction chain keeps its causation/correlation across the HTTP hop. Honoured
        // for any authenticated caller: dotnetcqrs has no ExternalCallerCollection
        // allow-list the way pocketcqrs gates this (see its gateway.actorMeta), and this
        // is strictly provenance -- `actor` above stays token-derived and is never
        // caller-settable. An Idempotency-Key header, if sent, is ignored: this gateway
        // has no idempotency store yet (see the Host survey).
        var causationId = request.Headers["Causation-Id"].ToString();
        if (!string.IsNullOrEmpty(causationId))
            meta["causationId"] = causationId;
        var correlationId = request.Headers["Correlation-Id"].ToString();
        if (!string.IsNullOrEmpty(correlationId))
            meta["correlationId"] = correlationId;

        try
        {
            var metaArg = meta.Count == 0 ? null : meta;
            var events = await registry.HandleWithMetaAsync(aggregate, aggregateId, new Command(command, payload), metaArg, ct);
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
        catch (SubjectErasedException ex)
        {
            // Permanent, unlike the 409 above: retrying with this id can never succeed, a
            // returning person needs a new one. Not the KMS facade's own 409 -- that's a
            // service-to-service detail, and here it would read as "reload and retry".
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone);
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

    private const string PiiEnvelopeKey = "$pii";

    /// <summary>True when any object anywhere in <paramref name="payload"/> has a
    /// <c>"$pii"</c> property. <c>DotnetCqrs.Crypto</c>'s <c>Pii&lt;T&gt;</c> converter reads
    /// such an object as an already-encrypted value (subject + ciphertext), which is right
    /// for stored events and for commands a reactor or ext-caller builds from them -- those
    /// call <see cref="DeciderRegistry"/> directly and never pass through here. This gateway
    /// is where outside callers supply command JSON, so an envelope arriving here would be
    /// appended as-is, bound to whatever subject the caller named. Broader than "the
    /// object's only key" (the converter's own rule) on purpose: no outside caller has a
    /// reason to send a <c>"$pii"</c> key at all, so refusing any leaves no near-miss shape
    /// for a later converter change to reopen. Parsed leniently, so nothing the dispatcher
    /// could read slips past, and property names are compared unescaped (a key spelled with
    /// JSON escapes still counts); a body that doesn't parse at all is left for dispatch to
    /// reject as before.</summary>
    private static bool ContainsPiiEnvelope(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return ContainsPiiEnvelope(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsPiiEnvelope(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (property.NameEquals(PiiEnvelopeKey) || ContainsPiiEnvelope(property.Value))
                        return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsPiiEnvelope(item))
                        return true;
                return false;
            default:
                return false;
        }
    }
}
