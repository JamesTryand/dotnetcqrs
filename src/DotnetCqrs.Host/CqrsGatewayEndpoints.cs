using System.Security.Claims;
using System.Text.Json;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Idempotency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetCqrs.Host;

/// <summary>
/// The HTTP gateway: turns a <see cref="DeciderRegistry"/> already wired up by the
/// consuming app into a runnable command endpoint, same shape as pocketcqrs's
/// <c>gateway.go</c> — <c>POST /{aggregate}/{id}/{command}</c>. It meets the command gateway
/// HTTP contract v0.1 (both stacks', frozen 2026-11-21; <c>GatewayContractProbeTests</c> is its
/// conformance probe): every error an RFC 9457 problem body with the decider's message verbatim
/// in <c>detail</c> and, for the generated refusals, a <c>reason</c>
/// (<see cref="CommandRefusedException"/>); the accepted body <c>{"events":[...]}</c> with
/// <c>data</c> and <c>metadata</c> as objects; <c>Idempotency-Key</c> replaying a 200 from an
/// <see cref="IIdempotencyStore"/>; <c>Retry-After</c> on a 503. Batching and maintenance mode
/// are not ported.
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
/// configured at all (the original, open shape) means an unauthenticated
/// <see cref="ClaimsPrincipal"/>, <see cref="DefaultResolveActor"/> returns "", and
/// behavior is unchanged from before authorization existed.
/// </summary>
public static class CqrsGatewayEndpoints
{
    /// <summary>Maps <c>POST {prefix}/{aggregate}/{aggregateId}/{command}</c>, reading
    /// the request body as the command's JSON payload (empty body → <c>"{}"</c>) and
    /// dispatching through a <see cref="DeciderRegistry"/> resolved from DI. Returns
    /// the route builder so the caller can chain <c>.RequireAuthorization()</c> and
    /// friends.
    ///
    /// <para><paramref name="forward"/> is write-forwarding: when set, every request is handed to it and proxied whole
    /// instead of decided locally — the shape a same-host secondary
    /// (<see cref="DotnetCqrs.EventStore.SqliteEventStore.OpenReadOnlyAsync"/>) uses to
    /// reach its master, matching pocketcqrs's own <c>Config.Forward http.Handler</c>
    /// (<c>gateway.go</c>) checked before any local Mode/auth/decide logic. Use
    /// <see cref="ForwardTo"/> to build one from an <see cref="HttpClient"/> whose
    /// <c>BaseAddress</c> is the master's gateway. Unlike pocketcqrs, this library does
    /// not suppress <c>.RequireAuthorization()</c> when <paramref name="forward"/> is
    /// set: dotnetcqrs's auth is a provider-agnostic bearer token validated
    /// independently against an external issuer's OIDC metadata, not a
    /// per-node-signed token only the master can verify (as pocketcqrs's is), so a
    /// secondary validating its own inbound request locally before forwarding is
    /// correct, not broken.</para>
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
    /// myReadModelStore, resolveOwnRole, resolveOwnSubjectId, ct)</c> — so this library
    /// stays exactly as decoupled from <c>DotnetCqrs.ReadModels</c> as it is today.
    /// When supplied and it returns <c>false</c>, or throws <c>CommandForbiddenException</c>
    /// (answered with its message), the request is refused with 403
    /// before <see cref="DeciderRegistry.HandleWithMetaAsync"/> ever loads any
    /// aggregate state.</para></summary>
    public static RouteHandlerBuilder MapCqrsGateway(
        this IEndpointRouteBuilder endpoints, string prefix = "/api/cqrs",
        Func<ClaimsPrincipal, string>? resolveActor = null,
        Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>>? authorize = null,
        RequestDelegate? forward = null)
    {
        resolveActor ??= DefaultResolveActor;
        // The host's IIdempotencyStore from DI (resolved per request, never a handler parameter: an unregistered
        // one would poison every endpoint, see above), else one in this process's memory for this mapping.
        var fallbackIdempotency = new Lazy<IIdempotencyStore>(() => new InMemoryIdempotencyStore());
        var keyLocks = new KeyedLocks();
        return endpoints.MapPost($"{prefix}/{{aggregate}}/{{aggregateId}}/{{command}}",
            async (string aggregate, string aggregateId, string command, HttpRequest request, HttpContext httpContext,
             DeciderRegistry registry, CancellationToken ct) =>
            {
                if (forward is not null)
                {
                    // Counted and timed only on the writer that decides it (contract section 6.5).
                    await forward(httpContext);
                    return Results.Empty;
                }
                // Command outcomes (health/telemetry contract section 7), when the host registered
                // a NodeMetrics; without one nothing is recorded.
                var metrics = httpContext.RequestServices.GetService<NodeMetrics>();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var idempotency = httpContext.RequestServices.GetService<IIdempotencyStore>() ?? fallbackIdempotency.Value;
                var result = await HandleAsync(aggregate, aggregateId, command, request, httpContext, registry, resolveActor, authorize,
                    idempotency, keyLocks, ct);
                metrics?.RecordCommand(
                    (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));
                return result;
            });
    }

