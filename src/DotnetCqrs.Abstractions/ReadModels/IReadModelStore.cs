using System.Data.Common;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// A read-model database a projection materializes events into: denormalized,
/// freely rewritable/rebuildable tables that are never the source of truth. Exposes
/// the ADO.NET <see cref="DbConnection"/> so a projection writes ordinary SQL without
/// naming a provider — <c>SqliteReadModelStore</c> is the SQLite implementation, a
/// Postgres one (Milestone 7) is a second.
///
/// <para>The write-guard — which rejects out-of-band writes on projection-owned
/// tables — is part of this contract rather than a free-standing helper, because its
/// bypass state is per-connection state this store owns. SQLite enforces it with
/// connection-scoped triggers; Postgres (Milestone 7) will use a least-privilege role
/// and <see cref="BeginBypassAsync"/> becomes a no-op scope, leaving projection
/// bodies byte-identical across providers.</para>
/// </summary>
public interface IReadModelStore : IAsyncDisposable
{
    /// <summary>The open connection projections read and write through. Typed as the
    /// ADO.NET base class so nothing downstream is SQLite-bound.</summary>
    DbConnection Connection { get; }

    /// <summary>Installs the write-guard on <paramref name="tables"/>: after this,
    /// direct INSERT/UPDATE/DELETE on those tables fails unless made inside a
    /// <see cref="BeginBypassAsync"/> scope on this store. Call once, after the
    /// projection(s) owning the tables have created them. An empty list guards
    /// nothing (a legitimate "no projections yet" call, not a wildcard).</summary>
    Task InstallWriteGuardAsync(IReadOnlyList<string> tables, CancellationToken ct = default);

    /// <summary>Marks writes on this store as internal (projection) writes for the
    /// lifetime of the returned scope. Nests: writes stay allowed until every nested
    /// scope has been disposed.</summary>
    ValueTask<IAsyncDisposable> BeginBypassAsync(CancellationToken ct = default);
}
