using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace Generated.Order;

/// <summary>Projects "order" events into the "pendingShipments" table, one row
/// per order stream keyed by the aggregate id -- a generic field-merge. Port
/// your own per-event rules once they've settled.</summary>
public sealed class PendingShipmentsProjection(IReadModelStore store) : IProjection
{
    public string Name => "pendingShipments";
    public IReadOnlyList<string> Tables => ["pendingShipments"];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pendingShipments (
                order_id TEXT PRIMARY KEY
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Type is not ("OrderPlaced")) return;

        // The write-guard denies direct writes on every connection but the one that called
        // IReadModelStore.InstallWriteGuardAsync -- this IS that connection, but the guard
        // still fires unless a bypass scope is open, so this projection's own writes need one too.
        await using var bypass = await store.BeginBypassAsync(ct);

        await using (var insert = store.Connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO pendingShipments (order_id) VALUES (@id)
                ON CONFLICT (order_id) DO NOTHING
                """;
            insert.AddParam("@id", ev.AggregateId);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
