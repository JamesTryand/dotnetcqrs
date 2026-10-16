using System.Text.Json;
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Mapping;
using DotnetCqrs.Codegen.Verification;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Verifying a document against a host's own deciders: command scenarios run
/// in-process against what the host registers, not generated stand-ins, so the values a scenario
/// declares check the real arithmetic. A generated stand-in only copies the command's payload, so
/// it can never compute a total.
/// </summary>
public class HostVerificationTests
{
    private const string Json = """
        {
          "eventModelingSchemaVersion": "3.10.0", "id": "host-verify-test", "name": "Host Verify Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {"order-placed": {"name": "Order Placed", "swimlaneId": "s", "aggregate": "Order", "fields": [
            {"name": "orderId", "type": "string"}, {"name": "total", "type": "decimal"},
            {"name": "placedBy", "type": "string"}, {"name": "placedAt", "type": "dateTime"}]}},
          "commands": {"place-order": {"name": "Place Order", "aggregate": "Order", "fields": [
            {"name": "quantity", "type": "integer"}, {"name": "price", "type": "decimal"}]}},
          "screens": {"scr": {"name": "Screen"}},
          "slices": [{
            "id": "place-order-slice", "name": "Place Order", "pattern": "stateChange",
            "swimlaneId": "s", "status": "created",
            "screenId": "scr", "commandId": "place-order", "eventIds": ["order-placed"],
            "scenarios": [
              {
                "id": "total-is-quantity-times-price", "name": "The total is the quantity times the price", "kind": "stateChange",
                "given": [], "when": {"commandId": "place-order", "data": {"quantity": 3, "price": 10.5},
                  "actor": "m1", "at": "2026-10-01T09:00:00Z"},
                "then": {"events": [{"eventId": "order-placed",
                  "data": {"total": 31.5, "placedBy": "m1", "placedAt": "2026-10-01T09:00:00Z"}}]}
              },
              {
                "id": "a-wrong-total", "name": "A worked example with the wrong total", "kind": "stateChange",
                "given": [], "when": {"commandId": "place-order", "data": {"quantity": 3, "price": 10.5}},
                "then": {"events": [{"eventId": "order-placed", "data": {"total": 30}}]}
              },
              {
                "id": "no-free-orders", "name": "A free order is refused", "kind": "error",
                "given": [], "when": {"commandId": "place-order", "data": {"quantity": 3, "price": 0}},
                "then": {"error": {"message": "an order must cost something"}}
              }
            ]
          }]
        }
        """;

    private sealed record OrderState(bool Exists);

    /// <summary>The host's own decider: it computes the total and stamps who and when, which no
    /// generated stand-in does.</summary>
    private static void RegisterHostDeciders(DeciderRegistry registry, DotnetCqrs.Crypto.IKmsClient kms) =>
        registry.Register("order", new Decider<OrderState>
        {
            InitialState = () => new OrderState(false),
            Decide = (state, cmd) =>
            {
                using var payload = JsonDocument.Parse(cmd.Payload);
                var quantity = payload.RootElement.GetProperty("quantity").GetDouble();
                var price = payload.RootElement.GetProperty("price").GetDouble();
                if (price == 0) throw new InvalidOperationException("an order must cost something");
                return [new NewEvent("OrderPlaced", JsonSerializer.Serialize(new
                {
                    orderId = "o1", total = quantity * price, placedBy = cmd.Actor, placedAt = cmd.Now,
                }))];
            },
            Evolve = (state, _) => state with { Exists = true },
        });

    private static async Task<IReadOnlyList<ScenarioResult>> VerifyAsync(Action<DeciderRegistry, DotnetCqrs.Crypto.IKmsClient> register)
    {
        var doc = DocumentLoader.Parse(Json);
        return await ScenarioVerifier.VerifyAsync(doc, DocumentMapper.Map(doc), new HostDeciders(register));
    }

    [Fact]
    public async Task A_scenarios_values_are_checked_against_the_hosts_own_arithmetic()
    {
        var results = await VerifyAsync(RegisterHostDeciders);

        var right = results.Single(r => r.ScenarioId == "total-is-quantity-times-price");
        Assert.True(right.Passed, right.Detail);
    }

    [Fact]
    public async Task A_wrong_value_fails_and_names_both_values()
    {
        var results = await VerifyAsync(RegisterHostDeciders);

        var wrong = results.Single(r => r.ScenarioId == "a-wrong-total");
        Assert.False(wrong.Passed);
        Assert.Contains("total expected 30, got 31.5", wrong.Detail);
    }

    [Fact]
    public async Task A_hosts_refusal_is_compared_by_its_message()
    {
        var results = await VerifyAsync(RegisterHostDeciders);

        var refused = results.Single(r => r.ScenarioId == "no-free-orders");
        Assert.True(refused.Passed, refused.Detail);
    }

    [Fact]
    public async Task A_host_that_registers_no_decider_for_the_aggregate_fails_every_scenario_visibly()
    {
        var results = await VerifyAsync((_, _) => { });

        Assert.All(results, r => Assert.False(r.Passed, r.ScenarioId));
    }
}
