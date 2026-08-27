using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace DotnetCqrs.WriteGuards;

/// <summary>
/// Rejects out-of-band writes on guarded (projection-owned) read-model tables.
/// PocketBase gives pocketcqrs this for free by hooking its own record-save
/// pipeline; dotnetcqrs has no such framework underneath the read models, so this
/// is enforced with plain SQLite triggers instead — a mechanism the *database*
/// applies to every connection, not application-code discipline.
///
/// The one escape hatch is a bypass flag checked via a SQL function
/// (<c>writeguard_bypass_active()</c>) registered per-connection with
/// <see cref="SqliteConnection.CreateFunction{TResult}"/>. A persistent trigger
/// cannot reference a TEMP table (SQLite rejects that at CREATE TRIGGER time), so
/// a connection-scoped SQL function is the mechanism instead: only the connection
/// that called <see cref="InstallAsync"/> has that function registered at all — any
/// other connection, including a fresh one opened against the same file, fails with
/// "no such function" the moment its own write tries to fire the trigger. That is
/// the direct analogue of pocketcqrs's per-request context marker that "never
/// crosses the HTTP/API boundary."
/// </summary>
public static class WriteGuard
{
    // Per-connection bypass depth (supports nested BeginBypassAsync scopes),
    // keyed by connection identity rather than stored on SqliteConnection itself.
    private static readonly ConditionalWeakTable<SqliteConnection, StrongBox<int>> BypassDepths = new();

    /// <summary>
    /// Installs triggers on <paramref name="tables"/> that reject INSERT/UPDATE/DELETE
    /// on <paramref name="connection"/> unless made inside a <see cref="BeginBypassAsync"/>
    /// scope on that same connection. Call once per connection that owns the tables
    /// (typically a projection, right after it creates them). Registering no tables
    /// guards nothing — an empty list is a legitimate "no projections yet" call, not
    /// a wildcard.
    /// </summary>
    public static Task InstallAsync(SqliteConnection connection, IReadOnlyList<string> tables, CancellationToken ct = default)
    {
        if (tables.Count == 0) return Task.CompletedTask;

        var depth = BypassDepths.GetOrCreateValue(connection);
        connection.CreateFunction("writeguard_bypass_active", () => depth.Value > 0);

        return InstallTriggersAsync(connection, tables, ct);
    }

    private static async Task InstallTriggersAsync(SqliteConnection connection, IReadOnlyList<string> tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS writeguard_{table}_{op.ToLowerInvariant()}
                    BEFORE {op} ON {table}
                    WHEN writeguard_bypass_active() = 0
                    BEGIN
                        SELECT RAISE(ABORT, 'direct writes to ''{table}'' are disabled; state changes must go through a command/decider');
                    END
                    """;
                await command.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>Marks writes on <paramref name="connection"/> as internal (projection)
    /// writes for the lifetime of the returned scope. Nests: writes stay allowed until
    /// every nested scope has been disposed.</summary>
    public static ValueTask<IAsyncDisposable> BeginBypassAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var depth = BypassDepths.GetOrCreateValue(connection);
        depth.Value++;
        return ValueTask.FromResult<IAsyncDisposable>(new BypassScope(depth));
    }

    private sealed class BypassScope(StrongBox<int> depth) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            depth.Value--;
            return ValueTask.CompletedTask;
        }
    }
}
