# Interop sample (dotnetcqrs-multi-node Milestone 5)

The runnable proof that a `dotnetcqrs` gateway and a `pocketcqrs` gateway can dispatch
commands into each other. Not a feature demo — a verification harness, the same shape
Milestone 3 used for cross-host replication. The design notes (why it's almost entirely
an auth exercise, the two behavioural asymmetries) are in
[`docs/interop.md`](../../docs/interop.md).

## Pieces

| path | what it is |
| --- | --- |
| `DotnetCqrsHost/` | a minimal `DotnetCqrs.Host` gateway for the `task` aggregate, with a real auth check on the command route: a hand-rolled HS256 JWT verifier (`System.Security.Cryptography` only, no `Microsoft.AspNetCore.Authentication.JwtBearer`). Reads `EVENTS_DB_PATH`, `LISTEN_URL`, `INTEROP_JWT_{KEY,ISSUER,AUDIENCE}`. Also serves `GET /events/{aggregate}/{id}` and `GET /healthz`. |
| `IntoPocketCqrs/` | direction A driver: uses Milestone 4's `GatewayFollowUpDispatcher` (unmodified) to POST `CreateTask` into a `pocketcqrs` gateway. Reads `POCKETCQRS_URL`, `SVC_TOKEN`, `CAUSATION_ID`, `CORRELATION_ID`. `--expect-reject` asserts a `400`. |
| `into-dotnetcqrs/` | direction B driver: a stdlib-only Go program mirroring `pocketcqrs`'s `internal/gatewayclient.(*Client).Dispatch` request shape by hand (that package is `internal/` to the pocketcqrs module and can't be imported here). Mints the shared-key HS256 JWT. Reads `DOTNETCQRS_URL`, `INTEROP_JWT_*`, `CAUSATION_ID`, `CORRELATION_ID`. `--expect-reject` asserts a `400`. |
| `verify.sh` | boots both gateways, provisions the `service_accounts` external-caller record in `pocketcqrs`, runs both directions, asserts against each `events.db` with `sqlite3`. |

## Running

```sh
POCKETCQRS_SRC=/path/to/pocketcqrs samples/Interop/verify.sh
```

Prereqs on `PATH`: `go`, `dotnet` (SDK 10), `curl`, `jq`, `sqlite3`. `POCKETCQRS_SRC`
must point at a `pocketcqrs` checkout (the Gogs `lab/pocketcqrs` repo — same one the
`dotnetcqrs`-family issues reference). Optional env: `PC_ADDR` / `DC_ADDR` (the two
listen addresses; defaults `127.0.0.1:8890` / `:8891`), `KEEP_WORK=1` (keep the temp
dir for inspection).

The drivers also run standalone against already-running gateways — see each `--expect`
usage line and the env vars above.

## Keeping the Go driver honest

`into-dotnetcqrs/main.go` is a hand copy of `pocketcqrs/internal/gatewayclient`'s
request construction (route, `Authorization` / `Content-Type` / `Idempotency-Key` /
`Causation-Id` / `Correlation-Id`, the deterministic sha256 key). If `gatewayclient.go`
changes shape, update this to match — it's a stand-in for that client, not an
independent implementation.