    /// <summary>Builds a <see cref="RequestDelegate"/> for <see cref="MapCqrsGateway"/>'s
    /// <c>forward</c> parameter that proxies the whole request to
    /// <paramref name="masterClient"/>'s <c>BaseAddress</c>: same method, path and query,
    /// body, and <c>Authorization</c> header (the master authenticates it, not this
    /// node — see <see cref="MapCqrsGateway"/>'s doc comment), copying the response back
    /// verbatim. No reverse-proxy package for one route shape, matching this project's
    /// own established call on hand-rolled code over a dependency for a small surface
    /// (<c>System.CommandLine</c> aside, which is stdlib-adjacent).
    ///
    /// <para>When the master can't be reached this node answers for it: <c>502</c> if the
    /// request couldn't be delivered (connection refused, DNS, TLS), <c>504</c> if it
    /// timed out, each with a problem body saying so and the exception in the server log.
    /// The command may or may not have been decided on a timeout, so a retry relies on
    /// the decider's own idempotency, as any retry does. A failure after the master's
    /// response has started streaming back can't change the status any more and aborts
    /// the response instead.</para></summary>
    public static RequestDelegate ForwardTo(HttpClient masterClient) => async httpContext =>
    {
        var request = httpContext.Request;
        // The target as the caller sent it, so a percent-encoded id (a %2F above all) reaches the writer unchanged.
        var rawTarget = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        var target = rawTarget is { Length: > 0 } && rawTarget[0] == '/' ? rawTarget : $"{request.Path}{request.QueryString}";
        using var forwardRequest = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StreamContent(request.Body),
        };
        if (request.ContentType is not null)
            forwardRequest.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        // The writer authenticates the caller and keeps the idempotency record, so both go with the command.
        foreach (var header in ForwardedHeaders)
            if (request.Headers[header].Count > 0)
                forwardRequest.Headers.TryAddWithoutValidation(header, (IEnumerable<string?>)request.Headers[header]);

        HttpResponseMessage response;
        try
        {
            response = await masterClient.SendAsync(
                forwardRequest, HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            LogForwardFailure(httpContext, ex);
            await Results.Problem("this node forwards writes to the primary, and the primary could not be reached; retry later",
                statusCode: StatusCodes.Status502BadGateway, title: "primary unreachable").ExecuteAsync(httpContext);
            return;
        }
        catch (OperationCanceledException ex) when (!httpContext.RequestAborted.IsCancellationRequested)
        {
            // HttpClient.Timeout, not our caller hanging up.
            LogForwardFailure(httpContext, ex);
            await Results.Problem("this node forwards writes to the primary, and the primary did not answer in time; " +
                "the command may or may not have been applied",
                statusCode: StatusCodes.Status504GatewayTimeout, title: "primary timed out").ExecuteAsync(httpContext);
            return;
        }
        using var _ = response;

        httpContext.Response.StatusCode = (int)response.StatusCode;
        httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (response.Headers.RetryAfter is { } retryAfter)
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        if (response.Headers.WwwAuthenticate.Count > 0)
            httpContext.Response.Headers.WWWAuthenticate = response.Headers.WwwAuthenticate.ToString();
        await response.Content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    };

    private static readonly string[] ForwardedHeaders = ["Authorization", "Idempotency-Key", "Causation-Id", "Correlation-Id"];

