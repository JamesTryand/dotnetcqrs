namespace DotnetCqrs.Deciders;

/// <summary>
/// A refusal that says the actor may not run this command on this aggregate, rather than that the command is invalid
/// (schema 3.15.0, <c>requiredOwnership.stateField</c>: the aggregate's own state names its owner, and the actor isn't
/// them). Thrown by <c>Decide</c> like any refusal, so the registry marks it a rejection and nothing is appended; a host
/// answers it as forbidden (the gateway: 403) rather than as a bad request (400).
/// </summary>
public sealed class CommandForbiddenException(string message) : InvalidOperationException(message);
