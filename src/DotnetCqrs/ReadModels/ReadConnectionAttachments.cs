using System.Data.Common;
using System.Runtime.CompilerServices;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// The databases a host has attached to a read-model connection (<see cref="SqliteSearchIndexStore.AttachAsync"/>),
/// remembered per connection so <see cref="SqliteReadModelStore"/> can attach the same ones to the connections it
/// reads on: an ATTACH exists only on the connection it was made on. Keyed by the connection object, so existing hosts
/// that call <c>AttachAsync(store.Connection, path)</c> need no change.
/// </summary>
internal static class ReadConnectionAttachments
{
    private static readonly ConditionalWeakTable<DbConnection, List<(string Schema, string Path)>> Attached = new();

    public static void Record(DbConnection connection, string schema, string path)
    {
        var list = Attached.GetOrCreateValue(connection);
        lock (list) list.Add((schema, path));
    }

    /// <summary>A snapshot of what has been attached so far, in order. Only ever grows.</summary>
    public static IReadOnlyList<(string Schema, string Path)> For(DbConnection connection)
    {
        if (!Attached.TryGetValue(connection, out var list)) return [];
        lock (list) return [.. list];
    }
}