    /// <summary>A <see cref="MapCqrsGateway"/> <c>forward</c> for a reader with no writer to
    /// forward to: every command is refused with <c>503</c> and a problem body, decided by nothing
    /// and appended to nothing (this node's event log is a read-only replica). Health/telemetry
    /// contract section 7 counts it as <c>unavailable</c> ("read-only node without forwarding"); no
    /// writer ever sees it, so unlike a forwarded command it is counted here, when the host
    /// registered a <see cref="NodeMetrics"/>.</summary>
    public static readonly RequestDelegate RefuseReadOnly = async httpContext =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
        await Results.Problem("this node is a read-only reader with no writer configured to forward commands to; " +
            "send commands to the writer", statusCode: StatusCodes.Status503ServiceUnavailable, title: "read-only node")
            .ExecuteAsync(httpContext);
        httpContext.RequestServices.GetService<NodeMetrics>()?.RecordCommand(
            StatusCodes.Status503ServiceUnavailable, System.Diagnostics.Stopwatch.GetElapsedTime(started));
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

    /// <summary>The longest <c>Idempotency-Key</c> accepted (contract G7); a longer one is a 400.</summary>
    public const int MaxIdempotencyKeyLength = 200;

    private static async Task<IResult> HandleAsync(
        string aggregate, string aggregateId, string command, HttpRequest request, HttpContext httpContext,
        DeciderRegistry registry, Func<ClaimsPrincipal, string> resolveActor,
        Func<ClaimsPrincipal, string, string, string, JsonElement, CancellationToken, Task<bool>>? authorize,
        IIdempotencyStore idempotency, KeyedLocks keyLocks, CancellationToken ct)
    {
        (aggregate, aggregateId, command) = DecodedRoute(httpContext, aggregate, aggregateId, command);

        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        // Parsed once, strictly, here: a body that is not one JSON object is a malformed command (400) whatever is
        // wired, and the same document serves the $pii check and the authorize hook.
        JsonDocument payloadDocument;
        try
        {
            payloadDocument = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Results.Problem("the command body is not valid JSON; send the payload as one JSON object",
                statusCode: StatusCodes.Status400BadRequest, title: "malformed command");
        }
        using var _ = payloadDocument;
        if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            return Results.Problem("the command body must be one JSON object (an empty body is {})",
                statusCode: StatusCodes.Status400BadRequest, title: "malformed command");

        if (ContainsPiiEnvelope(payloadDocument.RootElement))
            return Results.Problem(
                $"command payload contains a \"{PiiEnvelopeKey}\" object; send personal data as plaintext, " +
                "the host encrypts it", statusCode: StatusCodes.Status400BadRequest);

        var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            return Results.Problem($"Idempotency-Key is at most {MaxIdempotencyKeyLength} characters",
                statusCode: StatusCodes.Status400BadRequest, title: "malformed command");

        // Erasure is permanent and cannot be undone, and a command with no declared policy is allowed
        // through this gateway, so the built-in dataSubject aggregate fails CLOSED: with no authorize
        // policy wired at all, none of its commands (RequestErasure, EraseSubject, and any a module such
        // as DotnetCqrs.Retention adds) is accepted. A host opts in by wiring `authorize` and deciding who
        // may do what; the roles are the host's. Compared ignoring case so a differently-cased route can
        // never slip past a registry that might also match it.
        if (authorize is null && string.Equals(aggregate, DotnetCqrs.Crypto.DataSubject.Aggregate, StringComparison.OrdinalIgnoreCase))
            return Results.Problem(
                "erasure commands are refused until the host wires an authorize policy deciding who may " +
                "send them; nothing was recorded",
                statusCode: StatusCodes.Status403Forbidden);

        if (authorize is not null)
        {
            // fieldGatedRole (schema 2.5.0) is the one authorization declaration that needs the payload itself,
            // not just aggregate/command/aggregateId; every other declaration simply ignores it.
            bool authorized;
            try
            {
                authorized = await authorize(httpContext.User, aggregate, command, aggregateId, payloadDocument.RootElement, ct);
            }
            catch (CommandForbiddenException ex)
            {
                // A refusal naming its own message (a scope's, schema 4.0.0): the caller is told why.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception ex) when (InfrastructureFailure.IsInfrastructure(ex))
            {
                // The policy read something (a read model) that was not there to read: before the decision, so
                // nothing applied and worth retrying (contract G3).
                LogFailure(httpContext, ex, aggregate, aggregateId, command);
                httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
                return Results.Problem(UnavailableDetail(ex), statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "dependency unavailable");
            }
            catch (Exception ex)
            {
                LogFailure(httpContext, ex, aggregate, aggregateId, command);
                return Results.Problem("the command's authorization failed inside the host before it could be decided; see the server log",
                    statusCode: StatusCodes.Status500InternalServerError, title: "internal error");
            }
            if (!authorized)
                return Results.Problem("not authorized", statusCode: StatusCodes.Status403Forbidden);
        }

        var actor = resolveActor(httpContext.User);
        var meta = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(actor))
            meta["actor"] = actor;

        // Provenance headers an out-of-process dispatcher sends --
        // GatewayFollowUpDispatcher, or pocketcqrs's gatewayclient -- so a
        // reaction chain keeps its causation/correlation across the HTTP hop. Honoured
        // for any authenticated caller: dotnetcqrs has no ExternalCallerCollection
        // allow-list the way pocketcqrs gates this (see its gateway.actorMeta), and this
        // is strictly provenance -- `actor` above stays token-derived and is never
        // caller-settable.
        var causationId = request.Headers["Causation-Id"].ToString();
        if (!string.IsNullOrEmpty(causationId))
            meta["causationId"] = causationId;
        var correlationId = request.Headers["Correlation-Id"].ToString();
        if (!string.IsNullOrEmpty(correlationId))
            meta["correlationId"] = correlationId;

        if (idempotencyKey.Length == 0)
            return (await DecideAsync(aggregate, aggregateId, command, payload, meta, httpContext, registry, ct)).Result;

        // Contract G7. Looked up after authorization, so a replay never skips the role check. One request per key
        // at a time on this node, so a duplicate sent while the first is deciding waits and is then replayed
        // rather than decided twice. Only a 200 is recorded: a 409 is decided again, and nothing that was not
        // decided (400, 401, 403, 404, 410, 503) is kept.
        var requestHash = RequestHash(aggregate, aggregateId, command, payload);
        using (await keyLocks.AcquireAsync(idempotencyKey, ct))
        {
            IdempotencyRecord? recorded;
            try
            {
                recorded = await idempotency.FindAsync(idempotencyKey, ct);
            }
            catch (Exception ex) when (InfrastructureFailure.IsInfrastructure(ex))
            {
                LogFailure(httpContext, ex, aggregate, aggregateId, command);
                httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
                return Results.Problem("the idempotency store is unavailable; retry later",
                    statusCode: StatusCodes.Status503ServiceUnavailable, title: "dependency unavailable");
            }
            if (recorded is not null)
                return recorded.RequestHash == requestHash
                    ? Results.Bytes(recorded.Body, recorded.ContentType)
                    : Results.Problem("Idempotency-Key already used for a different request",
                        statusCode: StatusCodes.Status422UnprocessableEntity, title: "idempotency key reused");

            var (result, accepted) = await DecideAsync(aggregate, aggregateId, command, payload, meta, httpContext, registry, ct);
            if (accepted is not null)
            {
                try
                {
                    // Best effort: the command is decided and appended either way, so the caller gets its answer.
                    await idempotency.TryAddAsync(idempotencyKey,
                        new IdempotencyRecord(requestHash, StatusCodes.Status200OK, AcceptedContentType, accepted), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(CqrsGatewayEndpoints).FullName!)
                        .LogError(ex, "command {Aggregate}/{AggregateId}/{Command} was decided, but its answer could not be recorded for replay",
                            aggregate, aggregateId, command);
                }
            }
            return result;
        }
    }

