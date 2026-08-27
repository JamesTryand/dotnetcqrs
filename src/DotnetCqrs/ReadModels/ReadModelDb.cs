using Microsoft.Data.Sqlite;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// Opens a SQLite connection for a read-model database, with the same operational
/// pragmas as the event store (WAL, busy timeout). Schema is owned entirely by
/// whichever projection(s) target this database — nothing is created here, and
/// nothing here is ever the source of truth (see the concepts doc's "Storage":
/// read models are ordinary, freely rewritable/rebuildable tables).
/// </summary>
public static class ReadModelDb
{
    public static async Task<SqliteConnection> OpenAsync(string path, CancellationToken ct = default)
    {
        // Pooling=False: see SqliteEventStore.OpenAsync's identical comment -- a
        // pooled connection keeps the OS file handle open past DisposeAsync.
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(ct);

        return connection;
    }
}
