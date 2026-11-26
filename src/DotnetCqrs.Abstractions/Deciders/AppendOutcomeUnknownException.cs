namespace DotnetCqrs.Deciders;

/// <summary>
/// The event store failed while appending a decided command's events (a database, I/O or connection failure, a
/// timeout, a cancellation), so whether they were stored is not known: the write may have committed before the
/// failure was seen. Raised by <see cref="DeciderRegistry"/> around the append only, never for a failure before it
/// (loading, revealing or protecting personal data), which applied nothing. The original failure is
/// <see cref="Exception.InnerException"/>. It is an infrastructure failure (503, worth checking), but a caller must
/// not assume nothing applied: check the command's outcome before sending it again.
/// </summary>
public sealed class AppendOutcomeUnknownException(Exception inner)
    : Exception("the event store failed while storing the command's events, so they may or may not have been stored", inner),
      IInfrastructureFailure;
