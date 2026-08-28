# Concepts: coming from CRUD

You know CRUD: a table has rows, a request updates a row in place, a query
reads the current row. `dotnetcqrs` doesn't keep that experience on the read
side the way [pocketcqrs](https://journal.bureau.tryand.uk/lab/pocketcqrs)
does — there's no PocketBase underneath handing you REST/realtime/auth for
free — but the underlying idea is the same, and it's the whole point of CQRS
(Command Query Responsibility Segregation) and event sourcing.

## The core shift: facts instead of current state

CRUD stores **current state** and overwrites it: `UPDATE tasks SET completed
= 1 WHERE id = 't1'`. The previous value is gone — the row only ever tells
you *what is true now*, never *what happened*.

Event sourcing stores **facts about what happened**, and derives current
state from them. Instead of overwriting a row, you append `TaskCreated`,
then later `TaskCompleted`, to a log for `t1`. Nothing is ever overwritten
or deleted. "Current state" becomes a read-time (or projection-time)
computation: fold every event for `t1` in order and see where you land.

That log is the **source of truth**. Every read-model table you query is a
*derived*, disposable view of it. You could delete every read-model table
and rebuild them from the log; you could never do that with a CRUD table,
because the table *is* the truth.

`dotnetcqrs` uses SQLite for both sides — the event log and the read
models — but they are two separate SQLite databases (or at minimum, two
clearly separate sets of tables) with different write paths, not one
database serving both roles interchangeably. See "Storage" below.

## Commands and events are not the same thing

CRUD has one kind of write: a mutation. `dotnetcqrs` splits writes into two
concepts that are easy to conflate at first:

| | is a request or a fact? | can it fail? | example |
| --- | --- | --- | --- |
| **Command** | a request — "please do this" | yes, can be refused | `CompleteTask` |
| **Event** | a fact — "this happened" | no, it already did | `TaskCompleted` |

A command is *intent*, named as an imperative verb, and it can be rejected —
the task might already be complete, or the caller might not be allowed to
complete it. If it's accepted, it produces one or more events, named as
past-tense facts, which are what actually get appended to the log. The
distinction matters because only events are stored; a rejected command
leaves no trace, exactly like a CRUD `UPDATE` that fails validation and
never reaches the database.

## Deciders: the write side, and why they don't touch the database directly

In CRUD, request handling and persistence are one step: validate, then
`UPDATE`. Here the write side is a pure decision function — a **decider** —
with no database access at all. A decider is three functions:

- `InitialState` — what a brand-new stream looks like before any events
- `Decide(state, command) → events | rejection` — the business rule
- `Evolve(state, event) → state` — how one event changes the state

To handle a command, the runtime *replays* every existing event for that
aggregate through `Evolve` to reconstruct current state, then calls
`Decide` against that reconstructed state. If `Decide` accepts, the
resulting events are appended to the log — that append is the only
database write in the whole path. The decider itself never reads or writes
a row; it only ever sees state folded from events. This is what "the event
log is the source of truth" means concretely: the decider's whole world is
the log, replayed.

("Decider" is a named pattern, not a `dotnetcqrs`/`pocketcqrs` invention —
see [the pattern write-up](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider).)

## Aggregates: the CRUD "table" is now a stream, scoped by ID

In CRUD, a row's identity is its primary key, and any row can be updated
independently of any other. A decider's unit of consistency is an
**aggregate** — e.g. `task`, `order` — and every command targets one
aggregate instance by ID (`task/t1`). All of `t1`'s events live in one
stream, appended with per-stream optimistic concurrency: two concurrent
commands against `t1` can conflict and one is refused, the same guarantee
an `UPDATE ... WHERE version = ?` gives you in CRUD. Two different tasks,
`t1` and `t2`, never contend with each other — same as two unrelated rows.

## Projections: your familiar query surface, rebuilt from facts

This is the part that looks exactly like CRUD from the outside: a
**projection** folds events into a denormalized SQLite read-model table,
and you query it however your application queries SQLite — no ORM, REST
framework, or realtime layer bundled in, unlike `pocketcqrs`'s PocketBase
collections, so wiring up your own query/API surface over the read-model
tables is part of what a `dotnetcqrs`-based system needs to do.
`TaskCreated` then `TaskCompleted` for `t1` becomes one row in `tasks` with
`completed = 1`, same as CRUD would show you.

The differences only show up when you think about *how that row got
there*: a projection is disposable and reproducible. If you change how a
projection folds events, or discover a bug in it, you don't migrate the
row — you fix the code and rebuild the projection, which replays the whole
log from scratch and produces a correct table. A CRUD table has no
equivalent operation, because it has no log behind it to replay.

Read models are meant to be **searchable**, not just queryable by key —
denormalize aggressively, and layer full-text search (e.g. SQLite's FTS5)
over the projected tables where it's useful, rather than treating search as
a bolt-on afterthought.

## The write-guard: why you can't just write to a read-model table

The next question is "what stops something writing to the `tasks` table
directly, the CRUD way, and skipping all of this?" In `pocketcqrs`,
PocketBase enforces this for free: any direct write to a projection-owned
collection is rejected with 403. `dotnetcqrs` has no such framework
underneath it, so this is a mechanism the library provides itself: a SQL
trigger installed on every guarded table (`WriteGuard.InstallAsync`)
rejects any write on any connection except the one currently inside a
connection-scoped bypass (`WriteGuard.BeginBypassAsync`) — which is exactly
what a projection's own `ApplyAsync` opens before it writes. State changes
should be commands; commands become events; events become read models.
There is exactly one path in, and the write-guard is what makes that an
enforced guarantee rather than just a convention.

## Reactors: sagas, or "triggers that dispatch commands, not SQL"

CRUD sometimes reaches for a DB trigger to make one write cascade into
another. The equivalent here is a **reactor**: a durable consumer that
watches committed events and, on a match, dispatches a *follow-up command*
— back through the same decider registry a human caller would use, not by
appending events directly. `TaskCompleted` → reactor → `CreateNote`
command → decider decides → `NoteCreated` event. Because the reaction is a
real command, it can be refused, it's idempotent under replay (a reactor
that dispatches the same command with the same target ID twice doesn't
duplicate), and it shows up in the log with causation/correlation metadata
tying it back to the event that triggered it. This is the multi-aggregate
"saga" pattern CQRS literature talks about — one decider's fact triggering
another decider's decision.

Reactors and projections share the same underlying need: a durable
consumer over the event log that tracks its own read position and catches
up reliably after a restart. `dotnetcqrs` builds that consumer/subscription
plumbing once and uses it for both, rather than as two separate
implementations.

## Eventual consistency, made concrete

In CRUD, a read after a write always sees the write — it's the same row.
Here, the write side (append to the log) and the read side (fold into a
table) are two separate steps connected by a **consumer** that watches the
log and applies each new event to its projection. That catch-up gap is
what "eventually consistent" means — the source of truth moved first, the
read view catches up. It's the same gap a CRUD system gets from a read
replica or a cache.

## Storage: SQLite, twice

Both the event store and the read models are SQLite, but treat them as two
different roles even where they happen to live in the same file or
process:

- **Event store**: append-only, one row per event, ordered per stream and
  globally, never updated or deleted. This is the source of truth.
- **Read models**: ordinary denormalized tables, freely
  rewritable/rebuildable, owned entirely by their projection code. Never
  hand-edited, never written to by anything except the projection that
  owns them (see "the write-guard" above).

## FaaS-style handlers and extcaller

Deciders, projections, and reactors are meant to be small, focused
functions dispatched by the runtime rather than long-running services you
hand-wire together — a FaaS-style handler surface, similar in spirit to
`pocketcqrs`'s `pb_functions/`. `extcaller` is the mechanism for those
handlers to call out to external systems (HTTP, other services) as part of
handling a command or reacting to an event, kept as a distinct concern from
the decider/projection/reactor logic itself so that pure decision logic
stays pure and testable.

The follow-up command `extcaller` produces from a third-party response is
dispatched through an `IFollowUpDispatcher`: `InProcessFollowUpDispatcher`
applies it against a local `DeciderRegistry`, or `GatewayFollowUpDispatcher`
POSTs it to a configured command gateway — another `dotnetcqrs` instance, or a
`pocketcqrs` one — which is how a reaction chain crosses an instance boundary.
Either way the target decider still gets to accept or reject it; `extcaller`
never appends a raw event.

## A CRUD → `dotnetcqrs` glossary

| CRUD instinct | `dotnetcqrs` equivalent |
| --- | --- |
| `UPDATE table SET ...` | append a command → decider decides → events appended |
| the row *is* the data | the row is a *projection* of the events; the log is the data |
| SELECT / REST GET | query the SQLite read-model tables directly, or via your own API surface |
| primary key | aggregate ID (`task/t1`) — scopes one event stream |
| `WHERE version = ?` optimistic lock | per-stream optimistic concurrency on append |
| schema migration | projection rebuild (read side) — deciders don't have "schema" the way rows do |
| DB trigger cascading a second write | reactor dispatching a follow-up command |
| direct table write | rejected by the write-guard (a SQL trigger, bypassed only inside a projection's own scoped write) |
| audit log bolted on afterward | not needed — the event log already *is* the full history |

## Where next

- [Getting started](getting-started.md) — run a hand-written domain over HTTP, send
  a real command, see a real rejection.
- [Tutorial](tutorial.md) — the other direction: an EventModeling document, generated
  code, a running slice.

This doc started as Milestone 1 of the build-out tracked in the
`platform/dotnet-cqrs-baseline` delegation; everything it describes (write-side core,
consumer/subscription plumbing, projections, reactors, write-guard, extcaller) is now
built, along with a runnable host (`platform/dotnetcqrs-host`) and EventModeling
codegen (`platform/eventmodeling-codegen`) on top of it.
