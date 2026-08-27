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
  tracking for consumers that fail.
- **Read side**: `IProjection` folds events into plain SQLite tables you
  query however you query SQLite — no ORM or REST layer bundled in (unlike
  pocketcqrs's PocketBase collections; see [Concepts](docs/concepts.md)
  for what that trade-off actually means).
- **Write-guard**: a SQL trigger rejects direct writes to a guarded table
  on every connection except the one currently inside a projection's own
  scoped bypass — the read-model tables really can only be written by the
  projection that owns them.
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
- **Codegen** (`DotnetCqrs.Codegen`): generate a starting decider,
  projections, and reactors straight from an
  [EventModeling](https://eventmodeling.org) document — wiring correct,
  business rules left as stubs for you to fill in — and verify a
  document's own scenarios against the result.

## Docs

- [Getting started](docs/getting-started.md) — run the sample host, send a
  real command, see a real rejection, look at the raw event log
- [Tutorial](docs/tutorial.md) — an EventModeling document, generated code,
  a running slice, including a real collision and the field-mapping gap
  the generator leaves for you
- [Concepts](docs/concepts.md) — coming from CRUD: commands vs. events,
  deciders, aggregates, projections, the write-guard, reactors

## Samples

- [`samples/OrderFulfillment`](samples/OrderFulfillment) — a hand-written
  `order`/`task` domain: deciders, projections, a reactor connecting them
- [`samples/OrderFulfillmentHost`](samples/OrderFulfillmentHost) — the same
  domain, running for real over HTTP with auth and file storage
- [`samples/OrderFulfillmentGenerated`](samples/OrderFulfillmentGenerated) —
  the actual output of running `DotnetCqrs.Codegen` against an EventModeling
  document, committed so it's provably compilable, not just illustrative

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
