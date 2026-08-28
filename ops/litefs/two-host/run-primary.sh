#!/usr/bin/env bash
# dotnetcqrs-multi-node Milestone 3 -- start the write master on this host.
# See ops/litefs/two-host/README.md. Assumes `multinode-primary` is loaded locally.
set -euo pipefail

PRIMARY_ADDR="${PRIMARY_ADDR:?set PRIMARY_ADDR to the routable address of this host}"
IMAGE="${IMAGE:-multinode-primary:latest}"
NAME="${NAME:-primary}"
GATEWAY_PORT="${GATEWAY_PORT:-8080}"
LITEFS_PORT="${LITEFS_PORT:-20202}"

docker rm -f "$NAME" 2>/dev/null || true
exec docker run -d --name "$NAME" --privileged \
  -p "${GATEWAY_PORT}:8080" \
  -p "${LITEFS_PORT}:20202" \
  -e EVENTS_DB_PATH=/litefs/events.db \
  -e "LITEFS_ADVERTISE_URL=http://${PRIMARY_ADDR}:${LITEFS_PORT}" \
  "$IMAGE"
