namespace DotnetCqrs.EventStore;

/// <summary>Thrown by every write method on an event store opened read-only (e.g.
/// <c>SqliteEventStore.OpenReadOnlyAsync</c>) — a same-host secondary polls the
/// master's <c>events.db</c> directly and must never append to it (or checkpoint/dead-letter
/// into it) itself; see <c>SqliteEventStore.OpenReadOnlyAsync</c>'s own doc comment
/// for why checkpoints need a separate, locally writable store instead.</summary>
public sealed class ReadOnlyStoreException(string operation)
    : Exception($"cannot {operation}: this event store was opened read-only (see SqliteEventStore.OpenReadOnlyAsync)");
