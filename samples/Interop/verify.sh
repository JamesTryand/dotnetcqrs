#!/usr/bin/env bash
# dotnetcqrs-multi-node Milestone 5: verified dotnetcqrs <-> pocketcqrs interop.
#
# Boots a real pocketcqrs gateway and a real dotnetcqrs gateway, then drives a `task`
# CreateTask command BOTH ways between them and asserts against each event store:
#
#   direction A  dotnetcqrs -> pocketcqrs   via Milestone 4's GatewayFollowUpDispatcher
#                (IntoPocketCqrs), authenticating as a --cqrsExternalCallerCollection
#                record so actor + Causation-Id/Correlation-Id are honoured
#   direction B  pocketcqrs -> dotnetcqrs   via a thin gatewayclient-shaped driver
#                (into-dotnetcqrs), authenticating with a shared-key HS256 JWT the
#                DotnetCqrsHost sample validates
#
# Each direction also re-sends the same command with --expect-reject to confirm the
# duplicate is refused (400) end to end.
#
# Prereqs on PATH: go, dotnet (SDK 10), curl, jq, sqlite3.
# Required env:
#   POCKETCQRS_SRC   path to a pocketcqrs checkout (the Gogs lab/pocketcqrs repo)
# Optional env:
#   PC_ADDR (127.0.0.1:8890)  DC_ADDR (127.0.0.1:8891)  KEEP_WORK (1 = keep tmp dir)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
: "${POCKETCQRS_SRC:?set POCKETCQRS_SRC to a pocketcqrs checkout}"
POCKETCQRS_SRC="$(cd "$POCKETCQRS_SRC" && pwd)"
PC_ADDR="${PC_ADDR:-127.0.0.1:8890}"
DC_ADDR="${DC_ADDR:-127.0.0.1:8891}"

ADMIN_PW="interop-admin-pw-123"
SVC_PW="interop-svc-pw-123"
JWT_KEY="interop-shared-hs256-key-not-for-production-0123456789"

for tool in go dotnet curl jq sqlite3; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool" >&2; exit 1; }
done

WORK="$(mktemp -d)"
PIDS=()
FAILED=0
cleanup() {
  for pid in "${PIDS[@]:-}"; do kill "$pid" 2>/dev/null || true; done
  for pid in "${PIDS[@]:-}"; do wait "$pid" 2>/dev/null || true; done
  if [ "${KEEP_WORK:-}" = "1" ]; then echo "kept work dir: $WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

pass() { echo "  PASS: $1"; }
fail() { echo "  FAIL: $1"; FAILED=1; }
assert_eq() { [ "$1" = "$2" ] && pass "$3" || fail "$3 (want '$2', got '$1')"; }
assert_contains() { case "$1" in *"$2"*) pass "$3";; *) fail "$3 (got: $1)";; esac; }
# `|| true`: a sqlite3 failure (locked/missing db) must surface as a failed assertion
# below, not kill the script under `set -e` before the diagnostics block runs.
q() { sqlite3 "$1" "$2" 2>&1 || true; }
wait_for() { # url label
  for _ in $(seq 1 120); do curl -sf "$1" >/dev/null 2>&1 && return 0; sleep 0.5; done
  echo "timed out waiting for $2 ($1)" >&2; return 1
}

DC_SHA="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
PC_SHA="$(git -C "$POCKETCQRS_SRC" rev-parse --short HEAD)"
echo "dotnetcqrs @ $DC_SHA   pocketcqrs @ $PC_SHA"
echo "work dir: $WORK"

echo "== build =="
( cd "$POCKETCQRS_SRC" && go build -o "$WORK/pocketcqrs" . )
( cd "$REPO_ROOT/samples/Interop/into-dotnetcqrs" && go build -o "$WORK/into-dc" . )
dotnet build "$REPO_ROOT/samples/Interop/DotnetCqrsHost" -c Release -o "$WORK/host"    -v q --nologo
dotnet build "$REPO_ROOT/samples/Interop/IntoPocketCqrs" -c Release -o "$WORK/into-pc" -v q --nologo

# go build -o may or may not append .exe depending on platform.
PC_BIN="$WORK/pocketcqrs"; [ -f "$PC_BIN.exe" ] && PC_BIN="$PC_BIN.exe"
DC_BIN="$WORK/into-dc";    [ -f "$DC_BIN.exe" ] && DC_BIN="$DC_BIN.exe"

echo "== boot pocketcqrs =="
"$PC_BIN" superuser upsert "admin@interop.test" "$ADMIN_PW" --dir "$WORK/pb_data" >/dev/null
"$PC_BIN" serve \
  --dir "$WORK/pb_data" --http "$PC_ADDR" \
  --tutorial --cqrsExternalCallerCollection=service_accounts \
  >"$WORK/pocketcqrs.log" 2>&1 &
PIDS+=($!)
wait_for "http://$PC_ADDR/api/health" "pocketcqrs"

SU_TOKEN="$(curl -sf -X POST "http://$PC_ADDR/api/collections/_superusers/auth-with-password" \
  -H "Content-Type: application/json" \
  -d "{\"identity\":\"admin@interop.test\",\"password\":\"$ADMIN_PW\"}" | jq -r .token)"

# The external-caller identity dotnetcqrs authenticates as for direction A.
curl -sf -X POST "http://$PC_ADDR/api/collections" \
  -H "Authorization: Bearer $SU_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"service_accounts","type":"auth","fields":[{"name":"name","type":"text"}]}' >/dev/null
curl -sf -X POST "http://$PC_ADDR/api/collections/service_accounts/records" \
  -H "Authorization: Bearer $SU_TOKEN" -H "Content-Type: application/json" \
  -d "{\"email\":\"svc@interop.test\",\"password\":\"$SVC_PW\",\"passwordConfirm\":\"$SVC_PW\",\"name\":\"dotnetcqrs\"}" >/dev/null
