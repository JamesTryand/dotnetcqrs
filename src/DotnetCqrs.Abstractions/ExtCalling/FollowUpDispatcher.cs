using System.Net.Http.Headers;
using System.Text;
using DotnetCqrs.Deciders;

namespace DotnetCqrs.ExtCalling;

/// <summary>
/// One follow-up command <see cref="ExtCallerConsumer"/> wants applied, together with
/// the causation/correlation/idempotency metadata it derived from the source event. An
/// <see cref="IFollowUpDispatcher"/> turns this into either an in-process
/// <see cref="DeciderRegistry"/> call or an HTTP POST to a remote command gateway.
/// </summary>
/// <param name="Aggregate">Target aggregate name.</param>
/// <param name="Id">Target aggregate id.</param>
/// <param name="Name">Command name.</param>
/// <param name="Payload">Command JSON payload; "" is sent as "{}".</param>
/// <param name="Actor">Always "extcall:&lt;name&gt;". Honoured by
/// <see cref="InProcessFollowUpDispatcher"/>; <see cref="GatewayFollowUpDispatcher"/>
/// structurally cannot set it — a remote gateway derives the actor from the
/// authenticated caller of the dispatch request, never from anything in the request the
/// caller controls — so the same follow-up carries a different actor depending on
/// dispatch mode. Provenance only; deciders must not make authorization decisions on
/// it.</param>
/// <param name="CausationId">The source event's id.</param>
/// <param name="CorrelationId">The source event's correlation id (its own id if it is
/// the root of the chain).</param>
/// <param name="CommandId">Deterministic per (consumer, source event, follow-up index):
/// the key a future dedup index — or a pocketcqrs target's Idempotency store — uses to
/// recognise a redelivered follow-up. dotnetcqrs's own gateway has no idempotency store
/// yet and ignores it on the wire.</param>
public sealed record FollowUpDispatch(
    string Aggregate, string Id, string Name, string Payload,
    string Actor, string CausationId, string CorrelationId, string CommandId);

/// <summary>
/// Applies an <see cref="ExtCallerConsumer"/> follow-up command.
/// <see cref="InProcessFollowUpDispatcher"/> is today's behaviour — straight into a
/// local <see cref="DeciderRegistry"/>; <see cref="GatewayFollowUpDispatcher"/> is
/// dotnetcqrs-multi-node Milestone 4 — an HTTP POST to a configured command gateway,
/// which may be another dotnetcqrs instance or a pocketcqrs one. Mirrors pocketcqrs's
/// <c>extcaller.Gateway</c> interface, widened to also cover the in-process case this
/// baseline started from.
/// </summary>
public interface IFollowUpDispatcher
{
    /// <summary>Applies one follow-up. Throws on any failure to apply it — a domain
    /// rejection or concurrency conflict from the target decider, or (remote dispatch)
    /// the target gateway being unreachable or answering non-2xx.
    /// <see cref="ExtCallerConsumer"/> dead-letters the source event when it does.</summary>
    Task DispatchAsync(FollowUpDispatch followUp, CancellationToken ct);
}

/// <summary>Dispatches follow-ups straight into a local <see cref="DeciderRegistry"/> —
/// the mode <see cref="ExtCallerConsumer"/> shipped with before there was any remote
/// gateway to target. Wrap the registry a consuming app already built:
/// <c>new InProcessFollowUpDispatcher(registry)</c>.</summary>
public sealed class InProcessFollowUpDispatcher(DeciderRegistry registry) : IFollowUpDispatcher
{
    public Task DispatchAsync(FollowUpDispatch followUp, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["actor"] = followUp.Actor,
            ["causationId"] = followUp.CausationId,
            ["correlationId"] = followUp.CorrelationId,
            ["commandId"] = followUp.CommandId,
        };
        return registry.HandleWithMetaAsync(
            followUp.Aggregate, followUp.Id, new Command(followUp.Name, followUp.Payload), meta, ct);
    }
}

