using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Schema 3.17.0 <c>readModel.projection: "hand"</c>: a read model built by a hand-written projection (a join no
/// declaration expresses). The model-only check reports its view scenarios as HAND rather than failing them; a host
/// check runs the host's own projection (<see cref="HostDeciders.Projections"/>), so the scenarios prove the real code.
/// A known failure in both checks until then. The example: an order
/// for 14 October, whose dispatch has closed, is late; one for 15 October is not.
/// </summary>
[Trait("Category", "Compiles")]
public class HandProjectionTests
{
    private const int VerifyTimeoutMs = 120000;
    private const string ScenarioId = "an-order-after-dispatch-closed-is-late";

    private static string TestDataPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Codegen", "TestData", fileName);

    private static DotnetCqrs.Codegen.Model.Document Example(Action<JsonNode>? change = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(TestDataPath("hand-projection.json")))!;
        change?.Invoke(node);
        return DocumentLoader.Parse(node.ToJsonString());
    }

    private static HostDeciders Host(Type? projection) => new((_, _) => { })
    {
        Projections = projection is null ? new Dictionary<string, Type>() : new Dictionary<string, Type> { ["late-orders"] = projection },
    };

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_model_only_check_reports_a_hand_read_models_view_as_hand_not_failed()
    {
        var doc = Example();

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc)));

        Assert.True(result.ByHand, result.Detail);
        Assert.False(result.Skipped);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task Without_the_mark_the_generated_stand_in_fails_it()
    {
        // The stand-in is one row per stream: it can't join, so the scenario fails. That is what the mark is for.
        var doc = Example(n => n["readModels"]!["late-orders"]!.AsObject().Remove("projection"));

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc)));

        Assert.False(result.Passed || result.ByHand, result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task The_host_check_runs_the_hosts_own_projection()
    {
        var doc = Example();

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), Host(typeof(LateOrdersProjection))));

        Assert.True(result.Passed, result.Detail);
        Assert.False(result.ByHand);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_wrong_host_projection_fails_it()
    {
        var doc = Example();

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), Host(typeof(EveryOrderProjection))));

        Assert.False(result.Passed, result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_host_that_gives_no_projection_for_a_hand_read_model_fails_it_and_says_why()
    {
        var doc = Example();

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), Host(null)));

        Assert.False(result.Passed || result.ByHand || result.Skipped);
        Assert.Contains("hand", result.Detail);
        Assert.Contains("late-orders", result.Detail);
    }

    [Fact(Timeout = VerifyTimeoutMs)]
    public async Task A_host_projection_is_used_for_a_generated_read_model_too()
    {
        // A host may hand-finish a generated projection; given one, the host check runs it.
        var doc = Example(n => n["readModels"]!["late-orders"]!.AsObject().Remove("projection"));

        var result = Assert.Single(await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), Host(typeof(LateOrdersProjection))));

        Assert.True(result.Passed, result.Detail);
    }

    /// <summary>The example's hand projection: an order is late when its day's dispatch has closed. Keeps its own
    /// tables of closed days and orders, and recomputes the list after each event.</summary>
    public class LateOrdersProjection(IReadModelStore store) : IProjection
    {
        public string Name => "lateOrders";
        public IReadOnlyList<string> Tables => ["lateOrders", "late_orders_closed", "late_orders_orders"];

        protected virtual string ListSql =>
            "INSERT INTO lateOrders (order_id, day) SELECT o.order_id, o.day FROM late_orders_orders o JOIN late_orders_closed c ON c.day = o.day";

        public async Task InitAsync(CancellationToken ct = default)
        {
            foreach (var sql in new[]
            {
                "CREATE TABLE IF NOT EXISTS lateOrders (order_id TEXT PRIMARY KEY, day TEXT)",
                "CREATE TABLE IF NOT EXISTS late_orders_closed (day TEXT PRIMARY KEY)",
                "CREATE TABLE IF NOT EXISTS late_orders_orders (order_id TEXT PRIMARY KEY, day TEXT)",
            })
                await ExecAsync(sql, ct);
        }

        public async Task ApplyAsync(Event ev, CancellationToken ct)
        {
            if (ev.Type is not ("OrderPlaced" or "DispatchClosed")) return;
            await using var bypass = await store.BeginBypassAsync(ct);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ev.Data) ?? [];
            var day = data["day"].GetString();
            if (ev.Type == "DispatchClosed")
                await ExecAsync("INSERT INTO late_orders_closed (day) VALUES (@a) ON CONFLICT (day) DO NOTHING", ct, day);
            else
                await ExecAsync("INSERT INTO late_orders_orders (order_id, day) VALUES (@a, @b) ON CONFLICT (order_id) DO NOTHING", ct, data["orderId"].GetString(), day);
            await ExecAsync("DELETE FROM lateOrders", ct);
            await ExecAsync(ListSql, ct);
        }

        private async Task ExecAsync(string sql, CancellationToken ct, string? a = null, string? b = null)
        {
            await using var command = store.Connection.CreateCommand();
            command.CommandText = sql;
            if (a is not null) command.AddParam("@a", a);
            if (b is not null) command.AddParam("@b", b);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>A wrong one: lists every order, late or not.</summary>
    public sealed class EveryOrderProjection(IReadModelStore store) : LateOrdersProjection(store)
    {
        protected override string ListSql => "INSERT INTO lateOrders (order_id, day) SELECT order_id, day FROM late_orders_orders";
    }
}
