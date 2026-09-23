# dotnetcqrs

A CQRS + event sourcing library for .NET: [deciders](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
(`InitialState` / `Decide` / `Evolve`) commit events to an append-only
SQLite log, projections fold that log into ordinary read-model tables, and
reactors turn committed events into follow-up commands — the same shape as
[pocketcqrs](https://journal.bureau.tryand.uk/lab/pocketcqrs), ported from
Go/PocketBase to C#/.NET rather than wrapping it.

- **Write side**: `DeciderRegistry` replays an aggregate's stream through
  `Evolve`, calls `Decide` against the result, and appends whatever events
  it returns — the only database write in the whole path.
- **Event store**: `SqliteEventStore` — append-only, per-stream optimistic
  concurrency, a global position for catch-up subscriptions, dead-letter
  tracking for consumers that fail. The write path is provider-neutral
  (`DotnetCqrs.Abstractions`' `IEventStore`); `DotnetCqrs.Postgres`'
  `PostgresEventStore` is a second implementation (see
  [Postgres backend](docs/postgres-backend.md)).
- **Read side**: `IProjection` folds events into plain read-model tables you
  query however you query the database — no ORM or REST layer bundled in
  (unlike pocketcqrs's PocketBase collections; see [Concepts](docs/concepts.md)
  for what that trade-off actually means). SQLite by default;
  `PostgresReadModelStore` behind the same `IReadModelStore` contract.
- **Write-guard**: the read-model tables really can only be written by the
  projection that owns them — a SQL trigger + connection-scoped bypass on
  SQLite, the database's own table-privilege system on Postgres.
- **Reactors**: durable consumers that map a committed event to a
  follow-up command, dispatched back through the same registry a human
  caller would use — the multi-aggregate "saga" pattern, with
  causation/correlation metadata tying the reaction back to its trigger.
- **extcaller**: the pattern for reacting to an event by calling an
  external system, kept as its own consumer so decider/projection logic
  stays free of network calls.
- **Host** (`DotnetCqrs.Host`): an ASP.NET Core gateway
  (`POST /{prefix}/{aggregate}/{aggregateId}/{command}`) plus pluggable
  auth (bring your own `AddJwtBearer`/Entra ID/etc. — the library only
  reads whatever `HttpContext.User` your scheme populates) and file
  storage (`IFileStore`, a local-disk implementation included).
- **Personal data** (`DotnetCqrs.Crypto`): GDPR erasure by
  crypto-shredding. A personal field is a `Pii<T>`, encrypted under its
  data subject's own key (held by a separate key-management service)
  before it reaches the log, and revealed only when something reads it.
  Erasing the subject destroys the key, so every copy of their data becomes
  unreadable without editing the log. See
  [Concepts](docs/concepts.md#personal-data-crypto-shredding-instead-of-delete).
- **Codegen** (`DotnetCqrs.Codegen`, CLI `dotnetcqrs-codegen`): generate a
  starting decider, projections, reactors and query routes straight from an
  [EventModeling](https://eventmodeling.org) document — wiring correct,
  business rules left as stubs for you to fill in — or a whole runnable
  host with `--host`, and verify a document's own scenarios against the
  result. Fields the document marks `pii` are wired for encryption and
  erasure end to end.

## Docs

- [Getting started](docs/getting-started.md) — run the sample host, send a
  real command, see a real rejection, look at the raw event log
- [Tutorial](docs/tutorial.md) — an EventModeling document, generated code,
  a running slice, including a real collision, the field-mapping gap the
  generator leaves for you, personal data and erasure, and a generated host
- [Concepts](docs/concepts.md) — coming from CRUD: commands vs. events,
  deciders, aggregates, projections, the write-guard, reactors,
  crypto-shredding
- [Cross-host replication](docs/cross-host-replication.md) — a same-host
  read-only secondary plus write-forwarding, extended across real hosts
  via LiteFS (dotnetcqrs-multi-node Milestone 3)
- [dotnetcqrs ↔ pocketcqrs interop](docs/interop.md) — dispatching commands
  between the two runtimes' gateways both ways: what matches for free, and
  the auth alignment that is the actual work (dotnetcqrs-multi-node
  Milestone 5)
- [Postgres backend](docs/postgres-backend.md) — `DotnetCqrs.Postgres`, a
  second `IEventStore` / `IReadModelStore` implementation behind the
  Milestone 6 abstractions: advisory-lock append serialization, a
  grant-based write-guard (dotnetcqrs-multi-node Milestone 7)

## Samples

- [`samples/OrderFulfillment`](samples/OrderFulfillment) — a hand-written
  `order`/`task` domain: deciders, projections, a reactor connecting them
- [`samples/OrderFulfillmentHost`](samples/OrderFulfillmentHost) — the same
  domain, running for real over HTTP with auth and file storage
- [`samples/OrderFulfillmentGenerated`](samples/OrderFulfillmentGenerated) —
  the actual output of running `DotnetCqrs.Codegen` against an EventModeling
  document, committed so it's provably compilable, not just illustrative
- [`samples/MultiNode`](samples/MultiNode) — a primary/secondary pair for
  the cross-host replication smoke test (see `ops/litefs/` and
  [Cross-host replication](docs/cross-host-replication.md))
- [`samples/Interop`](samples/Interop) — a verification harness dispatching
  `CreateTask` both ways between a real `dotnetcqrs` gateway and a real
  `pocketcqrs` one (see [interop](docs/interop.md))

## Development

Requires .NET 10 SDK.

```sh
dotnet build dotnetcqrs.slnx
dotnet test dotnetcqrs.slnx
```

## Status

Under active development; not published as a NuGet package yet — consume
via project/source reference. No stability guarantees on the public API.

## License

MIT (see `LICENSE`).
