# Full-stack installation: Redis + OpenTelemetry + Grafana

LlmProxy supports a minimal PostgreSQL quickstart and a distributed full stack:

```text
minimal
  LlmProxy + PostgreSQL

full stack
  LlmProxy + PostgreSQL + Redis
  + OpenTelemetry Collector
  + Tempo + Loki + Prometheus + Grafana
```

Use the full stack when you need multi-gateway runtime synchronization, shared request-rate/capacity coordination and bundled observability.

## Components

| Component | Responsibility | Durable data |
| --- | --- | --- |
| LlmProxy | auth, governance, routing, inference gateway | delegated below |
| PostgreSQL | durable configuration/history + runtime outbox | `postgres-data` |
| Redis | L2 runtime state, global counters and capacity leases | AOF in `redis-data` |
| OpenTelemetry Collector | OTLP receive/process/export | none |
| Tempo | traces | `tempo-data` local backend |
| Loki | logs | `loki-data` |
| Prometheus | metrics | `prometheus-data` |
| Grafana | metrics/logs/traces UI | `grafana-data` |

Tempo local filesystem storage is intentionally simple for a single-host bundle; production/HA tracing should use an appropriate durable backend.

## Start the stack

Linux:

```bash
bash docker/scripts/full-stack-init.sh
```

Windows / PowerShell:

```powershell
.\docker\scripts\full-stack-init.ps1
```

Review `docker/.env.full`, especially the external DGX inputs:

```env
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
PROVIDER_MODEL_NAME=<exact vLLM model id>
```

Start:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

If the GHCR image is private, authenticate Docker with a token that can read the package before starting.

## Internal wiring

Compose injects internal addresses automatically:

```text
PostgreSQL -> postgres:5432
Redis      -> redis:6379
OTLP       -> http://otel-collector:4317
Tempo      <- otel-collector
Loki       <- otel-collector
Prometheus -> scrape otel-collector:9464
Grafana    -> provisioned Prometheus / Tempo / Loki datasources
```

Default host URLs:

```text
LlmProxy Admin / API  http://<host>:8080/
Grafana               http://<host>:3000/
```

## Runtime state model

```text
PostgreSQL = durable source of truth + transactional outbox
Redis      = distributed L2 + change propagation + shared coordination
RAM        = per-gateway request-path L1
```

Normal inference does not synchronously query PostgreSQL for credential/route/rate-policy configuration after startup.

### Transactional runtime publication

Redis-enabled runtime mutations are durably captured in `runtime_state_outbox` in the same PostgreSQL transaction as the configuration change.

The outbox worker is globally serialized with a PostgreSQL advisory lock, processes rows in Id order, retries failed Redis delivery with backoff and marks `ProcessedAtUtc` only after Redis state persistence, runtime version increment and pub/sub publication succeed.

A replica that publishes an outbox event also applies the acknowledged change to its own L1. This matters because the worker can run on a replica different from the one that originally committed the configuration change.

The delivery model is at-least-once/idempotent rather than distributed two-phase commit.

## Runtime/outbox diagnostics

```http
GET /api/admin/runtime-sync
```

returns Redis status and event/version counters plus:

```text
outbox.pendingCount
outbox.failedPendingCount
outbox.oldestPendingAtUtc
outbox.oldestPendingAgeSeconds
outbox.maxPendingAttemptCount
outbox.lastProcessedAtUtc
outbox.lastError
```

Use sustained pending age and failed pending count as the primary operational warning signals for DB -> Redis publication.

## Outbox tuning

`docker/.env.full` exposes:

```env
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
```

These map to:

```text
Redis:OutboxBatchSize
Redis:OutboxPollMilliseconds
```

Defaults favor fast control-plane propagation without putting the outbox worker on the inference hot path. The code clamps unsafe values.

## Shared multi-instance admission

Redis-enabled deployments coordinate:

```text
request-rate counters
node/deployment physical capacity leases
```

Therefore requests arriving at different gateways consume the same policy window and physical DGX capacity boundary.

Capacity acquisition fails closed when Redis cannot safely coordinate. If Redis disappears while inference owns a lease, LlmProxy cancels before lease expiry. Before response start the caller gets `503`, `Retry-After: 1` and `capacity_lease_lost`; after SSE bytes start the connection is aborted and `[DONE]` is not emitted.

## Trace a request

When OTEL is enabled LlmProxy returns:

```http
X-LlmProxy-Trace-Id: <32-hex trace id>
X-LlmProxy-Request-Id: <gateway request id>
```

Use the trace id in Grafana -> Explore -> Tempo. Explicit spans include auth, governance, routing and capacity boundaries. `capacity_lease_lost` is visible in request metrics and trace evidence.

Telemetry is metadata-only: never add prompt/source/output bodies, bearer tokens or raw secrets.

## Data retention knobs

The deployment exposes:

```env
RETENTION_REQUEST_METRICS_DAYS=90
RETENTION_AUDIT_EVENTS_DAYS=365
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
RETENTION_INTERVAL_HOURS=24
RETENTION_BATCH_SIZE=5000
```

Runtime-outbox retention only removes already-processed rows. Pending rows are never deleted by age. See `docs/data-retention.md`.

## Validation evidence

Canonical validated code baseline:

```text
commit     79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

The dedicated Full Stack run proves together:

- PostgreSQL/Redis runtime synchronization;
- explicit OTLP application spans in Tempo;
- Grafana observability wiring;
- cross-gateway request-rate limiting;
- shared DGX capacity leases;
- lease-loss cancellation before Redis expiry;
- transactional outbox commit while Redis is unavailable;
- failed pending delivery + retry state;
- replay after Redis recovery by a non-originating gateway;
- Redis persistence and publishing-peer L1 application;
- outbox diagnostic transition from clean -> failed backlog -> drained.

## Production evolution

For production consider customer-specific choices for PostgreSQL backup/HA, Redis HA/redundancy, Tempo/Loki object storage, long-term Prometheus-compatible storage and TLS/SSO around operator surfaces.

## Operations

```bash
# status
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml ps

# logs
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml logs -f llmproxy redis otel-collector

# update
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml pull
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d

# stop, preserve volumes
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml down

# destructive reset
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml down -v
```
