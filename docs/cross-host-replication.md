# Cross-host replication (dotnetcqrs-multi-node Milestone 3)

> **Status: authored, not yet run.** This was written on a machine with neither Docker
> nor WSL installed, so nothing below has actually been executed or verified against a
> real LiteFS mount. Treat the YAML and Dockerfile as a well-researched first draft,
> not a proven-working setup — run `ops/litefs/verify.sh` on a real Linux host (Docker
> with FUSE support) before trusting it, and fix forward from whatever it finds. See
> this issue's `NEXT_SESSION_PROMPT.md` for the concrete next step.

Milestones 1-2 built the app-level pieces of a single-master, multiple-reader
topology — `SqliteEventStore.OpenReadOnlyAsync` (a same-host secondary reads the
master's `events.db` directly) and `CqrsGatewayEndpoints.ForwardTo` (a secondary
proxies writes to the master). Both only needed one machine: the "replication" was
just two processes opening the same file path.

Milestone 3 is what happens once the two nodes are on **separate hosts** with separate
filesystems — something has to actually copy `events.db` (well-formed, WAL-consistent,
continuously) from the master's disk onto the secondary's. That something is
[LiteFS](https://github.com/superfly/litefs): a FUSE filesystem that intercepts writes
to the mounted database and streams the resulting WAL frames to replicas over HTTP,
typically with 1-2 second lag.

**Reused, not re-derived**: `pocketcqrs` already evaluated Litestream for this and
rejected it (its `restore -f` follow mode stalled 30-90+ seconds under a
continuously-polling reader — exactly what a live secondary does) before landing on
LiteFS. This issue's README carries that decision forward rather than re-running the
evaluation.

## The shape

Nothing in `dotnetcqrs` is LiteFS-aware. `samples/MultiNode/MultiNode.Primary` and
`MultiNode.Secondary` are Milestones 1-2's own mechanisms, unmodified, pointed at a
path that happens to live inside a LiteFS FUSE mount:

- **Primary**: `SqliteEventStore.OpenAsync("/litefs/events.db")` — LiteFS intercepts
  every write transparently. No app code needed to know LiteFS exists.
- **Secondary**: `SqliteEventStore.OpenReadOnlyAsync("/litefs/events.db")` — on this
  host, `/litefs/events.db` is LiteFS's continuously-updated replicated copy, not the
  same inode as the primary's. Everything downstream (`ConsumerEngine`, the local
  `tasks` projection, `CqrsGatewayEndpoints.ForwardTo` proxying writes back to the
  primary over HTTP) is identical to Milestone 1/2's same-host code.

`ops/litefs/` stands the pair up as two Docker containers (see `docker-compose.yml`)
in place of two real hosts — a genuine second machine wasn't available when this was
authored (see this issue's `NEEDS.md`); two containers on the home-lab or any Docker
host prove the same thing, since LiteFS replicates over an ordinary HTTP connection
between two separate filesystems either way.

## Running it

```sh
cd ops/litefs
docker compose up --build
# in another shell, once both containers report healthy:
./verify.sh
```

`verify.sh` posts a command to the *secondary*, confirms it forwards to the primary
and commits, confirms a duplicate still forwards its domain-rejection `400`, then
polls the secondary's own local read model until the write shows up — proving the
`events.db` LiteFS copied onto the secondary's filesystem is genuinely live, not a
stale or empty file.

## Hard guardrail (carried over from `pocketcqrs`'s own LiteFS notes)

**Never point a secondary at a network-mounted (NFS/SMB) copy of the file as a
substitute for LiteFS.** WAL mode's `-shm` coordination file needs shared-memory
semantics those filesystems don't reliably provide — this is a documented
incompatibility, not a scale-dependent risk that only shows up under load.

The master's own `events.db` must be opened from *inside* the FUSE mount
(`/litefs/events.db` above), not a plain path pointed at it from outside the mount.

## What this setup deliberately does NOT use

LiteFS ships its own reverse-proxy/write-redirect feature (`proxy:` in `litefs.yml`,
using `primary-redirect-timeout` to hold a write until the primary is reachable). This
setup does not configure it — `dotnetcqrs`'s own Milestone 2 gateway-level forwarding
(`CqrsGatewayEndpoints.ForwardTo`) already fills that role, and using both at once
would proxy the same request twice through two different mechanisms for no benefit.
Keeping the write path at the application layer also keeps it visible to
`verify.sh`'s own assertions (a `400` domain rejection has to survive the forward,
which a transport-level proxy wouldn't distinguish from a success).

## Static lease, not consul

`lease.type: static` in both `litefs-primary.yml`/`litefs-secondary.yml` — one fixed
primary, no automatic failover, matching this issue's README (`dotnetcqrs` has no
multi-node failover story to begin with; `static` is the honest choice, not a shortcut
around building `consul` support). Both files point `lease.advertise-url` at
`http://primary:20202` (the primary's own address) — that's how a static-lease replica
finds the primary; only `lease.candidate` differs between the two files. Verified
against `superfly/litefs-example`'s own real `litefs.static-lease.yml`, not guessed.
