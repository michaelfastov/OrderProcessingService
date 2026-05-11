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

The service is containerized; PostgreSQL and RabbitMQ are expected to be reachable on the network. The fastest local setup is to run them in containers next to the service.

### 1. Start Postgres + RabbitMQ

```bash
docker network create orders-net

docker run -d --name orders-postgres --network orders-net \
  -e POSTGRES_DB=orders -e POSTGRES_USER=orders -e POSTGRES_PASSWORD=orders \
  -p 5432:5432 postgres:16-alpine

docker run -d --name orders-rabbit --network orders-net \
  -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management
```

RabbitMQ management UI: <http://localhost:15672> (guest/guest).

### 2. Build and run the service

```bash
docker build -t order-processing-service .

docker run --rm --name orders-api --network orders-net -p 8080:8080 \
  -e ConnectionStrings__Postgres="Host=orders-postgres;Port=5432;Database=orders;Username=orders;Password=orders" \
  -e RabbitMq__Host=orders-rabbit \
  -e ASPNETCORE_ENVIRONMENT=Development \
  order-processing-service
```

Swagger: <http://localhost:8080/swagger>
Metrics: <http://localhost:8080/metrics>
Health:  <http://localhost:8080/health>

### Alternative — run locally with `dotnet run`

```bash
# Bring up Postgres + RabbitMQ as above, then:
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
        "totalAmount": 0,
        "items": [
          { "sku": "SKU-001", "quantity": 2 },
          { "sku": "SKU-003", "quantity": 1 }
        ]
      }'
```

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
3. `OrderProcessor` reloads the order, validates each line against current `Inventory`, enriches each item with the authoritative `UnitPrice`, decrements stock, computes a tier discount (5% over $200, 10% over $500), and flips the row to `Processed` (or `Failed` with a reason). The counter increments on success.

**Idempotency.** The worker short-circuits if the order is already `Processed`. Combined with manual acks this gives at-least-once delivery without double-processing in the common case. A proper solution would use a separate `Outbox` to also avoid the "saved but never published" failure mode — out of scope for a demo.

**Failure handling.** If processing throws, the order row is marked `Failed` with the exception message, then the message is `BasicNack`'d *without requeue* — failure is already persisted, so retry-looping the queue would just spin. A production version would route to a dead-letter queue with a retry policy.

**Observability.** Serilog for structured logging, `prometheus-net` for metrics scrapeable at `/metrics`. The specific counter the task requires (`orders_processed_total`) is the headline metric; I added `received` and `failed` siblings because they're nearly free and make a dashboard useful.

## Assumptions

- `CustomerId` is an opaque string — no separate Customer entity or auth.
- `TotalAmount` in the request is informational; the authoritative total is recomputed by the worker from current inventory prices. (The task says each order has a `TotalAmount`, so the field is accepted but not trusted.)
- Inventory is seeded once on first startup with four sample SKUs. There is no admin endpoint to mutate it — kept out for scope.
- Only happy-path retries: RabbitMQ's automatic recovery handles transient connection drops; a failed *message* is not re-queued (see above).
- "Database" connection failures at startup will crash the app — fine for a demo, in production you'd add a connection-retry policy (Polly + `EnableRetryOnFailure`).
- No authentication / authorization on the API.

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
