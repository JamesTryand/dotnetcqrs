# Tutorial: a design document to a running slice

[Getting started](getting-started.md) ran a hand-written domain. This walks
the other path: start from an EventModeling document, generate real C#
from it, and run the generated code — including a real collision and what
happens when the document and the generator don't perfectly agree.

## The document

[`test/DotnetCqrs.Tests/Codegen/TestData/order-fulfillment.json`](../test/DotnetCqrs.Tests/Codegen/TestData/order-fulfillment.json)
is a worked example against the [`eventmodelschema`](https://github.com/jamestryand/eventmodelschema)
schema — every slice pattern, every scenario kind. The pieces that matter
here:

- **`events.order-placed`** declares fields `orderId` (its id), `customerEmail`,
  `items` — the shape a fact about a placed order carries.
- **`commands.place-order`** declares its *own* fields, `customerId` and
  `items` — what a caller actually sends. Notice this doesn't match the
  event's fields exactly; that mismatch is the point later on.
- **`slices`** ties a screen, a command, and the event(s) it can produce
  into one vertical slice — `place-order-slice` is a `stateChange`
  pattern: command in, event out.

You don't need to read the whole file to follow along — just that a
command's declared shape and the event it produces are two separate
things, each named by the document.

## Generating code from it

`DotnetCqrs.Codegen` is a library, not a CLI — you drive it with three
calls. A throwaway console project is enough to run them:

```csharp
using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

var doc = DocumentLoader.LoadFromFile("order-fulfillment.json");
var result = DocumentMapper.Map(doc, new MappingOptions
{
    // "notify-shipping-partner" declares no `aggregate` tag in the document,
    // so mapping needs an operator override to know which aggregate it
    // belongs to -- DocumentMapper.Map throws DomainValidationException
    // without one. Every other command in this document is tagged.
    AggregateOverrides = new Dictionary<string, string> { ["notify-shipping-partner"] = "ShippingNotification" },
});

foreach (var warning in result.Report.Warnings)
    Console.WriteLine($"warning: {warning}");

foreach (var domain in result.Domains)
    foreach (var file in CSharpGenerator.Generate(domain))
        File.WriteAllText(file.Name, file.Source);
```

Running that against this document prints ten warnings (uuid fields
becoming plain `text` columns, an automation whose read model the
generated reaction doesn't consult, and so on — each one names exactly
what got simplified) and writes six files across two aggregates,
`order` and `shippingNotification`. You don't have to run this yourself to
see the result — the actual output is already committed at
[`samples/OrderFulfillmentGenerated/`](../samples/OrderFulfillmentGenerated),
built as a real project in the solution so it's proven to compile, not
just prose.

Here's the generated decider in full —
[`OrderDecider.cs`](../samples/OrderFulfillmentGenerated/OrderDecider.cs):

```csharp
public static class OrderDecider
{
    public const string Aggregate = "order";
    // ...
    public static Decider<OrderState> Create() => new()
    {
        InitialState = () => new OrderState(false, null, null, null),
        Decide = (state, cmd) =>
        {
            switch (cmd.Name)
            {
                case "PlaceOrder":
                {
                    if (state.Exists) throw new InvalidOperationException("order already exists");
                    var payload = JsonSerializer.Deserialize<OrderPlacedPayload>(cmd.Payload, JsonOptions)!;
                    return [new NewEvent(OrderEvents.OrderPlaced, JsonSerializer.Serialize(payload, JsonOptions))];
                }
                case "ShipOrder":
                {
                    if (!state.Exists) throw new InvalidOperationException("order does not exist");
                    return [new NewEvent(OrderEvents.OrderShipped, "{}")];
                }
                // ...
            }
        },
        // Evolve omitted here -- see the real file
    };
}
```

This is a real, compilable `Decider<OrderState>` — the create-guard
(`if (state.Exists) throw ...`), the existence check on `ShipOrder`, the
event names, all generated straight from the document's slices. Register
it exactly like a hand-written one:

```csharp
var store = await SqliteEventStore.OpenAsync(":memory:");
var registry = new DeciderRegistry(store);
registry.Register(OrderDecider.Aggregate, OrderDecider.Create());
```

## Running it — including the collision

```csharp
var placed = await registry.HandleAsync("order", "order-1",
    new Command("PlaceOrder", """{"customerId":"cust-1","items":[{"sku":"WIDGET-1","qty":2}]}"""));
Console.WriteLine($"PlaceOrder -> {placed[0].Type}: {placed[0].Data}");

try
{
    await registry.HandleAsync("order", "order-1",
        new Command("PlaceOrder", """{"customerId":"cust-1","items":[]}"""));
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"PlaceOrder again -> rejected: {ex.Message}");
}

var shipped = await registry.HandleAsync("order", "order-1", new Command("ShipOrder", "{}"));
Console.WriteLine($"ShipOrder -> {shipped[0].Type}: {shipped[0].Data}");
```

Real output:

```
PlaceOrder -> OrderPlaced: {"orderId":null,"customerEmail":null,"items":[{"sku":"WIDGET-1","qty":2}]}
PlaceOrder again -> rejected: order already exists
ShipOrder -> OrderShipped: {}
```

The collision is exactly what you'd want: the second `PlaceOrder` for the
same id is refused, no second `OrderPlaced` in the log, same shape as the
rejection you saw against the hand-written domain in
[Getting started](getting-started.md#the-rejection).

## Where the generated code needs your judgment

Look again at that first line: `orderId` and `customerEmail` came back
**null**. This is not a bug in the demo — it's the document's own field
mismatch surfacing for real. `OrderDecider`'s `PlaceOrder` case
deserializes the incoming command payload (which has `customerId`) into a
type shaped by the *event's* declared fields (`orderId`, `customerEmail`)
— it's reshaping, not renaming, and nothing in the document told it
`customerId` should become `customerEmail`. Every generator doc comment in
this codebase says a version of the same thing:

> THE SHAPE IS RIGHT, THE RULES ARE YOURS.

The generated code is a correct, compiling starting point — registration,
dispatch, the create-guard, the event log — never a finished domain. Fixing
this one is a few lines: hand-edit `OrderDecider.cs`'s `PlaceOrder` case to
read `customerId`/pull an email from wherever it actually comes from,
same as you'd fill in any other stub. The generator's job stopped at "this
compiles and the wiring is correct"; the mapping from a real command to a
real event's fields is domain knowledge only you have.

The same caveat applies to generated projections — a read-model column
only gets populated if some event literally carries that JSON key. See
`samples/OrderFulfillmentGenerated/OrderSummaryProjection.cs` for a
concrete case: its `status` column is never set, because no event in this
document carries a literal `status` field. That's not a bug either — it's
the same "shape is right, rules are yours" boundary, one level up.

## Checking a document's own scenarios automatically

You don't have to write demo code like the above by hand to check a
document's claims — `order-fulfillment.json` declares its own
given/when/then scenarios, and `ScenarioVerifier.VerifyAsync(doc, result, ...)`
runs every one of them against the generated code for you (compiling a
small fixed harness alongside it, same as this tutorial's demo project
does by hand). `test/DotnetCqrs.Tests/Codegen/ScenarioVerifierTests.cs` is
a full worked call site. It's how the `status`-column gap above was
originally found: the document's own view scenario expects
`"status": "placed"`, the verifier runs it against the real generated
projection, and reports the mismatch instead of a passing test lying to
you.

## Where next

- [Concepts](concepts.md) — the vocabulary this tutorial assumes (decider,
  aggregate, projection, reactor).
- [Getting started](getting-started.md) — the same domain shape, hand-written
  and running over HTTP.
- `src/DotnetCqrs.Codegen/` — `DocumentLoader`, `DocumentMapper`,
  `CSharpGenerator`, `ScenarioVerifier`, if you want to go past what this
  tutorial called.
- [`eventmodelschema`](https://github.com/jamestryand/eventmodelschema) — the
  schema itself, for writing your own documents.
