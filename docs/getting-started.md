# Getting started

Run the sample host, send your first command, watch it get rejected the
second time, and look at the event log it actually wrote. Everything below
is real output from actually running this.

## Requirements

.NET 10 SDK. Clone the repo, then from its root:

```sh
dotnet build dotnetcqrs.slnx
```

## Run the sample host

[`samples/OrderFulfillmentHost`](../samples/OrderFulfillmentHost) is the
runnable counterpart to the hand-written `samples/OrderFulfillment` domain
(an `order` aggregate and a `task` aggregate) — the same deciders, hosted
for real over HTTP via `DotnetCqrs.Host`'s gateway, with JWT bearer auth
wired in. Each run starts fresh (it deletes its own `data/events.db` on
boot).

```sh
dotnet run --project samples/OrderFulfillmentHost --urls http://127.0.0.1:5289
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://127.0.0.1:5289
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

## Get a token

Every route the gateway maps requires authentication
(`app.MapCqrsGateway().RequireAuthorization()`). This sample signs its own
test tokens locally so it runs without a real identity provider — a
`POST /dev/token` endpoint, gated on `IsDevelopment()`, mints one:

```sh
curl -s -X POST "http://127.0.0.1:5289/dev/token?oid=dev-user"
```

That's a real JWT (issuer `https://dotnetcqrs-sample.local`, audience
`order-fulfillment-host`, `oid` claim set to whatever you pass). Save it:

```sh
TOKEN=$(curl -s -X POST "http://127.0.0.1:5289/dev/token?oid=dev-user")
```

**Never ship an endpoint like this.** It exists only because this sample
has no real identity provider behind it; a real deployment points
`AddJwtBearer` at Entra ID (or any OAuth2/OIDC provider) instead — see the
commented-out block at the top of `Program.cs` for the shape.

## Your first command

The gateway maps `POST /api/cqrs/{aggregate}/{aggregateId}/{command}`, body
is the command's JSON payload:

```sh
curl -s -X POST "http://127.0.0.1:5289/api/cqrs/order/order-1/PlaceOrder" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"title":"My first order"}'
```

```json
[{"position":1,"id":"e35cf6f2cc8e4fd8ab96a61f0ea2198c","aggregate":"order","aggregateId":"order-1","sequence":1,"type":"OrderPlaced","data":"{\"title\":\"My first order\"}","metadata":"{\"actor\":\"dev-user\",\"now\":\"2026-08-27T14:00:22.647Z\"}","created":"2026-08-27T14:00:22.691Z"}]
```

The response is the list of events the command produced — here, one
`OrderPlaced`. `metadata.actor` is `"dev-user"`, read straight from the
token's `oid` claim by `CqrsGatewayEndpoints.DefaultResolveActor`.

## The rejection

`order` is now an existing aggregate instance. Send `PlaceOrder` again for
the same id and `Orders.Decider()`'s own rule refuses it — this is what "a
command can fail, an event already has" (see
[Concepts](concepts.md#commands-and-events-are-not-the-same-thing)) looks
like on the wire:

```sh
curl -s -X POST "http://127.0.0.1:5289/api/cqrs/order/order-1/PlaceOrder" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"title":"My first order"}'
```

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400,"detail":"order already exists"}
```

400, not 500 — a decider rejection is a normal, expected outcome, not a
server fault. Confirming it instead moves the aggregate forward:

```sh
curl -s -X POST "http://127.0.0.1:5289/api/cqrs/order/order-1/ConfirmOrder" \
  -H "Authorization: Bearer $TOKEN"
```

```json
[{"position":2,"id":"3752ae65a3164877bbece8e9d22e8fe9","aggregate":"order","aggregateId":"order-1","sequence":2,"type":"OrderConfirmed","data":"{}","metadata":"{\"actor\":\"dev-user\",\"now\":\"2026-08-27T14:00:30.057Z\"}","created":"2026-08-27T14:00:30.057Z"}]
```

And without a token at all, every one of these routes is just a 401:

```sh
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://127.0.0.1:5289/api/cqrs/order/order-2/PlaceOrder" -d '{"title":"x"}'
# 401
```

## Look at what actually got written

The event log is an ordinary SQLite file,
`samples/OrderFulfillmentHost/bin/Debug/net10.0/data/events.db` — append-only,
nothing overwritten, nothing deleted (see
[Concepts](concepts.md#the-core-shift-facts-instead-of-current-state)):

```sh
sqlite3 samples/OrderFulfillmentHost/bin/Debug/net10.0/data/events.db \
  ".mode column" ".headers on" \
  "SELECT position, aggregate, aggregate_id, sequence, type, data FROM events ORDER BY position;"
```

```
position  aggregate  aggregate_id  sequence       type                  data
--------  ---------  ------------  --------  --------------  --------------------------
       1  order      order-1              1  OrderPlaced     {"title":"My first order"}
       2  order      order-1              2  OrderConfirmed  {}
```

That's the whole write side, end to end: a command hit the gateway, a
decider decided, an event got appended — and every command you ever sent
that got rejected left no row here at all.

## Where next

This host only wires up the write side (deciders + the gateway) — no
projection is running, so there's no read-model table to query yet. See
[the tutorial](tutorial.md) for the rest of the picture: taking a domain
from an EventModeling document, through code generation, to a running
decider *and* a queryable projection. [Concepts](concepts.md) covers the
why behind all of it if you're coming from CRUD.
