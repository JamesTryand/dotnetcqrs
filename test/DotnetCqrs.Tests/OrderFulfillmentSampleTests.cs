using System.Data.Common;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.ReadModels;
using DotnetCqrs.Reactors;
using Microsoft.Data.Sqlite;
using OrderFulfillment;

namespace DotnetCqrs.Tests;

/// <summary>
/// The headless, assertable counterpart to samples/OrderFulfillment/Program.cs's
/// interactive walkthrough: the same real domain code (Orders/Tasks deciders,
/// OrdersProjection/TasksProjection, FulfillmentReactor), wired the same way,
/// verified with assertions instead of console output. This is Milestone 8's
/// "one hand-built example system end to end."
/// </summary>
public class OrderFulfillmentSampleTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public required SqliteEventStore EventStore { get; init; }
        public required IReadModelStore ReadModel { get; init; }
        public required DeciderRegistry Registry { get; init; }
        public required ConsumerEngine Engine { get; init; }

        public static async Task<Harness> BuildAsync()
        {
            var eventStore = await SqliteEventStore.OpenAsync(":memory:");
            var readModelDb = await SqliteReadModelStore.OpenAsync(":memory:");

            var registry = new DeciderRegistry(eventStore);
            registry.Register(Orders.Aggregate, Orders.Decider());
            registry.Register(Tasks.Aggregate, Tasks.Decider());

            var ordersProjection = new OrdersProjection(readModelDb);
            var tasksProjection = new TasksProjection(readModelDb);
            await ordersProjection.InitAsync();
            await tasksProjection.InitAsync();
            await readModelDb.InstallWriteGuardAsync([.. ordersProjection.Tables, .. tasksProjection.Tables]);

            var engine = new ConsumerEngine(eventStore, eventStore);
            engine.Register(ordersProjection);
            engine.Register(tasksProjection);
            engine.Register(new ReactorConsumer(new FulfillmentReactor(), registry));

            return new Harness { EventStore = eventStore, ReadModel = readModelDb, Registry = registry, Engine = engine };
        }

        public async ValueTask DisposeAsync()
        {
            await EventStore.DisposeAsync();
            await ReadModel.DisposeAsync();
        }
    }

    private static async Task<(string? Title, bool Confirmed)?> FindOrderAsync(DbConnection db, string orderId)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT title, confirmed FROM orders WHERE order_id = @id";
        command.AddParam("@id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetInt64(1) != 0);
    }

    private static async Task<(string? Title, bool Completed)?> FindTaskAsync(DbConnection db, string taskId)
    {
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT title, completed FROM tasks WHERE task_id = @id";
        command.AddParam("@id", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetInt64(1) != 0);
    }

    [Fact]
    public async Task Placing_and_confirming_an_order_fulfills_it_through_the_full_write_read_reactor_loop()
    {
        await using var system = await Harness.BuildAsync();

        await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("PlaceOrder", """{"title":"widget x 3"}"""));
        await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("ConfirmOrder", "{}"));

        // First pass: projections catch up on OrderPlaced/OrderConfirmed, and the
        // reactor reacts to OrderConfirmed by appending TaskCreated -- but the tasks
        // projection already ran its own catch-up earlier in this same pass, so it
        // hasn't seen that new event yet (see Program.cs's comment on this).
        await system.Engine.RunOnceAsync();
        var order = await FindOrderAsync(system.ReadModel.Connection, "o1");
        Assert.Equal(("widget x 3", true), order);
        Assert.Null(await FindTaskAsync(system.ReadModel.Connection, "fulfill-o1"));

        // Second pass: the tasks projection catches up on the reactor's TaskCreated.
        await system.Engine.RunOnceAsync();
        var task = await FindTaskAsync(system.ReadModel.Connection, "fulfill-o1");
        Assert.Equal(("fulfil order o1", false), task);
    }

    [Fact]
    public async Task The_write_guard_actually_blocks_a_direct_write_to_a_projection_owned_table()
    {
        await using var system = await Harness.BuildAsync();
        await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("PlaceOrder", """{"title":"x"}"""));
        await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("ConfirmOrder", "{}"));
        await system.Engine.RunOnceAsync();
        await system.Engine.RunOnceAsync();

        await using var direct = system.ReadModel.Connection.CreateCommand();
        direct.CommandText = "UPDATE tasks SET completed = 1 WHERE task_id = 'fulfill-o1'";
        await Assert.ThrowsAsync<SqliteException>(() => direct.ExecuteNonQueryAsync());

        var task = await FindTaskAsync(system.ReadModel.Connection, "fulfill-o1");
        Assert.False(task!.Value.Completed); // the blocked write did not sneak through
    }

    [Fact]
    public async Task Replaying_ConfirmOrder_via_the_reactor_is_idempotent()
    {
        await using var system = await Harness.BuildAsync();
        await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("PlaceOrder", """{"title":"x"}"""));
        var confirmed = (await system.Registry.HandleAsync(Orders.Aggregate, "o1", new Command("ConfirmOrder", "{}"))).Single();

        var reactor = new ReactorConsumer(new FulfillmentReactor(), system.Registry);
        await reactor.ApplyAsync(confirmed, CancellationToken.None);
        await reactor.ApplyAsync(confirmed, CancellationToken.None); // redelivered

        Assert.Single(await system.EventStore.LoadStreamAsync(Tasks.Aggregate, "fulfill-o1"));
    }
}
