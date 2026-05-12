# Order Processing Service

A small .NET 8 microservice that accepts orders over HTTP and processes them asynchronously via a RabbitMQ-backed background worker. Orders and inventory are persisted in PostgreSQL.

## Architecture

```
                   ┌──────────────────────────────────────┐
                   │           OrderProcessingService     │
                   │                                      │
   HTTP POST       │   ┌──────────────┐    ┌───────────┐  │     ┌──────────┐
   /api/orders ───►│──►│ OrdersCtrl   │───►│ Publisher │──┼────►│ RabbitMQ │
                   │   │ (202 Accept) │    └───────────┘  │     │  queue   │
                   │   └──────┬───────┘                   │     └────┬─────┘
                   │          │ writes Pending order      │          │
                   │          ▼                           │          │ consumed
                   │   ┌────────────┐                     │          ▼
                   │   │ PostgreSQL │◄────────────────────┼── OrderConsumer
                   │   └────────────┘  reads/updates      │  (BackgroundService)
                   │                                      │     │
                   │   /metrics  (Prometheus)             │     ▼
                   │   /health                            │  OrderProcessor
                   └──────────────────────────────────────┘
```

## How to run

### Recommended — `docker compose up`

A `docker-compose.yml` is included at the repo root that brings up the API together with PostgreSQL and RabbitMQ.

```bash
docker compose up --build
```

The API waits for both dependencies to pass their healthchecks before starting. Once up:

- Swagger:   <http://localhost:8080/swagger>
- Metrics:   <http://localhost:8080/metrics>
- Health:    <http://localhost:8080/health>
- RabbitMQ:  <http://localhost:15672> (guest/guest)
- Postgres:  `localhost:5432` (orders / orders / orders)

To tear it all down (and wipe the Postgres volume):

```bash
docker compose down -v
```

### Alternative — run the API locally with `dotnet run`

Start just the infrastructure containers via compose:

```bash
docker compose up -d postgres rabbitmq
```

Then run the API from the repo:

```bash
cd OrderProcessingService
dotnet run
```

`appsettings.json` already points at `localhost` for both dependencies.

## API

### Submit an order — returns immediately

```bash
curl -X POST http://localhost:8080/api/orders \
  -H "Content-Type: application/json" \
  -d '{
        "customerId": "cust-123",
        "totalAmount": 378.98,
        "items": [
          { "sku": "SKU-001", "quantity": 2, "unitPrice": 24.99 },
          { "sku": "SKU-003", "quantity": 1, "unitPrice": 329.00 }
        ]
      }'
```

The worker verifies that `totalAmount == Σ (unitPrice × quantity)` (cents-level tolerance) and rejects mismatched orders by marking them `Failed`.

Response: `202 Accepted` with the order body and a `Location` header pointing at the GET endpoint. The HTTP call does not block on processing.

### Poll order status

```bash
curl http://localhost:8080/api/orders/<id>
```

Status transitions: `Pending → Processing → Processed` (or `Failed` with `failureReason`).

### List inventory (handy for picking valid SKUs)

```bash
curl http://localhost:8080/api/inventory
```

Inventory is seeded on first startup with four products (`SKU-001` … `SKU-004`).

## Observability

- **Structured logs** via Serilog → console (JSON-ish key/value). Every order acceptance and every processing outcome is logged with the `OrderId`.
- **Metrics** at `GET /metrics` (Prometheus exposition format):
  - `orders_received_total` — orders accepted by the API.
  - `orders_processed_total` — orders successfully processed by the worker. *(This is the metric the task asks for.)*
  - `orders_failed_total` — orders that hit a validation/processing error.
  - Plus default HTTP/runtime metrics from `prometheus-net.AspNetCore`.

## Design decisions and trade-offs

**Queue choice — RabbitMQ.** The task asks specifically for a queueing mechanism. RabbitMQ gives durable queues, per-message acks, and a familiar mental model for a producer/consumer split — natural fit for "submit now, process later". Redis would be lighter but turning Redis into a reliable queue means either Streams (with consumer groups) or rolling your own retry semantics on top of lists — more code for less clarity. An in-process `Channel<T>` would be the simplest possible answer but loses the durability and the visible decoupling the task is testing for.

**Persistence — EF Core + PostgreSQL.** Idiomatic for .NET, code-first model is easy to read, and Npgsql is mature. The model is intentionally small (`Order`, `OrderItem`, `Inventory`) — two top-level entities as the task spec requires, plus a child collection so items aren't crammed into a JSON column. For this demo I call `EnsureCreated` at startup rather than generating EF migrations; in a real project you'd use `dotnet ef migrations add` and `MigrateAsync` instead.

**Async pipeline.**
1. `POST /api/orders` validates the payload, writes the order as `Pending` inside a single SaveChanges, publishes a `ProcessOrderMessage(OrderId)` to RabbitMQ, and returns `202 Accepted` with a `Location` header. The HTTP request never waits on the worker.
2. `OrderConsumer` (a `BackgroundService`) subscribes to the queue with manual acks and `prefetchCount=5`. Each message is handled in a freshly-scoped DI container so the `DbContext` lifecycle stays correct.
3. `OrderProcessor` reloads the order, flips it to `Processing` (committed eagerly so the state is visible to observers even on failure), then opens a DB transaction to do the real work: validate each line against current `Inventory` (SKU exists, stock available), verify `TotalAmount == Σ (UnitPrice × Quantity)` with a cents-level tolerance, decrement stock via atomic `ExecuteUpdateAsync` calls with a `StockQuantity >= Quantity` guard in the WHERE clause (makes overselling impossible under concurrent workers, no row-level locks required), compute a tier discount (5% over $200, 10% over $500), and flip the row to `Processed`. On success the transaction commits; on any failure it rolls back, and a separate write marks the order as `Failed` with the exception message. The counter increments on success.

