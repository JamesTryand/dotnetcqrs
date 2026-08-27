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
/// only: no auth, idempotency, or forwarding yet (see this issue's later milestones).
/// The consuming app registers its own <see cref="DeciderRegistry"/> in DI; this
/// extension only maps the route.
/// </summary>
public static class CqrsGatewayEndpoints
{
    /// <summary>Maps <c>POST {prefix}/{aggregate}/{aggregateId}/{command}</c>, reading
    /// the request body as the command's JSON payload (empty body → <c>"{}"</c>) and
    /// dispatching through a <see cref="DeciderRegistry"/> resolved from DI.</summary>
    public static IEndpointRouteBuilder MapCqrsGateway(this IEndpointRouteBuilder endpoints, string prefix = "/api/cqrs")
    {
        endpoints.MapPost($"{prefix}/{{aggregate}}/{{aggregateId}}/{{command}}", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string aggregate, string aggregateId, string command,
        HttpRequest request, DeciderRegistry registry, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        try
        {
            var events = await registry.HandleAsync(aggregate, aggregateId, new Command(command, payload), ct);
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
