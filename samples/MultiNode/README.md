# MultiNode sample

The runnable pair for dotnetcqrs-multi-node Milestone 3 (cross-host replication via
LiteFS). Not a feature demo -- a smoke test proving Milestones 1 and 2's app-level
machinery (`SqliteEventStore.OpenReadOnlyAsync`, `CqrsGatewayEndpoints.ForwardTo`)
keeps working unchanged once the shared `events.db` arrives over LiteFS replication
between two real hosts instead of being one literal shared path on one host.

- **MultiNode.Primary** -- the master. Opens `events.db` at `EVENTS_DB_PATH` (defaults
  to `/litefs/events.db`, i.e. inside a LiteFS FUSE mount) the ordinary way
  (`SqliteEventStore.OpenAsync`) and serves the gateway with no forwarding. Has no
  LiteFS-specific code -- LiteFS intercepts writes to the mounted path transparently.
- **MultiNode.Secondary** -- opens the *same path* read-only
  (`SqliteEventStore.OpenReadOnlyAsync`), on this host that's LiteFS's replicated copy;
  runs its own `ConsumerEngine` + a tiny `tasks` projection into a local read model
  (`GET /tasks/{id}`); forwards every write to `PRIMARY_URL` via
  `CqrsGatewayEndpoints.ForwardTo`.

Neither project references LiteFS's SDK or config -- both just read/write an ordinary
SQLite file path. That's the point: LiteFS's job is making the *file* show up
correctly on both hosts; dotnetcqrs's job (Milestones 1-2, already done) is knowing
what to do once it's there.

Not meant to be run directly with `dotnet run` for the cross-host story -- see
`ops/litefs/` for the two-node docker-compose setup and `docs/cross-host-replication.md`
for how it fits together. (Each project *will* run standalone with `dotnet run` against
a local path for a quick compile/smoke check; it just won't prove anything about
replication on its own.)