    private const string AcceptedContentType = "application/json";

    /// <summary>Sent with every 503 (contract G8): a dependency or a read-only node, worth trying again shortly.</summary>
    private const string RetryAfterSeconds = "1";

    /// <summary>Decides the command. The accepted body's bytes come back beside the result, so they can be recorded
    /// and replayed exactly.</summary>
    private static async Task<(IResult Result, byte[]? Accepted)> DecideAsync(
        string aggregate, string aggregateId, string command, string payload, Dictionary<string, object> meta,
        HttpContext httpContext, DeciderRegistry registry, CancellationToken ct)
    {
        try
        {
            var metaArg = meta.Count == 0 ? null : meta;
            var events = await registry.HandleWithMetaAsync(aggregate, aggregateId, new Command(command, payload), metaArg, ct);
            var body = AcceptedBody(events);
            return (Results.Bytes(body, AcceptedContentType), body);
        }
        catch (UnknownAggregateException ex)
        {
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound), null);
        }
        catch (ConcurrencyException ex)
        {
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict), null);
        }
        catch (SubjectErasedException ex)
        {
            // Permanent, unlike the 409 above: retrying with this id can never succeed, a
            // returning person needs a new one. Not the KMS facade's own 409 -- that's a
            // service-to-service detail, and here it would read as "reload and retry".
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status410Gone), null);
        }
        catch (CommandForbiddenException ex) when (DeciderRegistry.IsRejection(ex))
        {
            // Schema 3.15.0: the aggregate refused the actor (not the owner its own state names), not the command.
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden), null);
        }
        catch (CommandRefusedException ex) when (DeciderRegistry.IsRejection(ex))
        {
            // A refusal naming its reason (contract G5: the generated deciders' does-not-exist and
            // already-exists), so a caller decides on the reason rather than the words.
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["reason"] = ex.Reason }), null);
        }
        catch (Exception ex) when (DeciderRegistry.IsRejection(ex))
        {
            // A domain rejection: Decide threw it (DeciderRegistry marks exactly those).
            // Any type, same as pocketcqrs's Decide returning a plain error, so 400 covers
            // every business-rule failure -- refuse the command, don't guess why more
            // precisely than the decider said. The message is the decider's own, meant
            // for the caller, and goes in detail verbatim.
            return (Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest), null);
        }
        catch (AppendOutcomeUnknownException ex)
        {
            // The store failed while appending the decided events: they may have been stored. The gateway contract's
            // answer for that is 500, "failed; it may or may not have applied; retry with the same idempotency key"
            // (G3), never the 503 that promises nothing applied. Nothing is recorded under the key, so a retry is
            // decided again against whatever the stream now holds.
            LogFailure(httpContext, ex, aggregate, aggregateId, command);
            return (Results.Problem("the event store failed while storing the command's events, so it may or may not " +
                    "have applied; retry with the same Idempotency-Key, or check its outcome before sending it again",
                statusCode: StatusCodes.Status500InternalServerError, title: "outcome unknown"), null);
        }
        catch (Exception ex) when (InfrastructureFailure.IsInfrastructure(ex))
        {
            // A dependency failed around Decide before anything was written (loading, revealing or protecting
            // PII), so the command was never decided: 503, worth retrying. (A failure of the append itself is caught
            // above: it may have applied.) The body names the kind of failure only; the exception, which can carry hosts,
            // connection details or a ciphertext, goes to the server log.
            LogFailure(httpContext, ex, aggregate, aggregateId, command);
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;
            return (Results.Problem(UnavailableDetail(ex), statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "dependency unavailable"), null);
        }
        catch (Exception ex)
        {
            // Neither a rejection nor a known dependency failure: a fault in the host
            // around the decision -- e.g. a decision read protected state with no
            // IPiiProtector registered to reveal it (RevealRequiredException), a KMS key
            // used before it was ensured, or a plain bug -- or, in a decider that declares
            // TypedRejections (hub K8), a bug inside Decide itself. The caller did nothing
            // wrong. The message stays in the server log.
            LogFailure(httpContext, ex, aggregate, aggregateId, command);
            return (Results.Problem("the command failed inside the host; see the server log",
                statusCode: StatusCodes.Status500InternalServerError, title: "internal error"), null);
        }
    }

    /// <summary>
    /// The accepted body (contract G6): <c>{"events":[...]}</c>, each event an object whose <c>data</c> and
    /// <c>metadata</c> are the JSON objects stored, not strings holding them; no events is <c>[]</c>.
    /// </summary>
    public static byte[] AcceptedBody(IReadOnlyList<Event> events)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("events");
            foreach (var ev in events)
            {
                writer.WriteStartObject();
                writer.WriteNumber("position", ev.Position);
                writer.WriteString("id", ev.Id);
                writer.WriteString("aggregate", ev.Aggregate);
                writer.WriteString("aggregateId", ev.AggregateId);
                writer.WriteNumber("sequence", ev.Sequence);
                writer.WriteString("type", ev.Type);
                WriteJson(writer, "data", ev.Data);
                WriteJson(writer, "metadata", ev.Metadata);
                writer.WriteString("created", ev.Created);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes stored JSON as JSON. Empty is <c>{}</c>; text that does not parse (never written by the
    /// registry) is written as a string rather than failing an answer whose events are already appended.</summary>
    private static void WriteJson(Utf8JsonWriter writer, string name, string json)
    {
        writer.WritePropertyName(name);
        if (string.IsNullOrWhiteSpace(json))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(json);
        }
    }

    /// <summary>What an idempotency record answers: the aggregate, id, command and body, each length-prefixed so
    /// no two requests share one.</summary>
    private static string RequestHash(string aggregate, string aggregateId, string command, string payload)
    {
        var text = string.Concat(
            aggregate.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", aggregate,
            aggregateId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", aggregateId,
            command.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", command,
            payload);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The route's three values decoded from the request target as sent (contract G1: the caller percent-encodes
    /// the id, the gateway decodes it). The server decodes the path before routing except for <c>%2F</c>, so an id
    /// holding a <c>/</c> would otherwise reach the decider still encoded. Falls back to the route values when the
    /// raw target is not a plain path.
    /// </summary>
    private static (string Aggregate, string AggregateId, string Command) DecodedRoute(
        HttpContext httpContext, string aggregate, string aggregateId, string command)
    {
        var raw = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(raw) || raw[0] != '/')
            return (aggregate, aggregateId, command);
        var path = raw.Split('?', 2)[0];
        if (path.EndsWith('/'))
            path = path[..^1];
        var segments = path.Split('/');
        if (segments.Length < 4)
            return (aggregate, aggregateId, command);
        return (Uri.UnescapeDataString(segments[^3]), Uri.UnescapeDataString(segments[^2]), Uri.UnescapeDataString(segments[^1]));
    }

    /// <summary>One holder per key at a time, in this process. Entries go when nobody holds or waits for them.</summary>
    internal sealed class KeyedLocks
    {
        private readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> _locks = new(StringComparer.Ordinal);

        public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct)
        {
            SemaphoreSlim gate;
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var entry))
                    entry = (new SemaphoreSlim(1, 1), 0);
                _locks[key] = (entry.Gate, entry.Users + 1);
                gate = entry.Gate;
            }
            try
            {
                await gate.WaitAsync(ct);
            }
            catch
            {
                Leave(key, held: false);
                throw;
            }
            return new Holder(this, key);
        }

        private void Leave(string key, bool held)
        {
            lock (_locks)
            {
                var entry = _locks[key];
                if (held)
                    entry.Gate.Release();
                if (entry.Users == 1)
                {
                    _locks.Remove(key);
                    entry.Gate.Dispose();
                }
                else
                {
                    _locks[key] = (entry.Gate, entry.Users - 1);
                }
            }
        }

        private sealed class Holder(KeyedLocks owner, string key) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    owner.Leave(key, held: true);
            }
        }
    }

    /// <summary>Which dependency failed, in words safe to send to any caller.</summary>
    private static string UnavailableDetail(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ReadOnlyStoreException:
                    return "this node's event store is read-only and it is not configured to forward writes; send the command to the primary";
                case System.Data.Common.DbException or IOException or System.Net.Sockets.SocketException:
                    return "the event store is unavailable; retry later";
                case KmsProtocolException or HttpRequestException:
                    return "the key service is unavailable; retry later";
                case TimeoutException or OperationCanceledException:
                    return "a dependency timed out or the request was cancelled; retry later";
            }
        }
        return "a dependency is unavailable; retry later";
    }

    private static void LogForwardFailure(HttpContext httpContext, Exception ex)
    {
        var logger = httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(CqrsGatewayEndpoints).FullName!);
        logger?.LogError(ex, "forwarding {Path} to the primary failed", httpContext.Request.Path.Value);
    }

    private static void LogFailure(HttpContext httpContext, Exception ex, string aggregate, string aggregateId, string command)
    {
        var logger = httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(CqrsGatewayEndpoints).FullName!);
        logger?.LogError(ex, "command {Aggregate}/{AggregateId}/{Command} failed before it could be decided",
            aggregate, aggregateId, command);
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
    /// for a later converter change to reopen. Property names are compared unescaped (a key
    /// spelled with JSON escapes still counts). The body has already been parsed strictly:
    /// one that doesn't parse is a 400 before this runs.</summary>
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
