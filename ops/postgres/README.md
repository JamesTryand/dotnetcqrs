# ops/postgres — disposable Postgres for the Milestone 7 backend tests

`src/DotnetCqrs.Postgres/` (the Npgsql implementation of `IEventStore` /
`IReadModelStore` / `IDeadLetterStore`) has real in-suite tests under
`test/DotnetCqrs.Tests/Postgres/`. They are `[SkippableFact]`s gated on the
`DOTNETCQRS_PG` environment variable: **set → they run; unset → they skip** (never a
silent pass). This directory is the one-command way to give them a database.

## One command

```sh
ops/postgres/verify.sh
```

Stands up `postgres:17-alpine` (auto-detects `docker` or `podman`; override with
`CONTAINER_ENGINE=`), waits for it, exports `DOTNETCQRS_PG`, runs
`dotnet test --filter FullyQualifiedName~Postgres`, and removes the container on exit.

## Manual

```sh
docker compose -f ops/postgres/compose.yml up -d          # or: podman run -d --name pg -e POSTGRES_PASSWORD=dev -p 55432:5432 postgres:17-alpine
export DOTNETCQRS_PG="Host=localhost;Port=55432;Username=postgres;Password=dev;Database=postgres"
dotnet test --filter FullyQualifiedName~Postgres
docker compose -f ops/postgres/compose.yml down -v
```

## What the database needs

- A **privileged role** — the default `postgres` superuser is fine. `PostgresWriteGuardTests`
  creates and drops throwaway `LOGIN` roles to prove an out-of-band writer is refused by
  the grant system.
- Nothing else. Every test runs inside its own `CREATE SCHEMA "s_<guid>"`, dropped
  `CASCADE` on teardown (`PostgresFixture`), so repeated runs need no cleanup and any
  reachable Postgres 14+ works (`GENERATED ALWAYS AS IDENTITY`, `MERGE`-free upserts).

See `docs/postgres-backend.md` for the design — the advisory-lock append serialization
and the grant-based write-guard in particular.