**Idempotency.** The worker short-circuits if the order is already `Processed`. Combined with manual acks this gives at-least-once delivery without double-processing in the common case. A proper solution would use a separate `Outbox` to also avoid the "saved but never published" failure mode — out of scope for a demo.

**Failure handling.** If processing throws, the order row is marked `Failed` with the exception message, then the message is `BasicNack`'d *without requeue* — failure is already persisted, so retry-looping the queue would just spin. A production version would route to a dead-letter queue with a retry policy.

**Observability.** Serilog for structured logging, `prometheus-net` for metrics scrapeable at `/metrics`. The specific counter the task requires (`orders_processed_total`) is the headline metric; I added `received` and `failed` siblings because they're nearly free and make a dashboard useful.

## Assumptions

- `CustomerId` is an opaque string — no separate Customer entity or auth.
- The client supplies both `UnitPrice` per item and the order `TotalAmount`. The worker accepts these but verifies internal consistency (`Σ unitPrice × quantity == totalAmount`). `Inventory` is the source of truth for **stock only** — pricing lives entirely on the order payload.
- Inventory is seeded once on first startup with four sample SKUs. There is no admin endpoint to mutate it — kept out for scope.
- Only happy-path retries: RabbitMQ's automatic recovery handles transient connection drops; a failed *message* is not re-queued (see above).
- "Database" connection failures at startup will crash the app — fine for a demo, in production you'd add a connection-retry policy (Polly + `EnableRetryOnFailure`).
- No authentication / authorization on the API.

## Known limitations (intentional scope cuts)

- **No stuck-order recovery.** `RecordFailureAsync` is wrapped in its own try-catch so a failed failure-write is logged rather than escalated, but if the DB is unreachable at that exact moment the order is left in `Processing` indefinitely. A real system would have a periodic reaper job that scans for orders stuck in `Processing` past a timeout and re-enqueues them.
- **No dead-letter queue.** When the consumer fails to handle a message (deserialization error, DB unavailable during the initial claim), it calls `BasicNack(requeue: false)` and the message is dropped. A real system would declare the queue with an `x-dead-letter-exchange` argument and route poisoned/failed messages to a DLQ for inspection and replay.
- **`/health` is a stub** — it returns `{ status: "ok" }` unconditionally and does not actually ping Postgres or RabbitMQ. For real operation you'd split into `/health/live` (process up) and `/health/ready` (dependencies pingable) using `AddHealthChecks().AddNpgSql(...).AddRabbitMQ(...)`.

## Future improvements

If this needed to harden toward production, the next pieces I'd add:

- **Transactional outbox for publish.** Today `OrdersController.Submit` does a dual write — it commits the order row to Postgres and *then* publishes to RabbitMQ. If the publish throws after the commit (broker down, network blip), the order is persisted as `Pending` but no message ever lands on the queue, and it sits there forever. The fix is the outbox pattern: insert an `OutboxMessage` row inside the same `SaveChanges` as the order, then a separate background pump reads outbox rows and publishes them to RabbitMQ with retries. That converts the two-system commit into one atomic DB write plus an idempotent publish, giving at-least-once delivery without the orphan-order failure mode. Left out for the demo because it's a meaningful chunk of code (outbox table, polling pump, dedupe on the consumer) and the dual-write window is small enough to be a known-but-accepted risk at this scale.
- **Split API and worker into separate services.** The queue already decouples them — the API only needs to write to the DB and publish, the worker only needs to consume and process. Today they're packaged in one process for the demo's sake, but moving the `OrderConsumer` + `OrderProcessor` into a second deployment unit (sharing only the EF model and message contract via a small `OrderProcessing.Contracts` library) would let you scale them independently (HTTP traffic is spiky, background work is steady), restart them independently (a worker crash doesn't take the API down), and give each its own resource budget. Minimal code change — mostly a project-split refactor.
- **Idempotency keys on `POST /api/orders`.** Accept an `Idempotency-Key` header and store the (key → order ID) mapping so a client retry on a network blip returns the original order instead of creating a duplicate.
- **Processing-latency histogram.** Add `orders_processing_seconds` around `ProcessAsync` so the metric story includes p50/p95/p99 timings, not just counters.
- **EF migrations instead of `EnsureCreated`.** Real schema evolution requires `dotnet ef migrations add` + `MigrateAsync` on startup.

## Project layout

```
OrderProcessingService/
├── Api/                 # Request / response DTOs
├── Controllers/         # OrdersController, InventoryController
├── Data/                # AppDbContext, DbInitializer (seed)
├── Domain/              # Order, OrderItem, Inventory, OrderStatus
├── Messaging/           # RabbitMqConnection, OrderPublisher, message contracts
├── Observability/       # Prometheus metric definitions
├── Processing/          # OrderConsumer (BackgroundService), OrderProcessor
├── Program.cs           # DI wiring, middleware, /metrics, /health
└── appsettings*.json    # Connection strings, RabbitMQ host
```
