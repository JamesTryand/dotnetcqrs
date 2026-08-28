# Two-host LiteFS replication (dotnetcqrs-multi-node Milestone 3)

`ops/litefs/docker-compose.yml` proves the cross-host mechanism with two containers on
one Docker host. This directory runs the same two images on **two separate machines** --
primary on host A, secondary on host B -- which is what surfaces the things the
single-host compose run hides: the LiteFS advertise URL has to be a real routable
address, the primary has to publish its LiteFS API port, and the secondary's app port
has to be free on a host that may already run other services.

Verified 2026-08-28 on two Ubuntu 24.04 nodes (Docker 28.3.3, LiteFS 0.5.14) -- see
`docs/cross-host-replication.md` for the full run notes.

## Images

Same two images `docker-compose.yml` builds:

```sh
# from the repo root, on a machine with fast internet:
docker build -f ops/litefs/Dockerfile \
  --build-arg PROJECT=MultiNode.Primary  --build-arg LITEFS_CONFIG=litefs-primary.yml \
  -t multinode-primary .
docker build -f ops/litefs/Dockerfile \
  --build-arg PROJECT=MultiNode.Secondary --build-arg LITEFS_CONFIG=litefs-secondary.yml \
  -t multinode-secondary .
```

If the target hosts can't pull the .NET base images fast enough to build there (the
verification lab's nodes had ~10 KB/s egress), build once elsewhere and ship the
result:

```sh
docker save multinode-primary   | ssh HOSTA 'docker load'
docker save multinode-secondary | ssh HOSTB 'docker load'
```

## Run

`run-primary.sh` / `run-secondary.sh` wrap the two `docker run` invocations. Both need
`--privileged` (or `cap_add: SYS_ADMIN` + `--device /dev/fuse`) for the FUSE mount.

On host A (the primary):

```sh
PRIMARY_ADDR=192.168.20.14 ./run-primary.sh
```

- Publishes `8080` (gateway) and **`20202` (LiteFS API)** -- the compose file doesn't
  publish 20202 because compose-network DNS makes it reachable without; across hosts it
  must be published so the replica can reach it.
- Sets `LITEFS_ADVERTISE_URL=http://$PRIMARY_ADDR:20202`, which both litefs YAMLs now
  read via `${...}` expansion.

On host B (the secondary):

```sh
PRIMARY_ADDR=192.168.20.14 SECONDARY_PORT=8082 ./run-secondary.sh
```

- `PRIMARY_ADDR` is host A's address; the script points both the LiteFS advertise URL
  and Milestone 2's write-forward target (`PRIMARY_URL`) at it.
- `SECONDARY_PORT` (default `8082`) is the host port for the secondary's gateway --
  override if something already holds it (a k8s `kube-router` on `:8080` is what forced
  it off `8080` during verification).

## Verify

From anywhere that can reach both hosts:

```sh
PRIMARY_URL=http://192.168.20.14:8080 \
SECONDARY_URL=http://192.168.20.15:8082 \
  ../verify.sh
```

Posts a command to the secondary, confirms it forwards to the primary and commits,
confirms a duplicate still forwards its `400`, then polls the secondary's own read
model until LiteFS's replicated `events.db` shows the write. End-to-end (forward hop +
replication + consumer poll) was ~0.6 s in the verification run; `verify.sh` polls at
1 s granularity so it reports 1-2 s.

## The "listening on localhost:20202" log line

LiteFS logs `http server listening on: http://localhost:20202`, but `localhost` there
is cosmetic -- `/proc/net/tcp6` in the primary container shows the socket bound to `::`
(all interfaces), and the same-host baseline reached it from another container with the
port unpublished. The only thing the cross-host case adds is publishing `20202` (done
by `run-primary.sh -p 20202:20202`) so the other host's packets reach the container.
No `http:` block is needed.
