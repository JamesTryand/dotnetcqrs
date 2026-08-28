# Postgres backend (dotnetcqrs-multi-node Milestone 7)

> **Status: verified 2026-08-28** on one machine against `postgres:17-alpine` — 16
> in-suite tests under `test/DotnetCqrs.Tests/Postgres/`, `[SkippableFact]`-gated on
> `DOTNETCQRS_PG`. See "Verification run" at the bottom.

Milestone 6 extracted `src/DotnetCqrs.Abstractions/` — the provider-neutral contracts
`IEventStore` / `IReadModelStore` / `IDeadLetterStore` (plus the already-abstract
`IPollSource` / `ICheckpointStore` / `IProjection` / decider / reactor surface) — with
`SqliteEventStore` + `SqliteReadModelStore` as the one implementation. This milestone is
the **second implementation**: `src/DotnetCqrs.Postgres/` (`PostgresEventStore`,
`PostgresReadModelStore`), backed by Npgsql, behind those exact interfaces, with no
changes to the abstractions.

It is deliberately **an alternative and complementary scaling path**, not a replacement
for anything. SQLite-file replication (Milestones 1–3: one master, many read-only
replicas over LiteFS) already delivers single-master / multi-read-model scaling with no
server to run. Postgres instead buys managed-service operability (RDS / Cloud SQL /
etc.), a client-server concurrency model, and native streaming replication as an eventual
alternative to the LiteFS setup — at the cost of running a database server.

## What ported for free

The M6 groundwork did most of the work:

- **Envelope + SQL shape.** `Event` / `NewEvent` / `Command` / `DeadLetter` are
  provider-neutral already. `RETURNING`, `ON CONFLICT (…) DO UPDATE SET x = excluded.x`,
  and `@name` parameters (M6 switched every provider-neutral projection from `$name` to
  `@name` precisely for this) all work unchanged on Postgres.
