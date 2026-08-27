using DotnetCqrs.Consumers;

namespace DotnetCqrs.Projections;

/// <summary>
/// A consumer that materializes events into denormalized SQLite read-model tables.
/// A projection is disposable and reproducible: reset its checkpoint to 0, wipe the
/// tables it owns, and re-running the same <see cref="ConsumerEngine"/> pass rebuilds
/// it from the log — there is no migration, only replay.
/// </summary>
public interface IProjection : IConsumer
{
    /// <summary>The read-model tables this projection owns — the tables a rebuild
    /// wipes, and a future write-guard (see the concepts doc) would protect from
    /// direct writes.</summary>
    IReadOnlyList<string> Tables { get; }
}
