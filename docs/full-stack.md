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

Use the full stack when you need multi-gateway runtime synchronization, shared request-rate/output-token/capacity coordination and bundled observability.

## Components

| Component | Responsibility | Durable data |
| --- | --- | --- |
| LlmProxy | auth, caller governance, routing, inference gateway | delegated below |
| PostgreSQL | durable configuration/history + runtime outbox | `postgres-data` |
| Redis | L2 runtime state, shared request/token counters and capacity leases | AOF in `redis-data` |
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

Review `docker/.env.full`, especially external DGX inputs:

```env
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
PROVIDER_MODEL_NAME=<exact vLLM model id>
```

Start:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

If the GHCR image is private, authenticate Docker with a token that can read the package first.

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
RAM        = per-gateway request-path configuration L1
```

Normal inference does not synchronously query PostgreSQL for credential/route/caller-policy configuration after startup.

### Transactional runtime publication

Redis-enabled runtime mutations are captured in `runtime_state_outbox` in the same PostgreSQL transaction as the configuration change.

The outbox worker is globally serialized with a PostgreSQL advisory lock, processes rows in Id order, retries failed Redis delivery with backoff and marks `ProcessedAtUtc` only after Redis state persistence, runtime version increment and pub/sub publication succeed.

A replica that publishes an outbox event also applies the acknowledged change to its own L1. This is required because the worker can run on a replica different from the one that originally committed the mutation.

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

Use sustained pending age and failed pending count as primary warning signals for DB -> Redis publication.

## Outbox tuning

`docker/.env.full` exposes:

```env
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
```

Defaults favor fast control-plane propagation without putting the worker on the inference hot path. Code safety clamps remain authoritative.

## Shared multi-instance governance and admission

Redis-enabled deployments currently coordinate three request-time boundaries:

```text
request-rate counters
output-token budget reservations/settlements
node/deployment physical-capacity leases
```

### Request rate

Gateways consume the same credential/model fixed request window.

### Output-token budget

Output-token policy definitions live in local L1 and propagate through the transactional runtime-state pipeline. Redis owns the shared fixed-window charged/reserved amount.

Before inference LlmProxy reserves the request's effective output-token cap. After a successful response with observed usage it refunds unused reservation. If upstream work may have occurred but usage is uncertain, the full reservation remains charged.

Distributed token-budget coordination is fail closed:

```text
Redis unavailable during token-budget reservation
  -> HTTP 503
  -> Retry-After: 1
  -> token_budget_coordination_unavailable
```

There is no per-replica local fallback in Redis mode because independent counters would violate the shared budget.

### Physical capacity

A request admitted on gateway A consumes shared Redis node/deployment capacity visible to gateway B. Capacity acquisition fails closed when Redis cannot coordinate safely. If Redis disappears while inference owns a lease, LlmProxy cancels before lease expiry. Before response start the caller gets `503`, `Retry-After: 1` and `capacity_lease_lost`; after SSE bytes start the connection is aborted.

## Output-token budget validation scenario

The dedicated full-stack token-budget smoke:

1. starts a second gateway before quota policy creation;
2. creates a credential/model policy with budget 17 and max 10 output tokens/request;
3. proves the pre-existing peer receives the policy into its local L1;
4. sends one request through gateway A and verifies Redis settles `used=7`;
5. sends one through gateway B and verifies shared `used=14`;
6. verifies the next reservation is globally rejected with `429 token_budget_exceeded`;
7. stops Redis and requires `503 token_budget_coordination_unavailable` rather than local degraded admission;
8. restarts Redis and proves the still-exhausted shared window remains enforced.

This is separate from the existing outbox outage smoke and the capacity lease-loss fault injection.

## Trace a request

When OTEL is enabled LlmProxy returns:

```http
X-LlmProxy-Trace-Id: <32-hex trace id>
X-LlmProxy-Request-Id: <gateway request id>
```

Use the trace id in Grafana -> Explore -> Tempo. Explicit spans cover auth, caller governance, routing and capacity. Telemetry is metadata-only: never add prompt/source/output bodies, bearer tokens or raw secrets.

## Data retention knobs

```env
RETENTION_REQUEST_METRICS_DAYS=90
RETENTION_AUDIT_EVENTS_DAYS=365
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
RETENTION_INTERVAL_HOURS=24
RETENTION_BATCH_SIZE=5000
```

Runtime-outbox retention only removes processed rows. Pending rows are never deleted by age.

## Validation evidence

Current distributed-runtime/quota validation:

```text
commit     887ebfac98389c0115eaf9c102a60133ede745ff
CI         34987407172 SUCCESS
Full Stack 34987407169 SUCCESS
```

The Full Stack run proves:

- PostgreSQL/Redis runtime synchronization;
- OTLP application spans and Grafana datasource wiring;
- cross-gateway request-rate limiting;
- shared DGX capacity leases + lease-loss cancellation;
- transactional outbox Redis-outage/recovery replay;
- non-originating publishing-peer L1 application;
- outbox clean -> failed backlog -> drained diagnostics;
- shared output-token reservation/settlement across gateways;
- fail-closed token-budget behavior during Redis outage and correct recovery.

React/Admin quota management is validated separately by CI on the complete product checkpoint documented in `docs/project-status.md`.

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
