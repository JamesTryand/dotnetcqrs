// dotnetcqrs-multi-node Milestone 5, direction A: a dotnetcqrs process dispatches a
// command into a *pocketcqrs* gateway, using Milestone 4's GatewayFollowUpDispatcher
// exactly as ExtCallerConsumer would -- no pocketcqrs-specific code. This is the
// "first-class supported pattern" the milestone is about: the same IFollowUpDispatcher
// a reaction chain uses in-process, pointed at another runtime's gateway.
//
// Auth: pocketcqrs's gateway only accepts a PocketBase-minted token. To get
// actor="extcall:<name>" AND the Causation-Id/Correlation-Id headers honoured, that
// token must belong to a record in the collection pocketcqrs was started with as
// --cqrsExternalCallerCollection (a superuser token gets actor but drops the provenance
// headers; --cqrsAllowAnonymous drops everything). verify.sh provisions that record and
// passes its token in SVC_TOKEN. See docs/interop.md.
//
// Usage:
//   IntoPocketCqrs <taskId> <title>          -- dispatch CreateTask, expect success
//   IntoPocketCqrs <taskId> <title> --expect-reject   -- expect the gateway to answer 400
using DotnetCqrs.ExtCalling;

var expectReject = args.Contains("--expect-reject");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length < 2)
{
    Console.Error.WriteLine("usage: IntoPocketCqrs <taskId> <title> [--expect-reject]");
    return 2;
}
var taskId = positional[0];
var title = positional[1];

var gatewayUrl = Environment.GetEnvironmentVariable("POCKETCQRS_URL") ?? "http://127.0.0.1:8890";
var token = Environment.GetEnvironmentVariable("SVC_TOKEN")
    ?? throw new InvalidOperationException("SVC_TOKEN must be set (a pocketcqrs auth token for the --cqrsExternalCallerCollection record)");
var causationId = Environment.GetEnvironmentVariable("CAUSATION_ID") ?? "interop-cause-1";
var correlationId = Environment.GetEnvironmentVariable("CORRELATION_ID") ?? "interop-corr-1";

var dispatcher = GatewayFollowUpDispatcher.Create(new Uri(gatewayUrl), token, TimeSpan.FromSeconds(10));

// CommandId becomes the Idempotency-Key on the wire. Deterministic per logical dispatch
// on the happy path (a redelivered source event reuses it). For --expect-reject we want
// a *genuine* re-decide, so use a fresh id: pocketcqrs's gateway has an always-on
// idempotency store and would otherwise replay the original 200 for the same key+body
// instead of letting the decider reject the duplicate. (dotnetcqrs has no such store --
// see docs/interop.md's note on the retry-semantics asymmetry.)
var commandId = expectReject ? $"interop-{taskId}-{Guid.NewGuid():N}" : $"interop-{taskId}";

// Actor is set here for completeness but does NOT cross the HTTP hop by design -- the
// receiving gateway derives actor from the authenticated caller, never from the request
// body (see FollowUpDispatch.Actor). On pocketcqrs that means "extcall:<record name>".
var followUp = new FollowUpDispatch(
    Aggregate: "task",
    Id: taskId,
    Name: "CreateTask",
    Payload: $$"""{"title":{{System.Text.Json.JsonSerializer.Serialize(title)}}}""",
    Actor: "extcall:dotnetcqrs",
    CausationId: causationId,
    CorrelationId: correlationId,
    CommandId: commandId);

try
{
    await dispatcher.DispatchAsync(followUp, CancellationToken.None);
    if (expectReject)
    {
        Console.Error.WriteLine("FAIL: expected the gateway to reject the command, but it succeeded");
        return 1;
    }
    Console.WriteLine($"OK: CreateTask({taskId}) dispatched into {gatewayUrl}");
    return 0;
}
catch (GatewayDispatchException ex)
{
    if (expectReject && ex.StatusCode == 400)
    {
        Console.WriteLine($"OK: gateway rejected as expected: {ex.StatusCode} {ex.Body}");
        return 0;
    }
    Console.Error.WriteLine($"FAIL: gateway returned {ex.StatusCode}: {ex.Body}");
    return 1;
}
