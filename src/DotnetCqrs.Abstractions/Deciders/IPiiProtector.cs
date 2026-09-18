using DotnetCqrs.EventStore;

namespace DotnetCqrs.Deciders;

/// <summary>
/// Per-aggregate hook that keeps personal data encrypted at rest without taking it away
/// from <see cref="Decider{TState}.Decide"/>. Registered alongside a decider via
/// <see cref="DeciderRegistry.Register{TState}"/>; null (the default, and every
/// hand-written decider today) means the registry's path is exactly what it was before
/// this hook existed. Both methods are I/O at the edges of the dispatch shell — the same
/// place the registry already loads the stream and appends — so <c>Decide</c>/<c>Evolve</c>
/// stay pure and synchronous. Implementations are generated from a model's
/// <c>field.pii</c> markers; the shape is deliberately provider-neutral (no crypto types).
/// </summary>
public interface IPiiProtector
{
    /// <summary>Decrypts every protected value in <paramref name="state"/> that is still
    /// unresolved, returning the state to decide against. Only called after a
    /// <c>Decide</c> attempt threw <see cref="RevealRequiredException"/> — a decision that
    /// never reads protected state costs no round trip.</summary>
    Task<object> RevealAsync(object state, CancellationToken ct);

    /// <summary>Encrypts every fresh protected value carried by <paramref name="events"/>'
    /// typed payloads, returning events safe to serialise and append. Called after
    /// <c>Decide</c>, before the registry serialises <see cref="NewEvent.Payload"/>.
    /// <paramref name="aggregateId"/> is the stream being appended to, for models whose
    /// data subject is the aggregate instance itself.</summary>
    Task<IReadOnlyList<NewEvent>> ProtectAsync(string aggregateId, IReadOnlyList<NewEvent> events, CancellationToken ct);
}

/// <summary>Thrown when a decision reads a protected value that has not been decrypted
/// yet. <see cref="DeciderRegistry"/> catches it, asks the aggregate's
/// <see cref="IPiiProtector"/> to reveal the state, and runs <c>Decide</c> again — safe
/// because <c>Decide</c> is pure. Escapes to the caller when no protector is registered.</summary>
public sealed class RevealRequiredException(string message) : Exception(message);
