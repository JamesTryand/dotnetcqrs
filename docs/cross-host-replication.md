# Cross-host replication (dotnetcqrs-multi-node Milestone 3)

> **Status: verified 2026-08-28** on two separate physical machines — see
> "Verification run" at the bottom for the environment, what was checked, the observed
> replication lag, and the three things that had to change from the compose-only draft.

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
on one Docker host; `ops/litefs/two-host/` runs the same two images on two separate
machines (primary on host A, secondary on host B). Both were run on 2026-08-28 (see
"Verification run" below) — the compose pair as a same-host baseline, then the two-host
setup for real. LiteFS replicates over an ordinary HTTP connection between two separate
filesystems either way; the two-host run is what forced out the address/port details
the compose network papered over.

## Running it

Same-host baseline (two containers, one Docker host):

```sh
cd ops/litefs
docker compose up --build
# in another shell, once both containers report healthy:
./verify.sh
```

Two separate machines: see `ops/litefs/two-host/README.md` — `run-primary.sh` on
host A, `run-secondary.sh` on host B, then `verify.sh` with `PRIMARY_URL` /
`SECONDARY_URL` pointed at the two hosts.

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
around building `consul` support). Both files point `lease.advertise-url` at the
primary's address — that's how a static-lease replica finds the primary; only
`lease.candidate` differs between the two files. Verified against
`superfly/litefs-example`'s own real `litefs.static-lease.yml`, not guessed.

The address itself comes from `$LITEFS_ADVERTISE_URL` (LiteFS expands `${...}` in the
config file — checked against LiteFS 0.5.14). `docker-compose.yml` sets it to the
compose service name (`http://primary:20202`); `ops/litefs/two-host/` sets it to the
primary host's real routable address. LiteFS has no `${VAR:-default}` syntax, so
whatever launches the container must always set the variable.

## Verification run

**2026-08-28**, two Ubuntu 24.04.3 nodes on a home LAN (`m4` = primary, `m5` =
secondary), Docker 28.3.3, LiteFS 0.5.14, .NET 10 images.

- **Baseline first** (advisor's steer — isolates FUSE/LiteFS from the cross-host
  networking): the two loaded images run as two containers on `m4` with a user-defined
  bridge network, reproducing `docker-compose.yml`. `verify.sh` → PASS. Confirms the
  FUSE mount works under Docker + AppArmor on 24.04 with `--privileged` alone (no
  `apparmor=unconfined` needed), Milestone 1's `OpenReadOnlyAsync` reads the
  LiteFS-replicated copy, Milestone 2's forward + the `400` domain rejection survive a
  real HTTP hop.
- **Two hosts**: `run-primary.sh` on `m4`, `run-secondary.sh` on `m5`, `verify.sh`
  with `PRIMARY_URL`/`SECONDARY_URL` at the two node addresses → PASS. `events.db` on
  the two nodes are genuinely separate files on separate disks; after the run both hold
  the same event count. A `CreateTask` posted to `m5` was committed as event
  `sequence 1` on `m4` and appeared in `m5`'s own `tasks` read model — full
  round-trip (forward hop + LiteFS replication + consumer poll) measured at **~0.6 s**
  end to end; `verify.sh` polls at 1 s granularity so it reports 1–2 s.

Three things had to change from the compose-only draft, all now in the repo:

1. **`advertise-url` must be a routable address, not a compose alias**, and the primary
   must **publish `20202`** (the LiteFS API port). Compose never published it because
   service-DNS made it reachable on the compose network; across hosts the replica dials
   it directly. Handled via `$LITEFS_ADVERTISE_URL` + `run-primary.sh -p 20202:20202`.
2. **The secondary's app port must be configurable.** `m5` runs Kubernetes; its
   `kube-router` already held `:8080`, so `run-secondary.sh` takes `SECONDARY_PORT`
   (default `8082`), and `verify.sh` takes `PRIMARY_URL`/`SECONDARY_URL`.
3. **LiteFS logs `http server listening on: http://localhost:20202`** — the word
   `localhost` in that line is cosmetic. Checked directly: inside the primary
   container `/proc/net/tcp6` shows the `:20202` socket bound to `::` (all interfaces,
   dual-stack), and the same-host baseline reached `primary:20202` from another
   container with the port *not* published at all. So no `http:` block is needed; the
   only cross-host requirement is that the primary **publish** `20202` like any
   container port so packets from the other host reach its network namespace.

Not run on the nodes themselves: `docker build`. The lab nodes' outbound internet was
~10 KB/s during the run — far too slow to pull the .NET base images — so both images
were built off-node (podman, this repo's unchanged `Dockerfile`) and shipped with
`docker save | ssh 'docker load'`. The images are exactly what `docker build` produces;
only the delivery route differed.
