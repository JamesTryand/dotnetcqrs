# dotnetcqrs ↔ pocketcqrs interop (dotnetcqrs-multi-node Milestone 5)

> **Status: verified 2026-08-28** on one machine (both runtimes build and run locally) —
> see "Verification run" at the bottom for the environment, the exact assertions, and two
> behavioural asymmetries the run surfaced.

Milestone 4 gave `ExtCallerConsumer` a second dispatch mode: a follow-up command can be
POSTed to a configured command gateway instead of applied into a local
`DeciderRegistry` (`GatewayFollowUpDispatcher`). That gateway can be *another runtime*.
This milestone is the worked example proving it both ways against a real `pocketcqrs`,
and — more usefully — writing down what actually has to line up for it to work, which is
almost entirely **auth**, not protocol.

## What already matches, for free

Checked directly, not assumed:

- **Route shape.** `pocketcqrs`'s gateway (`gateway.go`) and `DotnetCqrs.Host`'s
  (`CqrsGatewayEndpoints`) both serve `POST /api/cqrs/{aggregate}/{id}/{command}`, body
  = the command payload as JSON, response = `{"events":[...]}` on success, `400` for a
  domain rejection, `409` for a concurrency conflict, `404` for an unknown aggregate.
  (dotnetcqrs also returns `410` for personal data sent for an erased data subject, and
  `400` for a `$pii` envelope in a command payload. `pocketcqrs` has no personal-data
  support yet; its port should match both.)
- **The `task` aggregate.** Both ship a `task` decider with `CreateTask` →
  `TaskCreated`, payload `{"title": "..."}`. `samples/MultiNode` (Milestone 3) and
  `ExtCallerRemoteDispatchTests` (Milestone 4) already use exactly this shape on the
  dotnetcqrs side; `pocketcqrs`'s `aggregates/task.go` is byte-compatible.
- **Provenance headers.** Both read `Causation-Id` / `Correlation-Id` request headers
  into the resulting event's metadata. `GatewayFollowUpDispatcher` already sends them.

So no translation layer is needed. `GatewayFollowUpDispatcher.Create(uri, token)` points
straight at a `pocketcqrs` gateway and works; a `pocketcqrs`-side dispatcher (its
`internal/gatewayclient`, or `extcaller.NewGatewayClient`) points straight at a
`DotnetCqrs.Host` gateway and works.

## Auth is the whole exercise

**There is no single token both gateways accept.** They validate fundamentally
different credentials:

| | validates | issued by |
| --- | --- | --- |
| `pocketcqrs` gateway | a PocketBase auth token (`apis.RequireAuth` → `FindAuthRecordByToken` against its own `data.db`) | that same `pocketcqrs` instance |
| `DotnetCqrs.Host` gateway | whatever ASP.NET Core auth scheme the consuming app configures (JWT bearer against any OIDC/OAuth2 issuer, or nothing) | an external issuer the host app trusts |

`pocketcqrs`'s `entralogin` flow is an OAuth2 *authorization-code* flow that ends by
minting a PocketBase token — it does **not** make the gateway accept a raw external JWT.
So "a shared token issuer" only genuinely applies to the dotnetcqrs side. The alignment
is: **each side presents the credential the *receiver* expects** — two different tokens,
one per direction.

### Direction A — dotnetcqrs → pocketcqrs

dotnetcqrs's `GatewayFollowUpDispatcher` attaches `Authorization: Bearer <token>`. For
`pocketcqrs` to accept it *and* stamp useful metadata, that token must belong to a
record in the collection `pocketcqrs` was started with as
`--cqrsExternalCallerCollection`:

```sh
pocketcqrs serve --tutorial --cqrsExternalCallerCollection=service_accounts
# provision a record in `service_accounts` with a non-empty `name` field,
# auth-with-password as it, pass its token to GatewayFollowUpDispatcher
```

- The event's `actor` is stamped `extcall:<record name>` (here `extcall:dotnetcqrs`).
- `Causation-Id` / `Correlation-Id` from the request are merged into event metadata.

