namespace DotnetCqrs.Deciders;

/// <summary>
/// A refusal carrying a machine-readable <see cref="Reason"/> beside its message, so a caller can decide on the reason
/// and never on the words (the command gateway contract v0.1, G5: the gateway answers it as a 400 problem body whose
/// <c>reason</c> is this one and whose <c>detail</c> is the message, verbatim). Thrown by <c>Decide</c> like any
/// refusal, so the registry marks it a rejection and nothing is appended. Any refusal may use it with a reason of its
/// own; the generated deciders use the two below.
/// </summary>
public class CommandRefusedException(string message, string reason) : CommandRejectedException(message)
{
    /// <summary>The reason, in lower-case words joined by hyphens, for example <c>does-not-exist</c>.</summary>
    public string Reason { get; } = reason;
}

/// <summary>The stream the command needs is not there yet (reason <c>does-not-exist</c>). A caller that sent it too
/// early may send it again once the stream exists.</summary>
public sealed class DoesNotExistException(string message) : CommandRefusedException(message, ReasonCode)
{
    /// <summary><c>does-not-exist</c>.</summary>
    public const string ReasonCode = "does-not-exist";
}

/// <summary>A create sent to a stream that already exists (reason <c>already-exists</c>). Final: sending it again
/// gets the same answer.</summary>
public sealed class AlreadyExistsException(string message) : CommandRefusedException(message, ReasonCode)
{
    /// <summary><c>already-exists</c>.</summary>
    public const string ReasonCode = "already-exists";
}
