#!/usr/bin/env bash
# dotnetcqrs-multi-node Milestone 7 — Postgres backend verification.
#
# Unlike ops/litefs/verify.sh (Milestone 3) this is not a bespoke smoke test: the
# Postgres backend has real in-suite tests (test/DotnetCqrs.Tests/Postgres/, gated on
# DOTNETCQRS_PG via [SkippableFact]). This script just stands up a disposable Postgres,
# points the suite at it, and tears it down — so a reviewer can reproduce the "not
# skipped" run in one command.
#
# Usage:   ops/postgres/verify.sh
# Engine:  auto-detects `docker` then `podman`; override with CONTAINER_ENGINE=podman
set -euo pipefail

cd "$(dirname "$0")/../.."

ENGINE="${CONTAINER_ENGINE:-}"
if [ -z "$ENGINE" ]; then
  if command -v docker >/dev/null 2>&1; then ENGINE=docker
  elif command -v podman >/dev/null 2>&1; then ENGINE=podman
  else echo "need docker or podman on PATH" >&2; exit 1
  fi
fi
echo "container engine: $ENGINE"

NAME="dotnetcqrs-m7pg"
PORT="${PGPORT:-55432}"

cleanup() { "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup

echo "starting postgres:17-alpine on :$PORT ..."
"$ENGINE" run -d --name "$NAME" -e POSTGRES_PASSWORD=dev -p "$PORT:5432" postgres:17-alpine >/dev/null

echo "waiting for it to accept connections ..."
for _ in $(seq 1 30); do
  if "$ENGINE" exec "$NAME" pg_isready -U postgres >/dev/null 2>&1; then break; fi
  sleep 1
done
"$ENGINE" exec "$NAME" pg_isready -U postgres

export DOTNETCQRS_PG="Host=localhost;Port=$PORT;Username=postgres;Password=dev;Database=postgres"
echo "DOTNETCQRS_PG=$DOTNETCQRS_PG"

echo "running the Postgres backend tests (should execute, not skip) ..."
dotnet test dotnetcqrs.slnx --filter "FullyQualifiedName~Postgres" -v q

echo
echo "PASS: Postgres backend suite ran against a real Postgres."
