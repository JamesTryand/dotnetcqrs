using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace Generated.Order;

/// <summary>Projects "order" events into the "orderSummary" table, one row
/// per order stream keyed by the aggregate id -- a generic field-merge. Port
/// your own per-event rules once they've settled.</summary>
public sealed class OrderSummaryProjection(IReadModelStore store) : IProjection
{
    public string Name => "orderSummary";
    public IReadOnlyList<string> Tables => ["orderSummary"];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS orderSummary (
                order_id TEXT PRIMARY KEY,
                status TEXT
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Type is not ("OrderPlaced" or "OrderShipped")) return;

        // The write-guard denies direct writes on every connection but the one that called
        // IReadModelStore.InstallWriteGuardAsync -- this IS that connection, but the guard
        // still fires unless a bypass scope is open, so this projection's own writes need one too.
        await using var bypass = await store.BeginBypassAsync(ct);

        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];

        await using (var insert = store.Connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO orderSummary (order_id) VALUES (@id)
                ON CONFLICT (order_id) DO NOTHING
                """;
            insert.AddParam("@id", ev.AggregateId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var setClauses = new List<string>();
        await using var update = store.Connection.CreateCommand();
        update.AddParam("@id", ev.AggregateId);
        if (data.TryGetValue("status", out var statusValue))
        {
            setClauses.Add("status = @status");
            update.AddParam("@status", statusValue.GetString());
        }
        if (setClauses.Count > 0)
        {
            update.CommandText = "UPDATE orderSummary SET " + string.Join(", ", setClauses) + " WHERE order_id = @id";
            await update.ExecuteNonQueryAsync(ct);
        }
    }
}
