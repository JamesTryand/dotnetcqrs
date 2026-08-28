#!/usr/bin/env bash
# dotnetcqrs-multi-node Milestone 3 smoke test. Run after `docker compose up --build`
# from this directory (ops/litefs/). Proves, over real HTTP between two containers on
# two different filesystems (not TestServer, not a shared path):
#   1. a command posted to the SECONDARY's gateway forwards to the primary and commits
#      (Milestone 2, unchanged, now over a real network hop)
#   2. a duplicate command still forwards its domain-rejection 400
#   3. the write shows up on the secondary's own local read model within LiteFS's
#      replication window, proving the events.db LiteFS copied onto the secondary's
#      filesystem is genuinely being read by Milestone 1's OpenReadOnlyAsync + its
#      ConsumerEngine -- not a stale/empty file
set -euo pipefail

# Default to the docker-compose port mapping; override both for the two-host setup
# (ops/litefs/two-host/), e.g. PRIMARY_URL=http://10.0.0.14:8080 SECONDARY_URL=http://10.0.0.15:8082 ./verify.sh
PRIMARY_URL="${PRIMARY_URL:-http://localhost:8081}"
SECONDARY_URL="${SECONDARY_URL:-http://localhost:8082}"
TASK_ID="verify-$(date +%s)"

wait_healthy() {
  local url="$1" name="$2"
  echo "waiting for $name ($url/healthz)..."
  for _ in $(seq 1 30); do
    if curl -fsS "$url/healthz" >/dev/null 2>&1; then
      echo "  $name is up"
      return 0
    fi
    sleep 1
  done
  echo "  $name never became healthy" >&2
  exit 1
}

wait_healthy "$PRIMARY_URL" primary
wait_healthy "$SECONDARY_URL" secondary

echo "posting CreateTask for $TASK_ID to the SECONDARY (should forward to primary)..."
status=$(curl -sS -o /tmp/verify-create.json -w '%{http_code}' \
  -X POST "$SECONDARY_URL/api/cqrs/task/$TASK_ID/CreateTask" \
  -H 'Content-Type: application/json' \
  -d '{"title":"cross-host replication smoke test"}')
if [ "$status" != "200" ]; then
  echo "FAIL: expected 200 from forwarded CreateTask, got $status" >&2
  cat /tmp/verify-create.json >&2
  exit 1
fi
echo "  OK (200, forwarded and committed on primary)"

echo "posting duplicate CreateTask (should forward and still reject with 400)..."
dup_status=$(curl -sS -o /dev/null -w '%{http_code}' \
  -X POST "$SECONDARY_URL/api/cqrs/task/$TASK_ID/CreateTask" \
  -H 'Content-Type: application/json' \
  -d '{"title":"duplicate"}')
if [ "$dup_status" != "400" ]; then
  echo "FAIL: expected 400 on duplicate forwarded command, got $dup_status" >&2
  exit 1
fi
echo "  OK (400, domain rejection forwarded correctly)"

echo "polling the SECONDARY's own local read model for LiteFS replication to catch up..."
for i in $(seq 1 15); do
  code=$(curl -sS -o /tmp/verify-task.json -w '%{http_code}' "$SECONDARY_URL/tasks/$TASK_ID")
  if [ "$code" = "200" ]; then
    echo "  OK: secondary's read model sees the task after ${i}s"
    cat /tmp/verify-task.json
    echo
    echo "PASS: cross-host replication (LiteFS) + write-forwarding (Milestone 2) + read-only replica (Milestone 1) all compose across two real hosts."
    exit 0
  fi
  sleep 1
done

echo "FAIL: secondary never saw the task in its local read model within 15s -- check LiteFS replication (docker compose logs secondary)" >&2
exit 1