- **Read-model seam.** `IReadModelStore.Connection` is `System.Data.Common.DbConnection`;
  `NpgsqlConnection` is one. Hand-written and generated projection bodies run against
  Postgres byte-identical — the only Postgres-flavoured thing is the read model's own
  DDL (`boolean`, not SQLite's `INTEGER` 0/1), which is projection-owned schema anyway.
- **`AddParam(name, null)`.** M6 flagged that Npgsql historically rejected an untyped
  `DBNull` parameter ("cannot determine parameter type"). Tested on Npgsql 10.0.3
  (`PostgresAddParamTests`): it is accepted and writes SQL `NULL`. No change to
  `DbCommandExtensions.AddParam` was needed — the abstractions are untouched.

## Two real redesigns

These are not translations of the SQLite mechanism; the SQLite mechanism has no Postgres
equivalent and the Postgres-native replacement is a different design.

### 1. Append is serialized by a transaction-scoped advisory lock

`SqliteEventStore` gets a gap-free, commit-ordered `position` for free: one connection,
one in-process `SemaphoreSlim` around the check-then-append. Postgres with a connection
pool does not. An identity column hands transaction A `position = 5` and transaction B
`position = 6`, but **B can `COMMIT` before A**. `ConsumerEngine` polls
`WHERE position > checkpoint ORDER BY position`, so it would read 6, checkpoint 6, and
**event 5 would be delivered to no projection, ever** — a silent, permanent hole.

`PostgresEventStore.AppendAsync` therefore takes `pg_advisory_xact_lock(<fixed key>)` as
the first statement inside the append transaction. The lock is cluster-global and
released automatically at transaction end, so the whole check-then-insert is serialized
across every connection and process and **commit order equals position order**. The
existing `IPollSource` / `ConsumerEngine` poll stays correct with zero changes to the
abstractions.

**Scope correction — stated plainly:** Milestone 7 does **not** deliver unrestricted
concurrent multi-writer append. Append throughput is single-writer, exactly as it is on
SQLite — the gain over SQLite is the client-server model (many machines, managed
service, concurrent *reads* and pooled connections), not parallel appends. A
visibility-watermark poll (let appends run truly concurrently; make the consumer lag
behind the oldest in-flight transaction so it never steps over a not-yet-committed
position) *would* allow it, but it is more code, subtler, and changes the poll contract —
so it is noted here as possible future work, its own milestone, not part of this one.

### 2. The write-guard is the database's grant system, not triggers

`SqliteReadModelStore` enforces "only the owning projection writes these tables" with a
persistent `BEFORE INSERT/UPDATE/DELETE` trigger plus a connection-scoped SQL function
(`writeguard_bypass_active()`) that only the installing connection has registered — any
other connection fires the trigger and fails on the missing function. A
`BeginBypassAsync` scope flips the function's result so the projection's own writes pass.

Postgres has a first-class mechanism for exactly this: **table privileges**. A
projection's `InitAsync` runs `CREATE TABLE` on the store's connection, so that role
*owns* the read-model tables and keeps full rights on them as owner.
`PostgresReadModelStore.InstallWriteGuardAsync` then makes the "nobody else writes"
policy explicit:

- `REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON <table> FROM PUBLIC` — Postgres grants
  `PUBLIC` no table privileges by default, so this is defensive (covers a database whose
  `public` schema has been loosened), self-documenting, and cheap.
- optionally `GRANT SELECT ON <table> TO <readerRole>` when a read-only role is passed to
  `PostgresReadModelStore.OpenAsync(connectionString, readerRole)` — so a dashboard or
  reporting job can query the read models without any write privilege.

Any out-of-band writer — a stray `psql`, app code taking a shortcut, a compromised
read-only credential — simply has no `INSERT`/`UPDATE`/`DELETE` privilege and is refused
by the database with `SQLSTATE 42501` (`insufficient_privilege`). This is enforced by
Postgres itself, not an application trigger.

**Consequence: `BeginBypassAsync` is a genuine no-op** on the Postgres store (it returns
a shared do-nothing `IAsyncDisposable`). The store's own connection is always the owning
role, which is never blocked, so projection bodies — which wrap their writes in
`await using (await store.BeginBypassAsync())` — are byte-identical across providers and
simply don't need a bypass here.

This is the one deliberate **behavioural** difference between the backends, and it only
shows at the seam the SQLite tests probe: `SqliteReadModelStore` rejects a direct write
on the *owning* connection unless it is inside a bypass scope; `PostgresReadModelStore`
never rejects the owner. `test/DotnetCqrs.Tests/Postgres/PostgresWriteGuardTests.cs`
asserts the Postgres model directly (owner writes freely; a throwaway unprivileged
`LOGIN` role cannot write but can still read) rather than porting the SQLite assertions
verbatim.

## Connection model

- **`PostgresEventStore` is pool-aware.** It holds one `NpgsqlDataSource` for its
  lifetime and takes a pooled connection per operation. (`SqliteEventStore` holds a
  single dedicated connection with `Pooling=False` — correct for one local file, wrong
  for a client-server database.)
- **`PostgresReadModelStore` holds one dedicated connection**, like its SQLite sibling —
  a projection is a single sequential consumer, so a pool would buy nothing and the
  `IReadModelStore.Connection` contract wants one connection to hand out.

## Non-goals

- **Postgres streaming replication** — that would be an alternative to Milestone 3's
  LiteFS story, not part of this milestone.
- **`LISTEN`/`NOTIFY`** — `IPollSource.Subscribe` stays in-process-only (a best-effort
  nudge that shortens poll latency); the durable path is still `PollAsync` + a
  checkpoint. `LISTEN/NOTIFY` as the cross-process nudge is a real improvement and a
  clear future addition.
- **A migration framework** — the schema is one idempotent `CREATE TABLE IF NOT EXISTS`
  block, same as `SqliteEventStore.Schema`.
- **A generated Postgres host.** `HostProjectGenerator` and every sample `Program.cs` are
  composition roots that legitimately name a concrete provider; they stay on SQLite.
  Generated *projections* are already provider-neutral (M6) and run on either backend.
- **Sharing a database between backends**, or between `dotnetcqrs` and `pocketcqrs` — the
  same non-goal `docs/interop.md` records, for the same reasons.

## `created` is stored as text

The `events.created` column is `text NOT NULL DEFAULT to_char(now() AT TIME ZONE 'utc',
'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')` — deliberately a string in the same `…T…Z` shape
SQLite's `strftime` default produces, not `timestamptz`. `Event.Created` is a `string`
on the envelope, and keeping the stored form identical across providers matters for the
cross-runtime format sensitivity Milestone 5 documented.

## Running the tests

```sh
ops/postgres/verify.sh          # stands up postgres:17-alpine, runs the subset, tears down
```

or point the suite at any reachable Postgres 14+ yourself:

```sh
export DOTNETCQRS_PG="Host=…;Port=…;Username=…;Password=…;Database=…"
dotnet test --filter FullyQualifiedName~Postgres
```

`DOTNETCQRS_PG` unset → every Postgres test **skips** (not a silent pass). The role must
be privileged enough to `CREATE`/`DROP ROLE` (the default `postgres` superuser is fine);
each test runs inside its own `CREATE SCHEMA "s_<guid>"`, dropped `CASCADE` on teardown.
See `ops/postgres/README.md`.

## Verification run

**2026-08-28**, one Windows machine: .NET SDK 10.0.400, Npgsql 10.0.3, `podman` 5.3.2
running `postgres:17-alpine`. dotnetcqrs @ `<PENDING>` (this milestone's commit).

`DOTNETCQRS_PG` **set** — the Postgres tests execute:

```
$ DOTNETCQRS_PG="Host=localhost;Port=55432;Username=postgres;Password=dev;Database=postgres" \
    dotnet test dotnetcqrs.slnx
Passed!  - Failed:     0, Passed:   123, Skipped:     0, Total:   123, Duration: 57 s
```

`DOTNETCQRS_PG` **unset** — the same tests skip, nothing else changes:

```
$ dotnet test dotnetcqrs.slnx
Passed!  - Failed:     0, Passed:   107, Skipped:    16, Total:   123, Duration: 54 s
```

`ops/postgres/verify.sh` (podman, `postgres:17-alpine`) runs the gated subset hands-off:

```
$ CONTAINER_ENGINE=podman ops/postgres/verify.sh
container engine: podman
starting postgres:17-alpine on :55432 ...
waiting for it to accept connections ...
/var/run/postgresql:5432 - accepting connections
running the Postgres backend tests (should execute, not skip) ...
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 44 s
PASS: Postgres backend suite ran against a real Postgres.
```

16 new tests (107 → 123): `PostgresEventStoreTests` (4 parity + 1 concurrent-append
gaplessness), `PostgresProjectionTests` (3), `PostgresWriteGuardTests` (4),
`PostgresDeadLetterTests` (3), `PostgresAddParamTests` (1). No `src/DotnetCqrs*/` file
outside the new project changed; `DotnetCqrs.Abstractions` is untouched.
