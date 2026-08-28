#!/usr/bin/env bash
# dotnetcqrs-multi-node Milestone 3 -- start the read/forward replica on this host.
# See ops/litefs/two-host/README.md. Assumes `multinode-secondary` is loaded locally.
set -euo pipefail

PRIMARY_ADDR="${PRIMARY_ADDR:?set PRIMARY_ADDR to the routable address of the primary host}"
IMAGE="${IMAGE:-multinode-secondary:latest}"
NAME="${NAME:-secondary}"
SECONDARY_PORT="${SECONDARY_PORT:-8082}"
LITEFS_PORT="${LITEFS_PORT:-20202}"

docker rm -f "$NAME" 2>/dev/null || true
exec docker run -d --name "$NAME" --privileged \
  -p "${SECONDARY_PORT}:8080" \
  -e "PRIMARY_URL=http://${PRIMARY_ADDR}:8080" \
  -e "LITEFS_ADVERTISE_URL=http://${PRIMARY_ADDR}:${LITEFS_PORT}" \
  "$IMAGE"
