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

Either way, this document produces fourteen warnings (uuid fields becoming
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
                    if (state.Exists) throw new AlreadyExistsException("order already exists");
                    var payload = JsonSerializer.Deserialize<OrderPlacedPayload>(cmd.Payload, JsonOptions)!;
                    return [NewEvent.Of(OrderEvents.OrderPlaced, payload)];
                }
                case "ShipOrder":
                {
                    if (!state.Exists) throw new DoesNotExistException("order does not exist");
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
event names, all generated straight from the document's slices. The two
refusals are typed, `AlreadyExistsException` and `DoesNotExistException`
(both `CommandRejectedException`s carrying a `Reason`), so the gateway can
tell a caller which one it was without the caller reading the words (see
[the gateway's answers](#the-command-gateways-answers)). Since 0.50.0 every
refusal a generated decider writes is a `CommandRejectedException`, and the
decider declares `TypedRejections = true`: anything else escaping its `Decide`
(a null reference, a broken invariant) is a fault the gateway answers `500`,
not a refusal. A hand-written decider refuses the same way once it sets
`TypedRejections`; until then anything its `Decide` throws is a refusal.
**A change for hand-written hooks:** a hook a generated decider calls (an outcome's
condition, its repeated items) runs inside that typed decider, so a refusal it
throws must be a `CommandRejectedException`; anything else it throws is now a
`500`, and a reactor sending the command retries it rather than dropping it. Because
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
  warns when a `piiSubject` field's name looks like one. Over HTTP, the
  refusal above is a `410 Gone` from the gateway, so a client can tell it
  from a rejected command (`400`) or a retryable conflict (`409`).

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
  {"param": "emailSearch", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"},
  {"param": "emailExact", "field": "email", "kind": "match", "mode": "exact", "normalize": "email"}
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
- **A personal field** (`pii`) holds ciphertext, so it is searched through a
  separate index in its own file, `search.db`. That file is deleted from
  when a person is erased, must be left out of backups, and is rebuilt from
  the log whenever it's missing (it keeps its own position inside itself,
  so it can never be silently half-built). What the index holds depends on
  the mode:
  - `contains` keeps normalized **plaintext**, and the mapping report flags
    every such index.
  - `exact` and `prefix` keep **keyed hashes** (HMACs) computed by the key
    service, never the value itself. A search hashes its term the same way,
    one call per search. `prefix` stores a hash of every prefix from
    `minPrefixLength` characters up, so it must declare `minPrefixLength`.
    The report warns about it: anyone who can call the key service's
    `hmac` endpoint can confirm a guessed prefix and extend it one character
    at a time, so a prefix index is only as private as access to that
    endpoint.

  Hashes survive crypto-shredding, which is why the host deletes a person's
  hashed rows on erasure as well as their plaintext ones.

The host uses one hashing key per application, named by `KMS_INDEX_KEY`
(default: the project name) and created at startup. If operations rotates
that key, the next start notices, rebuilds the hashed indexes with the new
version in the background while searches keep using the old one, and
switches over when the rebuild has caught up. A host that isn't restarted
keeps working on the version it has. If the key service is down, a hashed
search answers 503.

A search returns the matching rows with their personal values revealed as
usual. Searches and scenario checks run the same SQL.

**Since 0.48.0** every query route also answers a `POST` at the same path,
its terms a JSON object body whose keys and values are exactly the query
string's, so a search by a personal field never has to be in a URL, where
access logs, proxies and browser history keep it:

```sh
curl -X POST http://localhost:5000/api/query/customers \
  -H 'Content-Type: application/json' -d '{"emailExact":"alice@example.com"}'
```

It runs the same query as the `GET`: the same access rules, the same rows,
the same refusals. A value is a string or a number; an empty body is no
terms. Anything else is a 400 (a body that is not one JSON object, a value
that is null, a boolean, an array or an object, a key given twice, a query
string on the `POST`), and a body over 1 MiB is a 413. A route searching a
personal field takes `refusePersonalTermsInUrl` (default `false`); the
generated host reads it from `Cqrs:RefusePersonalTermsInUrl`. When on, a
`GET` whose query string carries such a search is a 400 telling the caller
to use `POST`. Turn it on once every caller has moved. The live route stays
`GET` only (a browser's `EventSource` cannot `POST`) and is not affected.

## Running it as a host

`generate --host` writes a complete runnable ASP.NET Core project for the
whole document instead of just the domain code: every decider registered,
every projection running, the command gateway
(`POST /api/cqrs/{aggregate}/{id}/{command}`) and one query route per read
model (`GET /api/query/{collection}`, or `POST` with the terms in a JSON body):

```sh
dotnet new tool-manifest          # once, in your own repository
dotnet tool install JamesTryand.DotnetCqrs.Codegen.Cli --version 0.52.1
dotnet tool run dotnetcqrs-codegen generate \
  --input order-fulfillment.json --output OrderFulfillment --host \
  --aggregate-override notify-shipping-partner=ShippingNotification
```

The project lives in your repository, not in dotnetcqrs's. Its `.csproj`
references the dotnetcqrs packages (`JamesTryand.DotnetCqrs`, `.Host`,
`.Codegen`, `.Crypto`, `.Postgres`, `.Telemetry.Nats`) at the version of the
tool that generated it, and the tool manifest pins that tool at the same
version, so one version pins both the generator and the libraries its code is
written against (0.37.0 is the first release that generates this way). The
manifest is the one `dotnet tool` itself finds, the nearest at or above the
project folder (here, the one `dotnet new tool-manifest` made); only this
tool's entry in it changes. With none, the project gets its own
`.config/dotnet-tools.json`. `--dotnetcqrs-version` names a different
published version.

**Changing the model:** edit the document and run the same `generate`
command again (`dotnet tool restore` first on a fresh clone).
**Upgrading:** `dotnet tool update JamesTryand.DotnetCqrs.Codegen.Cli
--version <new>`, then `generate` again: the `.csproj` and the manifest move
to the new version together. Regenerating rewrites the generated files, so
keep your own code in files of your own, and commit what is generated.

`--dotnetcqrs-project <checkout>/src/DotnetCqrs/DotnetCqrs.csproj` references
a dotnetcqrs checkout's projects instead, and writes no manifest. It is only
for trying a version that is not yet published; this repository's own tests
use it to build against the code under test.

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
Who may call it is your authorization decision: like every command it needs a
service token (next section), and on top of that the `dataSubject` commands
stay refused until you wire a policy saying who may send them.

### Who may send commands: service tokens

A writer takes a command only from a caller it knows: `Authorization: Bearer <token>`,
where the token is one the host issued itself. No identity provider is involved, so an
outside caller (a webhook receiver, a scheduler, another system) can be given one directly:

```sh
dotnet run --project OrderFulfillment -- --issue-token payments --role payments
# dcq_...   (printed once, on standard output)
```

Each token belongs to one service account: a name and one role. The name is the `actor`
stamped on the events its commands produce. The role is issued under the claim named by
`Cqrs:RoleClaim` (default `role`), the claim the document's command permissions read (see
[the end of this tutorial](#checking-a-documents-own-scenarios-automatically)). The host keeps
only a SHA-256 of each token, in `DOTNETCQRS_TOKENS_PATH` (default `data/service-tokens.json`
beside the build output, owner-only on Unix). The token itself is shown once and never logged;
keep it as a secret. `--revoke-token payments` removes an account, and `--list-tokens` lists
them (name, role, date issued; never a token). Both work beside a running host, which sees the
change on its next request. To rotate a token without a gap, issue one under a new name, move
the caller over, then revoke the old one. A host whose `data/` does not survive a restart or a redeploy
(a container with no volume, a fresh publish folder) loses its accounts with it, and every
caller gets `401`: point `DOTNETCQRS_TOKENS_PATH` at storage that persists.

No token, a header that is not `Bearer`, or a token never issued or since revoked: `401`
with `WWW-Authenticate: Bearer`. A token whose role may not send that command: `403`. A
reader forwards the header to its writer, which decides. The query and live routes are not
part of this and behave as before.

A host you wire by hand gets the same scheme from `DotnetCqrs.Host`:

```csharp
var tokens = new ServiceTokenStore(tokensPath);
if (ServiceTokenCommand.Handles(args))                 // --issue-token, --revoke-token, --list-tokens
    return ServiceTokenCommand.Run(args, tokensPath, Console.Out, Console.Error);
builder.Services.AddAuthentication(ServiceTokenDefaults.Scheme).AddServiceTokens(tokens);
builder.Services.AddAuthorization();
// ...
app.MapCqrsGateway().RequireAuthorization();
```

### The command gateway's answers

The gateway answers as the command gateway HTTP contract shared by both stacks (v0.1) says,
so a caller written for pocketcqrs works against dotnetcqrs unchanged.

- **`200`, decided:** `{"events":[...]}`. Each event is
  `{position, id, aggregate, aggregateId, sequence, type, data, metadata, created}`, with
  `data` and `metadata` as JSON objects. A command that produced nothing answers
  `{"events":[]}`.
- **Every other status** is an RFC 9457 problem body (`application/problem+json`) with
  `status`, `title` and `detail`.
- **`400`, refused or malformed:** a refusal's `detail` is the decider's message exactly as
  the decider wrote it. The two generated refusals add `reason`:
  - `does-not-exist`: the stream the command needs is not there yet, so sending it again
    later may succeed;
  - `already-exists`: a create sent to a stream that exists, which is final.

  A caller decides on `reason`, never on the words. A body that is not one JSON object is a
  `400` too (an empty body means `{}`), as is an `Idempotency-Key` over 200 characters.
- **`401` and `403`:** the caller (above).
- **`404`:** an unknown aggregate.
- **`409`:** the command lost a race for the stream and nothing was applied; send it again.
- **`410`:** the subject was erased.
- **`422`:** an `Idempotency-Key` reused for a different request.
- **`503`:** not taken, nothing applied (a dependency down, a reader with no writer). It
  carries `Retry-After` in seconds.
- **`500`, `502` and `504`:** it may or may not have been applied; send it again with the
  same `Idempotency-Key`.

**`Idempotency-Key`** is an optional header, at most 200 characters. The same key with the
same aggregate, id, command and body replays the first `200`, byte for byte, without
deciding again. Nothing else is replayed: a `409` is decided again, and a `400`, `401`,
`403`, `404`, `410` or `503` is never recorded, so a `does-not-exist` refusal sent again
under the same key succeeds once the stream exists. The key is checked after the caller is,
so a replay never skips the role check, and duplicates arriving together are decided once.
A writer keeps the answers beside its event log (`data/idempotency.db`, or the
`cqrs_idempotency` table on Postgres) for `DOTNETCQRS_IDEMPOTENCY_RETENTION_DAYS` (default
`7`, the least the contract allows; a smaller or non-numeric value refuses to start). A host
wired by hand registers an `IIdempotencyStore` (`SqliteIdempotencyStore`,
`PostgresIdempotencyStore`); without one, the gateway keeps answers in memory, which a
restart loses.

**Since 0.49.0** the gateway meets that contract. Before it, the accepted body was a bare
array whose `data` and `metadata` were strings holding JSON, a refusal had no `reason`,
`Idempotency-Key` was ignored, a 503 had no `Retry-After`, a body that did not parse was a
`500` when command permissions were wired, and an id holding a `/` reached the decider still
encoded as `%2F`. And the generated host signed no one in: a command declaring no permission
was open to anyone, and one declaring a permission was refused to everyone. Now every command
to a generated writer needs a service token. A caller that reads the accepted body, or sends
commands to a generated host without a token, has to change.

Every host also has a node identity, the same on dotnetcqrs and pocketcqrs: a
`node_id` that survives restarts and tells several nodes on one machine
apart. The host logs it once at startup and registers it in DI as
`NodeIdentity`. To keep it across restarts, give the host a node-local state
directory, which is never the replicated `data/`:

```sh
CQRS_STATE_DIR=/var/lib/orderfulfillment/node dotnet run --project OrderFulfillment
```

The first boot writes a new id to `node-id` in that directory and every later
boot reads it back. An orchestrator that knows the workload and replica can
assign one instead with `CQRS_NODE_ID` (1–64 letters, digits, `_` or `-`; the
host refuses to start on anything else), and the stored file is then left
alone. With neither set, the id is new on every start and the log says so. A
deployment whose state directory doesn't survive a restart, such as a
container with no volume, should set `CQRS_NODE_ID`.

The log line also names the node: `instance` is the project name unless
`CQRS_INSTANCE` sets another (same format as the id), and `host` is the
hostname, or `unknown` if it can't be read.

Every host also binds an **ops port** first, before it reads any other setting, so an
orchestrator gets an answer while the node boots: `CQRS_OPS_PORT` (default `10056`),
serving `GET /healthz` with the same identity (its fields are `null` until identity is
resolved). It is separate from the traffic port and unauthenticated, so keep it off any
ingress. `CQRS_OPS_BIND` sets the address it binds (default every interface;
`127.0.0.1` keeps it local). Several hosts on one machine must each set their own port; a host that can't bind it
refuses to start.

`GET /readyz` on the same port says whether to route traffic here: `503` with reason
`starting` while booting, `503` `catching_up` once the traffic port listens and the
projections are catching up, then `200` `ready` when every read model is within
`DOTNETCQRS_LAG_THRESHOLD_SECONDS` (default `5`). A projection that later falls behind or
blocks shows up as `projection_behind` / `projection_blocked`; on this host, the writer, that
is `200` `degraded` rather than `503`, so the only write authority never leaves the pool. For
the same reason a writer still catching up after `DOTNETCQRS_CATCHUP_DEADLINE_SECONDS`
(default `60`) serves anyway.

`/readyz` also lists the host's required dependencies under `checks.dependencies`: the event
store, and the key-management facade when the domain has personal data. Each is checked every
`DOTNETCQRS_DEPENDENCY_CHECK_SECONDS` (default `5`) and counts as down after
`DOTNETCQRS_DEPENDENCY_FAILURES` (default `3`) failures in a row. The event store down is
`event_store_unavailable`; the key service down is `dependency_unavailable`. On this host, the
writer, both are `degraded` rather than `503`.

A host can also **push** its metrics to a message bus, so a monitor on the bus sees a mixed estate
without scraping every node. Set `CQRS_TELEMETRY_URL` to the bus's address (`nats://host:4222`; the
scheme selects the transport, NATS being the one this host ships, and unset means no push) and,
optionally, `CQRS_TELEMETRY_INTERVAL` (seconds, default `15`). Each snapshot is JSON, the same
figures `/metrics` returns, published to the subject `cqrs.telemetry.metrics.<node_id>`: once the
node is up, then on the interval, and once more as draining begins. It is best-effort and never a
dependency: a snapshot the bus cannot take within a second is dropped, never queued, and an
unreachable bus changes nothing in `/healthz` or `/readyz`. A bad setting, or a scheme with no
transport, refuses to start. The NATS client lives in `DotnetCqrs.Telemetry.Nats`, so a host that
does not push carries no bus client.

On `SIGTERM` or Ctrl+C the host **drains** rather than just stopping: `/readyz` goes `503`
`draining` first, so whatever routes to this node stops, then in-flight requests finish, then
each consumer finishes the event it is applying and stops (the next start resumes from the next
event; it does not catch up first). The ops port keeps answering until the process exits.
`DOTNETCQRS_DRAIN_DEADLINE_SECONDS` (default `30`) bounds all of it; past that the consumers
are cancelled mid-event, which the next start redoes, and the log says so.

`GET /metrics` on the same port serves the contract's `cqrs_` series in the Prometheus text
format, every one present from the first scrape: identity, readiness, commands by outcome
(`accepted`, `rejected`, `conflict`, `unavailable`, `error`) with a duration histogram, events
appended, each consumer's lag and state, and the dead-letter depth.

A writer also keeps a **heartbeat**: one row beside the event log
(`writer_heartbeat`, never an event), upserted every `DOTNETCQRS_HEARTBEAT_INTERVAL_SECONDS`
(default `1`) with its node id and `CQRS_OPS_URL` (the ops port's address as other nodes reach it,
default `http://<hostname>:<ops port>`, or the `CQRS_OPS_BIND` address when that names one). A read
replica measures its lag from that row's age; once
it is older than `DOTNETCQRS_STALE_THRESHOLD_SECONDS` (default `5`) the replica asks the writer's
`/healthz` whether the writer is up (`ReplicationMonitor` in `DotnetCqrs.Host`).

### Running a reader

The same generated host runs as a **reader** with `DOTNETCQRS_ROLE=reader` (the default is
`writer`; anything else refuses to start). One writer and any number of readers make the
single-writer/multi-reader topology:

- **The event log is the writer's.** On SQLite the reader opens `DOTNETCQRS_EVENTS_PATH` read-only:
  the writer's `events.db` as it reaches this host, typically inside a LiteFS mount (see
  [cross-host replication](cross-host-replication.md); a writer can set the same variable to put its
  own `events.db` inside the mount). The file must already exist. On Postgres it reads
  `DOTNETCQRS_POSTGRES`, the writer's database.
- **Everything else is the reader's own**, in SQLite under its `data/` whichever backend the log is
  on: its read models, search indexes, and its consumers' checkpoints and dead letters
  (`checkpoints.db`). It runs every projection and search index, so its query routes answer from
  local data.
- **Side effects happen once, on the writer.** A reader runs no reactors and never destroys a
  key; it still empties its own reveal cache when a subject is erased.
- **Commands go to the writer.** With `DOTNETCQRS_WRITER_URL` (the writer's traffic port) a reader
  forwards each command there, with its `Authorization` and `Idempotency-Key`, where it is
  authenticated, decided and counted; a writer that can't be reached
  gives `502`, one that times out `504`. Without it every command is refused with `503`, counted
  on the reader as `unavailable`.
- **Health.** `/healthz` and `/readyz` say `role: reader`. The reader measures the writer's
  heartbeat (`replication_stale`, `replication_unknown`), requires the writer as a dependency
  (`writer` in `dependencies`, found through the heartbeat's `CQRS_OPS_URL`), and does not serve
  until its read models have caught up, however long that takes.

```sh
# the reader, beside a writer whose traffic port is writer-host:8080
DOTNETCQRS_ROLE=reader DOTNETCQRS_EVENTS_PATH=/litefs/events.db \
  DOTNETCQRS_WRITER_URL=http://writer-host:8080 dotnet run
```

## Checking a document's own scenarios automatically

You don't have to write demo code like the above by hand to check a
document's claims — `order-fulfillment.json` declares its own
given/when/then scenarios, and `ScenarioVerifier.VerifyAsync(doc, result, ...)`
runs every one of them against the generated code for you (compiling a
small fixed harness alongside it in memory and running it in-process,
against the dotnetcqrs the tool ships with, so no source checkout or
`dotnet build` is needed). From the command line, that's `dotnetcqrs-codegen verify`;
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

Checked against the generated code (`dotnetcqrs-codegen verify`), a command
scenario runs the generated `CommandAuthorization` before the decider, as the
gateway does, over the read models it reads (a
`requiredOwnership.via`, a `scope`), built from the scenario's given events.
A scope that refuses the scenario's `when.actor` refuses the command, with the
scope's `message` (schema 4.0.0) when it declares one; the gateway's 403
carries the same message. A scenario may also name the role its actor holds
(`when.role`, schema 4.1.0): the role declarations (`requiredRole`,
`fieldGatedRole`) must admit it, refusing with "not authorized" otherwise, and
a `requiredOwnership.via` or `scope` whose `bypassRoles` name it is skipped.
Without a role, the actor holds the command's required roles and none of its
bypass roles.

A `stateChange` scenario whose `then.events` is empty (schema 6.2.0) is an
assertion too: it passes, as "accepted, nothing recorded", only when the
command is accepted and records no events, so a forgotten event or a refusal
fails it. A `then` with no `events` at all is refused by the schema. It is the
scenario of an outcome whose `eventIds` is empty: a message that means nothing
to its receiver is accepted this way rather than refused.

An automation scenario runs on the stream its trigger names (schema 5.0.0):
every automation started by an event has `targetIdField`, a field of each
trigger event whose value is the id of the stream its command acts on, and
the command declares that field with `idAttribute: true`. The generated
reactor sends to that stream and nowhere else (it used to derive the id from
the trigger's own stream, which could never reach an existing stream named by
a field, such as a customer's from a payment's `customerId`). A scenario
whose command carries another value for the field fails, as does one that
shows no trigger event: before, such a scenario passed with its command going
to the wrong stream. The generator refuses an automation without the field,
or whose field a trigger event or the command lacks, and (0.41.0) one whose
trigger and command declare a field of one name with different types: the
reaction copies fields by name, and no scenario would show the mismatch.

**The boundary (schema 6.0.0, 0.42.0).** A document names messages, never
their sources, so the parts that cross the edge are read and only their model
is generated. A `stateChange` slice started by an ingress (`ingressId`)
generates its command; the ingress generates nothing (how its message arrives
is surface tooling's). One with neither a screen nor an ingress (schema 6.1.0,
0.44.0) is sent by another of your systems over a coordinating document's
edge: its command must be `public`, is generated like any other, and the
report names it. An automation with a delay generates its command, and
one with an effect its answer and give-up commands, but no reaction: firing
them is their source's. The answer and the give-up may each begin the stream
(0.43.0: several commands may), and whichever comes first does: the other is
refused as "already exists", so a call has exactly one outcome. An output
generates nothing; the
generator checks each field it says leaves is on its trigger events. Each is
named in the report.

**A system's edges (0.45.0).** Given a coordinating document (eventmodelschema's
`architecture.schema.json`, 0.2.0) and this document's system in it,
`generate` and `verify` also read its edges: `--architecture
model/landing-pages.architecture.json --system commerce`. `generate` writes
`Edges.g.cs`, two tables, `Generated.SystemEdgeTables.In` (the internal edges
into this system) and `.Out` (the public events it publishes, with the fields
each event's public marker lists, schema 6.2.0, or else only the fields some
edge maps). An edge reading a field its event's marker does not list is
refused, as is a personal field in the list; a listed field the event does
not declare is refused by the mapping, so the system's own document says what
leaves it. `DotnetCqrs.Edges` is what they mean: `Edges.Publish`
makes an `EdgeMessage` of an event, `EdgeIn.Translate` the command it becomes.
Each edge's `examples` run against that code, and one that fails refuses the
run. pocketcqrs (v0.28.0) writes the same tables and reads and sends the same
message, so a system on either runtime can be on either end of an edge. No
carrier is named in the document or the generated code. A document's
`expects` (schema 6.2.0), what its system relies on from others, is read and
generates nothing: an expected event is never this system's own. The mapping
refuses `expects` naming the document's own id.

**A system in several coordinating documents (0.53.0).** `--architecture` is
repeatable: a system linked in more than one view gets its edges from all of
them (`ArchitectureEdges.ForViews`), the edges in, the events published (their
fields together) and every example. `--system` is given once for all, once per
document in order, or left out, when the system in each is the one that is this
document (by its release's id, or its path). Two views giving one edge id
different ends or translations are refused, naming the edge and both views, as
eventmodelschema's checker refuses them; pocketcqrs v0.34.0 reads them the same.

**A message names its sender by its document id (0.52.0, coordinating
document 0.3.1).** `SystemEdgeTables.System` and each `Out` entry's system are
this document's `id`, never the name a view gives it, and each `In` entry's
sender is the sender's document id, read from its document or its release. A
system the view names by release generates too, when the document given is the
one whose `id` the release names; an edge from a placeholder is refused, since
it has no id yet. This is a change on the wire where a view named a system
otherwise: regenerate both ends and deploy them together, since the carrier
names the sender's stream by it. **With no coordinating document**, `generate`
writes `Edges.g.cs` when some public event's marker lists its fields: those
events with exactly those fields, and no edges in, so a sender is built from
its own document alone. An event whose marker lists nothing publishes nothing
then. **Since 0.52.1** the same holds with a coordinating document: every event
whose marker lists its fields is in `Out`, whether or not an edge in that
document starts from it, since a link in another view may read it.

**Carrying an edge over NATS (0.46.0).** `JamesTryand.DotnetCqrs.Edges.Nats`
carries the tables over NATS JetStream, wire-compatible with
pocketcqrs-extensions' `federation` (the same subjects, stream, durable consumer
and command ids), so a dotnetcqrs system and a pocketcqrs one share an edge either
way. The host wires it by hand, from its own configuration, never from the
document: the carrier is a source, surface tooling's to configure.

```csharp
var js = new NatsJSContext(EdgesNatsConnection.Open(url, user, password, "my-system"));
// the sending side: a consumer of its own log
var publisher = new EdgePublisher(js, "personal-data", Generated.SystemEdgeTables.Out);
engine.Register(publisher);
_ = publisher.EnsureStreamUntilAsync(TimeSpan.FromSeconds(2), Console.WriteLine, stopping);
// the receiving side: one ingest per peer, its dead letters in the host's own store
foreach (var peer in Generated.SystemEdgeTables.In.Select(e => e.FromSystem).Distinct())
    _ = new EdgeIngest(js, peer, Generated.SystemEdgeTables.In.Where(e => e.FromSystem == peer).ToList(),
        registry, deadLetterStore).RunAsync(stopping);
```

The publisher waits for the server's acknowledgement before its checkpoint moves,
and publishes again under the event's id after a crash (the stream drops the
duplicate). The ingest applies a message and then acknowledges it, with a command
id from the message and the edge, so a redelivery is skipped. A refusal by the
decider is acknowledged (the domain's answer), a conflict or a failure around
the decision is redelivered after a pause and dead-lettered once its deliveries
are used up, and an aggregate the system does not have is dead-lettered.
**Since 0.47.0** a newer version of an event the system has edges for than any of
them names is held (offered again after `EdgeRetry.Hold`, 30 s by default) until
a receiver that names it reads it, then applied once; the deliveries it spent
held do not count against the retry budget. So a version change can deploy its
sender first. An older version, or an event no edge names, is skipped: keep the
old version's edge in the coordinating document while a version changes, or a
receiver deployed first skips what the old sender still sends. Both start while
the carrier is down and carry once it is back.
Tested against a real `nats-server` (`DOTNETCQRS_NATS_SERVER`), including kill
tests that fail against a publisher that does not wait and an ingest that
acknowledges first.
The value has to be on the event: a command sent directly puts its stream's id
in its body too (`orderId` in `PlaceOrder`'s), or the reaction has no target
and is dropped, and logged.

A host that verifies against its own deciders (`ScenarioVerifier.VerifyAsync(doc,
mapped, new HostDeciders(register))`) runs the same check when it hands over its
generated authorization: `Authorization = Generated.CommandAuthorization.ActorRefusalAsync`.
The read models it reads are built by the host's own projections (those named in
`HostDeciders.Projections`, else the generated ones in the host's assembly).
Without it, a scenario whose command's authorization reads read models, or
whose role the command's role declarations would check, fails, saying so,
rather than passing a command the gateway would refuse.

The generated host enforces the same declarations at its gateway with nothing
to wire. When the document declares any command permission, `generate --host`
writes `Program.cs` to pass `Generated.CommandAuthorization.AuthorizeAsync` to
`MapCqrsGateway(authorize: ...)`, reading the caller's role from the claim
named by `Cqrs:RoleClaim` (default `role`) and their own subject id from
`Cqrs:SubjectClaim` (default the name-identifier claim). A command that
declares nothing is allowed to any service account, and a document that
declares nothing generates no authorization hook at all. The policy is found whatever
the case of the request's aggregate and command. The `dataSubject` aggregate
keeps the refusal it has with no hook: who may erase whom is the host's own
delegate. The caller's role is its service token's (see
[who may send commands](#who-may-send-commands-service-tokens)): a token whose
role a command's declarations refuse gets `403`. Replace the line to resolve
roles some other way.

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
