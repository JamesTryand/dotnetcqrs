# Shared by both nodes in ops/litefs/docker-compose.yml -- ARG PROJECT and
# ARG LITEFS_CONFIG pick which sample (MultiNode.Primary or MultiNode.Secondary) and
# which litefs.yml (litefs-primary.yml or litefs-secondary.yml) a given build produces.
# Build context is the repo root (see docker-compose.yml's `context: ../..`) so the
# publish step can see the whole solution tree, matching how the two sample projects
# reference ../../../src/*.
#
# LiteFS binary and package list straight from Fly.io's own "Getting Started with
# LiteFS in Docker" guide (fetched and checked while writing this, not guessed):
# flyio/litefs:0.5's own image, fuse3/ca-certificates/sqlite3 via apt. Runs as root
# (LiteFS's own recommendation for the mount process, not a shortcut taken here) --
# fine for this lab/smoke-test setup; harden before using this Dockerfile for anything
# real (non-root app process via `su`, narrower container capabilities -- see
# docs/cross-host-replication.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN dotnet publish "samples/MultiNode/${PROJECT}/${PROJECT}.csproj" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=flyio/litefs:0.5 /usr/local/bin/litefs /usr/local/bin/litefs
RUN apt-get update -y && apt-get install -y ca-certificates fuse3 sqlite3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ARG LITEFS_CONFIG
COPY ops/litefs/${LITEFS_CONFIG} /etc/litefs.yml

ENTRYPOINT ["litefs", "mount", "-config", "/etc/litefs.yml"]