/// <summary>Thrown by <see cref="GatewayFollowUpDispatcher"/> when the gateway answers
/// with a non-2xx status. <see cref="StatusCode"/> carries the exact code (400 domain
/// rejection, 404 unknown aggregate, 409 concurrency conflict, ...) so a caller can
/// tell them apart without matching on <see cref="Body"/> — same rationale as
/// pocketcqrs's <c>gatewayclient.ErrStatus</c>.</summary>
public sealed class GatewayDispatchException(int statusCode, string body)
    : Exception($"gateway returned {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

/// <summary>
/// Dispatches follow-ups over HTTP to a command gateway
/// (<c>POST {pathPrefix}/{aggregate}/{id}/{command}</c>) — the route shape both
/// <see cref="DotnetCqrs.Host.CqrsGatewayEndpoints"/> and pocketcqrs's <c>gateway.go</c>
/// serve. Hand-rolled on <see cref="HttpClient"/>, matching this project's established
/// call on hand-rolled code over a dependency for a small surface (see
/// <c>CqrsGatewayEndpoints.ForwardTo</c>, the same decision for Milestone 2's
/// write-forwarding).
///
/// <para>Sends <c>Causation-Id</c> and <c>Correlation-Id</c> headers: a dotnetcqrs
/// target threads them into the applied event's metadata so a reaction chain stays
/// intact across the hop. The <c>actor</c> does not cross — see
/// <see cref="FollowUpDispatch.Actor"/>. A deterministic <c>Idempotency-Key</c> is also
/// sent, for a pocketcqrs target's Idempotency store and for forward-compat;
/// dotnetcqrs's own gateway ignores it for now.</para>
/// </summary>
public sealed class GatewayFollowUpDispatcher : IFollowUpDispatcher
{
    private readonly HttpClient _http;
    private readonly string _pathPrefix;

    /// <param name="http">Its <see cref="HttpClient.BaseAddress"/> must be set and is
    /// the gateway root; any path on it (<c>http://host/gw/</c>) is preserved as long
    /// as it ends with '/'. Attach whatever auth the target needs to it (a bearer-token
    /// handler, ...); <see cref="Create"/> is a shortcut for the common
    /// bearer-token case.</param>
    /// <param name="pathPrefix">Route prefix the gateway was mapped under. Matches
    /// <c>CqrsGatewayEndpoints.MapCqrsGateway</c>'s own <c>prefix</c> parameter
    /// (default <c>/api/cqrs</c>); leading/trailing slashes are optional here.</param>
    public GatewayFollowUpDispatcher(HttpClient http, string pathPrefix = "api/cqrs")
    {
        if (http.BaseAddress is null)
            throw new ArgumentException(
                "GatewayFollowUpDispatcher: HttpClient.BaseAddress must be set to the gateway root", nameof(http));
        _http = http;
        _pathPrefix = pathPrefix.Trim('/');
    }

    /// <summary>Builds one against <paramref name="gatewayRoot"/>, sending
    /// <paramref name="token"/> (when non-empty) as <c>Authorization: Bearer</c> on
    /// every request. Minting the token — a shared-issuer JWT both ends validate, see
    /// Milestone 5 — is an operator concern, out of scope here.</summary>
    public static GatewayFollowUpDispatcher Create(
        Uri gatewayRoot, string? token = null, TimeSpan? timeout = null, string pathPrefix = "api/cqrs")
    {
        var root = gatewayRoot.AbsoluteUri.EndsWith('/')
            ? gatewayRoot
            : new Uri(gatewayRoot.AbsoluteUri + "/");
        var http = new HttpClient { BaseAddress = root };
        if (timeout is { } t) http.Timeout = t;
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new GatewayFollowUpDispatcher(http, pathPrefix);
    }

    public async Task DispatchAsync(FollowUpDispatch followUp, CancellationToken ct)
    {
        var payload = string.IsNullOrEmpty(followUp.Payload) ? "{}" : followUp.Payload;
        var path = $"{_pathPrefix}/{Uri.EscapeDataString(followUp.Aggregate)}" +
                   $"/{Uri.EscapeDataString(followUp.Id)}/{Uri.EscapeDataString(followUp.Name)}";

        // Relative URI so a BaseAddress carrying its own path prefix is preserved
        // (HttpClient resolves it as new Uri(BaseAddress, path)); the ctor/Create keep
        // BaseAddress ending in '/' and _pathPrefix free of a leading '/' for that to hold.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(followUp.CommandId))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", followUp.CommandId);
        if (!string.IsNullOrEmpty(followUp.CausationId))
            request.Headers.TryAddWithoutValidation("Causation-Id", followUp.CausationId);
        if (!string.IsNullOrEmpty(followUp.CorrelationId))
            request.Headers.TryAddWithoutValidation("Correlation-Id", followUp.CorrelationId);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new GatewayDispatchException((int)response.StatusCode, body);
        }
    }
}
