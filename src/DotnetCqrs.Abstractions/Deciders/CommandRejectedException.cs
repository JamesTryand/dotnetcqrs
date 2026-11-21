namespace DotnetCqrs.Deciders;

/// <summary>
/// The typed rejection in the decider contract (the user, 2026-11-16, hub K8, option b): a decider refuses a command by
/// throwing this, or a type derived from it (<see cref="CommandRefusedException"/> with a reason,
/// <see cref="CommandForbiddenException"/>). A decider that declares <see cref="Decider{TState}.TypedRejections"/>
/// refuses only this way, so anything else escaping its <c>Decide</c> is a fault (the gateway: 500; a reactor retries
/// it) rather than a refusal. The generated deciders declare it; a hand-written decider moves across when next touched.
/// The message is meant for the caller and is answered verbatim.
/// </summary>
public class CommandRejectedException(string message) : InvalidOperationException(message);