Weaker configurations and why they're not enough for a faithful example:

- **`--cqrsAllowAnonymous`** — `actorMeta` returns `nil` for an unauthenticated request:
  no `actor`, and the provenance headers are dropped too. Fine for a local smoke test,
  not a demonstration of the pattern.
- **A superuser token** — authenticates, so `actor` is stamped (the raw superuser id),
  but `Causation-Id` / `Correlation-Id` are still dropped: `pocketcqrs` gates those to
  `ExternalCallerCollection` members only (`gateway.actorMeta` — an arbitrary
  authenticated caller must not be able to fabricate the causation graph its
  ReactorFlows / catalog explorer draw).
- A record whose `name` field is empty — degrades to the raw record id as `actor`, no
  `extcall:` prefix. Deliberate, documented in `pocketcqrs`; set `name`.

**`actor` never crosses the hop as data.** `GatewayFollowUpDispatcher` sends no actor —
the receiving gateway always derives it from the authenticated caller. This is by
design (`FollowUpDispatch.Actor`'s doc comment): the same follow-up carries a different
actor depending on dispatch mode, and a decider must not make authorization decisions on
it.

### Direction B — pocketcqrs → dotnetcqrs

`DotnetCqrs.Host` validates whatever its consuming app wired up. The
`samples/Interop/DotnetCqrsHost` sample validates a **shared-key HS256 JWT** — the
smallest thing that makes "a shared token issuer" concrete without standing up an OIDC
server. The `samples/Interop/into-dotnetcqrs` driver mints one with the same key /
`iss` / `aud`. In a real deployment this is a bearer JWT from whatever OIDC issuer both
the `pocketcqrs`-side caller and the dotnetcqrs host trust.

- `CqrsGatewayEndpoints.DefaultResolveActor` derives `actor` from the token
  (`oid` / `azp` / `NameIdentifier` / ...). The sample maps the JWT `sub` to
  `NameIdentifier`, so events land with `actor` = the token subject.
- dotnetcqrs has **no `ExternalCallerCollection` allow-list**. `MapCqrsGateway` threads
  `Causation-Id` / `Correlation-Id` into metadata for *any* authenticated caller. If a
  deployment wants that scoped, it layers the scoping on itself, the ordinary ASP.NET
  Core way — it is not a core-library gate the way it is in `pocketcqrs`. State the
  comparison honestly: **pocketcqrs gates provenance to a recognized collection;
  dotnetcqrs leaves the gating to the host app.**

## Two behavioural asymmetries the round trip surfaces

1. **Retry semantics differ.** `pocketcqrs`'s gateway wires an idempotency store
   *unconditionally* at boot: a retried POST with the same `Idempotency-Key` and the
   same body **replays the original response** instead of re-deciding.
   `GatewayFollowUpDispatcher` always sends a deterministic `Idempotency-Key` (derived
   from `FollowUpDispatch.CommandId`), so a redelivered source event is safe against
   `pocketcqrs` for free. `DotnetCqrs.Host` has **no idempotency store yet** — it
   ignores the header and re-decides, so a redelivered `CreateTask` there produces a
   `400` ("already exists"), not a replayed `200`. Same dispatcher, different retry
   contract depending on the target. (`verify.sh`'s duplicate-command checks use a
   fresh key precisely so they exercise the *decider's* rejection on both sides rather
   than `pocketcqrs`'s replay.)
2. **Provenance gating differs** — see Direction B above.

## Explicit non-goal

Sharing one SQLite `events.db` file between a `dotnetcqrs` process and a `pocketcqrs`
process. The schemas are close (`events` table, same `UNIQUE (aggregate, aggregate_id,
sequence)`) but not identical (checkpoint table name, `pocketcqrs`'s extra `meta` table
and `commandId` index, the `created` timestamp format), and even fully aligned, two
different runtimes writing the same file concurrently is a materially different risk
than `pocketcqrs`'s own multi-node story (one codebase, one locking discipline).
Revisit only against a concrete need.

## Running it

`samples/Interop/verify.sh` boots a real `pocketcqrs` gateway and a real
`DotnetCqrs.Host` gateway, provisions the `service_accounts` external-caller record,
then drives `CreateTask` **both ways** and asserts against each event store — the
command landing as a real event, the expected `actor`, the provenance metadata, and a
duplicate being refused `400` end to end.

```sh
POCKETCQRS_SRC=/path/to/pocketcqrs samples/Interop/verify.sh
```

Needs `go`, `dotnet` (SDK 10), `curl`, `jq`, `sqlite3` on `PATH`, and a `pocketcqrs`
checkout (the Gogs `lab/pocketcqrs` repo). See `samples/Interop/README.md` for the
pieces.

## Verification run

**2026-08-28**, one Windows machine: .NET SDK 10.0.400, Go 1.26.5, `pocketcqrs`
(PocketBase v0.39.10) built from source. dotnetcqrs @ `c7e7988`, pocketcqrs @
`ec60915` (Gogs `lab/pocketcqrs`).

`POCKETCQRS_SRC=/c/dev/repo/pocketcqrs samples/Interop/verify.sh`:

```
== direction A: dotnetcqrs -> pocketcqrs ==
OK: CreateTask(t-A1) dispatched into http://127.0.0.1:8890
  PASS: GatewayFollowUpDispatcher dispatch accepted
  PASS: command landed as a real event on pocketcqrs
  PASS: pocketcqrs stamped the external-caller actor
  PASS: pocketcqrs honoured Causation-Id from the recognized caller
  PASS: pocketcqrs honoured Correlation-Id from the recognized caller
OK: gateway rejected as expected: 400 {"data":{},"message":"Task already exists.","status":400}
  PASS: duplicate CreateTask refused (400) by pocketcqrs
== direction B: pocketcqrs -> dotnetcqrs ==
OK: CreateTask(t-B1) dispatched into http://127.0.0.1:8891
  PASS: gatewayclient-shaped dispatch accepted
  PASS: command landed as a real event on dotnetcqrs
  PASS: dotnetcqrs derived actor from the shared-issuer JWT sub
  PASS: dotnetcqrs threaded Causation-Id (no allow-list gate)
  PASS: dotnetcqrs threaded Correlation-Id (no allow-list gate)
OK: gateway rejected as expected: 400 {"...","title":"Bad Request","status":400,"detail":"task already exists"}
  PASS: duplicate CreateTask refused (400) by dotnetcqrs

ALL PASS  (dotnetcqrs @ c7e7988, pocketcqrs @ ec60915)
```

What each direction actually exercised:

- **A**: `GatewayFollowUpDispatcher` (Milestone 4, unmodified) POSTed `CreateTask` into
  `pocketcqrs`'s gateway authenticated as the `service_accounts` record `dotnetcqrs`.
  The event on `pocketcqrs`'s `events.db` carried `actor: "extcall:dotnetcqrs"`,
  `causationId: "interop-cause-A1"`, `correlationId: "interop-corr-A1"`. A second
  attempt (fresh idempotency key) hit the decider's create-guard and came back `400`.
- **B**: a stdlib-only Go driver mirroring `internal/gatewayclient.(*Client).Dispatch`'s
  exact request (route, headers, deterministic sha256 `Idempotency-Key`) POSTed
  `CreateTask` into `DotnetCqrs.Host`, authenticated with a shared-key HS256 JWT the
  host validated. The event on the host's `events.db` carried
  `actor: "pocketcqrs-extcaller"` (from the JWT `sub`),
  `causationId: "interop-cause-B1"`, `correlationId: "interop-corr-B1"`. A second
  attempt was re-decided (no idempotency store) and refused `400`.

The two asymmetries above (idempotent replay on `pocketcqrs`, provenance-gating) were
found during this run, not designed around in advance — an earlier `verify.sh` asserted
direction A's duplicate would `400` with the *same* idempotency key and got a replayed
`200` instead.

No change to `src/` or the 104-test suite — Milestone 5 is `samples/Interop/` + this
doc, the same "harness, not in-suite test" shape Milestone 3 used for its cross-runtime
verification.