SVC_TOKEN="$(curl -sf -X POST "http://$PC_ADDR/api/collections/service_accounts/auth-with-password" \
  -H "Content-Type: application/json" \
  -d "{\"identity\":\"svc@interop.test\",\"password\":\"$SVC_PW\"}" | jq -r .token)"
[ -n "$SVC_TOKEN" ] && [ "$SVC_TOKEN" != "null" ] || { echo "failed to mint service_accounts token" >&2; exit 1; }

echo "== boot dotnetcqrs host =="
EVENTS_DB_PATH="$WORK/dc-events.db" LISTEN_URL="http://$DC_ADDR" \
INTEROP_JWT_KEY="$JWT_KEY" INTEROP_JWT_ISSUER="interop-issuer" INTEROP_JWT_AUDIENCE="dotnetcqrs-interop" \
  dotnet "$WORK/host/DotnetCqrsHost.dll" >"$WORK/host.log" 2>&1 &
PIDS+=($!)
wait_for "http://$DC_ADDR/healthz" "dotnetcqrs host"

PC_EVENTS="$WORK/pb_data/events.db"
DC_EVENTS="$WORK/dc-events.db"

echo "== direction A: dotnetcqrs -> pocketcqrs =="
SVC_TOKEN="$SVC_TOKEN" POCKETCQRS_URL="http://$PC_ADDR" \
CAUSATION_ID="interop-cause-A1" CORRELATION_ID="interop-corr-A1" \
  dotnet "$WORK/into-pc/IntoPocketCqrs.dll" t-A1 "from dotnetcqrs" \
  && pass "GatewayFollowUpDispatcher dispatch accepted" || fail "GatewayFollowUpDispatcher dispatch"
a_type="$(q "$PC_EVENTS" "SELECT type FROM events WHERE aggregate='task' AND aggregate_id='t-A1' ORDER BY sequence LIMIT 1")"
a_meta="$(q "$PC_EVENTS" "SELECT metadata FROM events WHERE aggregate='task' AND aggregate_id='t-A1' ORDER BY sequence LIMIT 1")"
assert_eq "$a_type" "TaskCreated" "command landed as a real event on pocketcqrs"
assert_contains "$a_meta" '"actor":"extcall:dotnetcqrs"' "pocketcqrs stamped the external-caller actor"
assert_contains "$a_meta" '"causationId":"interop-cause-A1"' "pocketcqrs honoured Causation-Id from the recognized caller"
assert_contains "$a_meta" '"correlationId":"interop-corr-A1"' "pocketcqrs honoured Correlation-Id from the recognized caller"
SVC_TOKEN="$SVC_TOKEN" POCKETCQRS_URL="http://$PC_ADDR" \
  dotnet "$WORK/into-pc/IntoPocketCqrs.dll" t-A1 "from dotnetcqrs" --expect-reject \
  && pass "duplicate CreateTask refused (400) by pocketcqrs" || fail "duplicate CreateTask should 400"
assert_eq "$(q "$PC_EVENTS" "SELECT count(*) FROM events WHERE aggregate='task' AND aggregate_id='t-A1'")" "1" \
  "duplicate was refused by the decider, not partially applied (exactly one t-A1 event)"

echo "== direction B: pocketcqrs -> dotnetcqrs =="
DOTNETCQRS_URL="http://$DC_ADDR" INTEROP_JWT_KEY="$JWT_KEY" \
CAUSATION_ID="interop-cause-B1" CORRELATION_ID="interop-corr-B1" \
  "$DC_BIN" t-B1 "from pocketcqrs" \
  && pass "gatewayclient-shaped dispatch accepted" || fail "gatewayclient-shaped dispatch"
b_type="$(q "$DC_EVENTS" "SELECT type FROM events WHERE aggregate='task' AND aggregate_id='t-B1' ORDER BY sequence LIMIT 1")"
b_meta="$(q "$DC_EVENTS" "SELECT metadata FROM events WHERE aggregate='task' AND aggregate_id='t-B1' ORDER BY sequence LIMIT 1")"
assert_eq "$b_type" "TaskCreated" "command landed as a real event on dotnetcqrs"
assert_contains "$b_meta" '"actor":"pocketcqrs-extcaller"' "dotnetcqrs derived actor from the shared-issuer JWT sub"
assert_contains "$b_meta" '"causationId":"interop-cause-B1"' "dotnetcqrs threaded Causation-Id (no allow-list gate)"
assert_contains "$b_meta" '"correlationId":"interop-corr-B1"' "dotnetcqrs threaded Correlation-Id (no allow-list gate)"
DOTNETCQRS_URL="http://$DC_ADDR" INTEROP_JWT_KEY="$JWT_KEY" \
  "$DC_BIN" t-B1 "from pocketcqrs" --expect-reject \
  && pass "duplicate CreateTask refused (400) by dotnetcqrs" || fail "duplicate CreateTask should 400"
assert_eq "$(q "$DC_EVENTS" "SELECT count(*) FROM events WHERE aggregate='task' AND aggregate_id='t-B1'")" "1" \
  "duplicate was refused by the decider, not partially applied (exactly one t-B1 event)"

echo
if [ "$FAILED" = "0" ]; then
  echo "ALL PASS  (dotnetcqrs @ $DC_SHA, pocketcqrs @ $PC_SHA)"
else
  echo "FAILURES ABOVE  (dotnetcqrs @ $DC_SHA, pocketcqrs @ $PC_SHA)"
  echo "pocketcqrs.log tail:"; tail -n 20 "$WORK/pocketcqrs.log" || true
  echo "host.log tail:";       tail -n 20 "$WORK/host.log" || true
fi
exit "$FAILED"
