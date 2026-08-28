using System.Text.Json;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace OrderFulfillment;

/// <summary>Folds order events into the "orders" read-model table.</summary>
public sealed class OrdersProjection(IReadModelStore store) : IProjection
{
    public string Name => "orders";
    public IReadOnlyList<string> Tables => ["orders"];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS orders (
                order_id  TEXT PRIMARY KEY,
                title     TEXT NOT NULL,
                confirmed INTEGER NOT NULL DEFAULT 0
            )
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        // The write-guard denies direct writes on every connection but the one that
        // called IReadModelStore.InstallWriteGuardAsync -- this IS that connection, but
        // the guard still fires unless a bypass scope is open, so the projection's own
        // writes need one too.
        await using var bypass = await store.BeginBypassAsync(ct);

        switch (ev.Type)
        {
            case "OrderPlaced":
                var placed = JsonSerializer.Deserialize<JsonElement>(ev.Data);
                await using (var command = store.Connection.CreateCommand())
                {
                    command.CommandText = """
                        INSERT INTO orders (order_id, title, confirmed) VALUES (@id, @title, 0)
                        ON CONFLICT (order_id) DO NOTHING
                        """;
                    command.AddParam("@id", ev.AggregateId);
                    command.AddParam("@title", placed.GetProperty("title").GetString());
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;

            case "OrderConfirmed":
                await using (var command = store.Connection.CreateCommand())
                {
                    command.CommandText = "UPDATE orders SET confirmed = 1 WHERE order_id = @id";
                    command.AddParam("@id", ev.AggregateId);
                    await command.ExecuteNonQueryAsync(ct);
                }
                break;
        }
    }
}
