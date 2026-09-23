# Tutorial: a design document to a running slice

[Getting started](getting-started.md) ran a hand-written domain. This walks
the other path: start from an EventModeling document, generate real C#
from it, and run the generated code — including a real collision, what
happens when the document and the generator don't perfectly agree, and
what the generator does with personal data.

## The document

[`test/DotnetCqrs.Tests/Codegen/TestData/order-fulfillment.json`](../test/DotnetCqrs.Tests/Codegen/TestData/order-fulfillment.json)
is a worked example against the [`eventmodelschema`](https://github.com/jamestryand/eventmodelschema)
schema — every slice pattern, every scenario kind. The pieces that matter
here:

- **`events.order-placed`** declares fields `orderId` (its id), `customerId`,
  `customerEmail` and `items` — the shape a fact about a placed order carries.
  `customerEmail` is marked `"pii": true` with `"piiSubject": "customerId"`:
  it is personal data, and it belongs to the person `customerId` names.
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

`DotnetCqrs.Codegen` is a library you can drive with three calls. A
throwaway console project is enough to run them:

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

The same thing is available from the command line, through
`DotnetCqrs.Codegen.Cli` (tool name `dotnetcqrs-codegen`):

```sh
dotnet run --project src/DotnetCqrs.Codegen.Cli -- generate \
  --input order-fulfillment.json --output generated \
  --aggregate-override notify-shipping-partner=ShippingNotification
```

Either way, this document produces twelve warnings (uuid fields becoming
plain `text` columns, an automation whose read model the generated
reaction doesn't consult, and so on — each one names exactly what got
simplified) and six files across two aggregates, `order` and
`shippingNotification`. You don't have to run this yourself to see the
result — the actual output is already committed at
[`samples/OrderFulfillmentGenerated/`](../samples/OrderFulfillmentGenerated),
built as a real project in the solution so it's proven to compile, not
just prose.

Here's the heart of the generated decider —
[`OrderDecider.cs`](../samples/OrderFulfillmentGenerated/OrderDecider.cs):

```csharp
public sealed record OrderState(bool Exists, string? OrderId, string? CustomerId, Pii<string>? CustomerEmail, JsonElement? Items);

public static class OrderDecider
{
    public const string Aggregate = "order";
    // ...
    public static Decider<OrderState> Create() => new()
    {
        InitialState = () => new OrderState(false, null, null, null, null),
        Decide = (state, cmd) =>
        {
            switch (cmd.Name)
            {
                case "PlaceOrder":
                {
                    if (state.Exists) throw new InvalidOperationException("order already exists");
                    var payload = JsonSerializer.Deserialize<OrderPlacedPayload>(cmd.Payload, JsonOptions)!;
                    return [NewEvent.Of(OrderEvents.OrderPlaced, payload)];
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

    private sealed record OrderPlacedPayload(string OrderId, string CustomerId, Pii<string>? CustomerEmail, JsonElement Items);

    public sealed class PiiProtector(IKmsClient kms, ISubjectStatus? subjects = null, PiiRevealCache? cache = null) : IPiiProtector
    {
        // encrypts CustomerEmail under CustomerId's key after Decide; see the real file
    }
}
```

This is a real, compilable `Decider<OrderState>` — the create-guard
(`if (state.Exists) throw ...`), the existence check on `ShipOrder`, the
event names, all generated straight from the document's slices. Because
`customerEmail` is personal data, it is typed `Pii<string>` rather than
`string`, and the decider comes with a `PiiProtector`. Register the two
together:

```csharp
var store = await SqliteEventStore.OpenAsync(":memory:");
var kms = new InMemoryKmsClient(); // a stand-in: see "Personal data" below
var registry = new DeciderRegistry(store);
registry.Register(OrderDecider.Aggregate, OrderDecider.Create(),
    new OrderDecider.PiiProtector(kms, new SubjectStatus(store)));
registry.RegisterDataSubjects();
```

A decider without personal data registers exactly like a hand-written one:
`registry.Register(aggregate, decider)`.

## Running it — including the collision

```csharp
var placed = await registry.HandleAsync("order", "order-1",
    new Command("PlaceOrder", """{"customerId":"cust-1","customerEmail":"alice@example.com","items":[{"sku":"WIDGET-1","qty":2}]}"""));
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
PlaceOrder -> OrderPlaced: {"orderId":null,"customerId":"cust-1","customerEmail":{"$pii":{"s":"cust-1","c":"inmem:cust-1:ImFsaWNlQGV4YW1wbGUuY29tIg=="}},"items":[{"sku":"WIDGET-1","qty":2}]}
PlaceOrder again -> rejected: order already exists
ShipOrder -> OrderShipped: {}
```

The collision is exactly what you'd want: the second `PlaceOrder` for the
same id is refused, no second `OrderPlaced` in the log, same shape as the
rejection you saw against the hand-written domain in
[Getting started](getting-started.md#the-rejection).

The email did not reach the log as plaintext. The event carries a
`{"$pii":…}` envelope instead: the subject it belongs to (`s`) and a
ciphertext (`c`). That's the protector at work, and the next-but-one
section covers it.

## Where the generated code needs your judgment

Look again at that first line: `orderId` came back **null**. This is not a
bug in the demo — it's the document's own field mismatch surfacing for
real. `OrderDecider`'s `PlaceOrder` case deserializes the incoming command
payload into a type shaped by the *event's* declared fields. `customerId`
and `items` are in both, so they carry through. `orderId` is the event's id
field, and no command field supplies it. The stream id (`order-1`) is the
obvious source, but nothing in the document says so.

`customerEmail` shows the same reshaping from the other side. The
document's `place-order` command doesn't declare it at all, yet it reached
the event, because the caller sent it and the event's shape has a slot for
it. Leave it out and the event records `null`. Every generator doc comment
in this codebase says a version of the same thing:

> THE SHAPE IS RIGHT, THE RULES ARE YOURS.

The generated code is a correct, compiling starting point — registration,
dispatch, the create-guard, encryption of personal data, the event log —
never a finished domain. Fixing this one is a few lines: hand-edit
`OrderDecider.cs`'s `PlaceOrder` case to set `OrderId` from the stream id
and to decide where the email really comes from, same as you'd fill in any
other stub. The generator's job stopped at "this compiles and the wiring is
correct"; the mapping from a real command to a real event's fields is
domain knowledge only you have.

The same caveat applies to generated projections — a read-model column
only gets populated if some event literally carries that JSON key. See
`samples/OrderFulfillmentGenerated/OrderSummaryProjection.cs` for a
concrete case: its `status` column is never set, because no event in this
document carries a literal `status` field. That's not a bug either — it's
the same "shape is right, rules are yours" boundary, one level up.

## Personal data

A field marked `"pii": true` is stored so that it can later be **erased**
without editing the log. The mechanism is crypto-shredding: each value is
encrypted under a key belonging to one person (the *data subject*, named by
the field's `piiSubject`), and erasing that person destroys their key. The
events stay in the log, but their personal data can never be read again.

What the generator does with a pii field:

- **The type is `Pii<T>`, not `T`.** A value arriving in a command is
  plaintext, and `Decide` can read it (to validate it, say). After `Decide`,
  the generated `PiiProtector` encrypts it under the subject's key, before
  anything is appended. A `Pii<T>` that was never encrypted refuses to
  serialize, so forgetting the protector fails closed rather than leaking.
  Without one, the same command fails:

  ```
  no protector -> InvalidOperationException: Refusing to serialise an unencrypted Pii<String> -- the aggregate has no IPiiProtector registered, or Decide produced a fresh value the protector did not encrypt.
  ```

  A command that carries no personal data (no email) still succeeds, since
  there is nothing to protect.

- **Stored values are revealed only when something needs them.** When
  `Decide` reads a stored `Pii<T>`, the registry reveals it (decrypting every
  pii value in the state, one batched call per person) and runs `Decide`
  again. A
  decision that never looks at personal data costs nothing extra.
- **Read models keep the envelope, not the plaintext.** A projection copies
  the `{"$pii":…}` envelope into its column. The generated query route
  reveals the page's pii cells just before responding, one batched call per
  person, with warm values served from an in-process cache. An erased
  person's cell comes back as `{"$redacted":true}`, which callers can tell
  apart from a value that was never set. A pii column can't be used as a
  query filter (the route answers 400), because encryption is randomized and
  equality can never match.
- **Erasure is a command.** `registry.RegisterDataSubjects()` adds a
  built-in `dataSubject` aggregate. `EraseSubject` on it records
  `SubjectErased`, and a `SubjectKeyDestroyer` consumer destroys the key when
  that event lands:

  ```csharp
  await registry.HandleAsync(DataSubject.Aggregate, "cust-1", new Command(DataSubject.EraseSubjectCommand, "{}"));
  var engine = new ConsumerEngine(store, store);
  engine.Register(new SubjectKeyDestroyer(kms));
  await engine.RunOnceAsync();
  ```

  Revealing the stored email before and after, then trying to store a new
  one for the same person:

  ```
  before erasure: Known alice@example.com
  after erasure: Redacted
  PlaceOrder for cust-1 again -> refused: data subject 'cust-1' has been erased; a returning subject needs a new id, not this one.
  ```

- **Erasure is terminal.** A person who comes back is a *new* subject with a
  new id and a new key. So subject ids must be opaque and never reused: never
  an email address or anything else derived from personal data. The mapper
  warns when a `piiSubject` field's name looks like one.

`InMemoryKmsClient`, used above, is **not encryption** — its "ciphertext" is
the plaintext in base64, as the output shows. It exists for tests and for
the scenario verifier. A real host uses `KmsClient` against the
key-management facade, which holds each subject's key in Vault and never
lets it out.

## Searching: `match` filters

A read model declares which fields can be searched, and how, in its
`filters` (schema 3.1.0):

```json
"filters": [
  {"param": "nameSearch", "field": "name", "kind": "match", "mode": "contains", "normalize": "personName"},
  {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"}
]
```

`mode` is `exact`, `prefix` or `contains`. `normalize` says how both the
stored value and the search term are cleaned before comparing: `caseFold`
(the default) ignores case and surrounding space, `personName` also ignores
accents and repeated spaces, `email` is `caseFold`, `phone` keeps a leading
`+` and the digits, and `none` compares exactly. A query then passes the
term as that param: `GET /api/query/customers?nameSearch=nunez` finds
"José Núñez".

How the generator serves it depends on the field:

- **An ordinary field** gets a normalized copy (a *shadow column*) kept
  beside it by the projection, searched with plain SQL. It never appears in
  query responses.
- **A personal field** (`pii`) only supports `contains` for now. Its values
  are ciphertext, so the generator builds a separate search index of
  normalized plaintext in its own file, `search.db`. That file is deleted
  from when a person is erased, must be left out of backups, and is rebuilt
  from the log whenever it's missing (it keeps its own position inside
  itself, so it can never be silently half-built). The mapping report flags
  every such index. `exact` and `prefix` on a personal field are refused at
  generation time: they will use keyed hashes from the key service, which
  isn't available yet.

A search returns the matching rows with their personal values revealed as
usual. Searches and scenario checks run the same SQL.

## Running it as a host

`generate --host` writes a complete runnable ASP.NET Core project for the
whole document instead of just the domain code: every decider registered,
every projection running, the command gateway
(`POST /api/cqrs/{aggregate}/{id}/{command}`) and one query route per read
model (`GET /api/query/{collection}`):

```sh
dotnet run --project src/DotnetCqrs.Codegen.Cli -- generate \
  --input order-fulfillment.json --output OrderFulfillment --host \
  --dotnetcqrs-project src/DotnetCqrs/DotnetCqrs.csproj \
  --aggregate-override notify-shipping-partner=ShippingNotification
```

When the document has personal data, the host needs the key-management
facade. It reads the facade's base URL from `KMS_FACADE_URL`, and refuses to
start without it rather than failing on the first request:

```sh
KMS_FACADE_URL=https://kms.example.internal/ dotnet run --project OrderFulfillment
```

It then wires everything from the previous section: each protector (with the
erased-subject guard and the reveal cache), the `dataSubject` aggregate, the
key destroyer and the cache evictor. Erasing a person is an ordinary command
through the gateway: `POST /api/cqrs/dataSubject/{subjectId}/EraseSubject`.
Who may call it is your authorization decision, like every other command;
the generated host is unauthenticated until you add a scheme.

## Checking a document's own scenarios automatically

You don't have to write demo code like the above by hand to check a
document's claims — `order-fulfillment.json` declares its own
given/when/then scenarios, and `ScenarioVerifier.VerifyAsync(doc, result, ...)`
runs every one of them against the generated code for you (compiling a
small fixed harness alongside it, same as this tutorial's demo project
does by hand). From the command line, that's `dotnetcqrs-codegen verify`;
a generated host has the same check built in as `dotnet run -- --verify`.
`test/DotnetCqrs.Tests/Codegen/ScenarioVerifierTests.cs` is a full worked
call site. It's how the `status`-column gap above was originally found:
the document's own view scenario expects `"status": "placed"`, the verifier
runs it against the real generated projection, and reports the mismatch
instead of a passing test lying to you.

Scenarios state personal data in plaintext. The verifier encrypts it on the
way in and reveals it on the way out, using `InMemoryKmsClient`, so a view
scenario is compared with what a caller would actually see. No facade is
needed to verify.

## Where next

- [Concepts](concepts.md) — the vocabulary this tutorial assumes (decider,
  aggregate, projection, reactor, crypto-shredding).
- [Getting started](getting-started.md) — the same domain shape, hand-written
  and running over HTTP.
- `src/DotnetCqrs.Codegen/` — `DocumentLoader`, `DocumentMapper`,
  `CSharpGenerator`, `HostProjectGenerator`, `ScenarioVerifier`, if you want to
  go past what this tutorial called.
- `src/DotnetCqrs.Crypto/` — `Pii<T>`, `KmsClient`, the reveal buffer and
  cache, and the data-subject lifecycle.
- [`eventmodelschema`](https://github.com/jamestryand/eventmodelschema) — the
  schema itself, for writing your own documents.
