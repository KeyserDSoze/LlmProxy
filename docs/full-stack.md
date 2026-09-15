# Full-stack installation: Redis + OpenTelemetry + Grafana

LlmProxy has two supported local/on-prem installation shapes:

```text
minimal quickstart
  LlmProxy + PostgreSQL

full stack
  LlmProxy + PostgreSQL + Redis
  + OpenTelemetry Collector
  + Tempo + Loki + Prometheus + Grafana
```

Use the full stack when you want distributed runtime coordination plus the complete observability experience on one Linux VM or Docker host.

## What is installed

| Component | Responsibility | Durable data |
| --- | --- | --- |
| LlmProxy | auth, governance, routing, inference gateway | configuration/history delegated below |
| PostgreSQL | durable source of truth, usage, audit | `postgres-data` |
| Redis | distributed L2 runtime state, global counters and capacity leases | AOF in `redis-data` |
| OpenTelemetry Collector | OTLP receive/process/export | none |
| Tempo | distributed traces | `tempo-data` local backend in bundled stack |
| Loki | structured application logs | `loki-data` |
| Prometheus | OpenTelemetry-exported metrics | `prometheus-data` |
| Grafana | common UI for metrics/logs/traces | `grafana-data` |

The bundled stack intentionally uses Tempo local filesystem storage to keep a single-host installation simple. For production/HA tracing, configure supported object storage instead of treating the local volume as a production trace lake.

## One-time prerequisites

Install Docker Engine + Docker Compose v2 on Linux, or Docker Desktop + WSL 2 on Windows. If the LlmProxy GHCR package is private, authenticate Docker first:

```bash
echo "$GHCR_PAT" | docker login ghcr.io -u KeyserDSoze --password-stdin
```

The GitHub token/user must be allowed to read the private package.

## Linux: prepare everything

From the repository root:

```bash
bash docker/scripts/full-stack-init.sh
```

The script creates `docker/.env.full` and generates strong random values for PostgreSQL, Redis, the bootstrap inference API key, the API-key HMAC pepper and Grafana administrator password.

It does **not** invent the DGX address/model. Review these operator inputs in `docker/.env.full`:

```env
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
PROVIDER_MODEL_NAME=<exact vLLM model id>
```

Then start everything:

```bash
docker compose \
  --env-file docker/.env.full \
  -f docker/docker-compose.full.yml \
  up -d
```

Or prepare and start in one command after setting the DGX values in an existing `.env.full`:

```bash
bash docker/scripts/full-stack-init.sh --start
```

The init script is idempotent with respect to secrets: once placeholders have been replaced in `.env.full`, rerunning it does not rotate them.

## Windows / PowerShell

```powershell
.\docker\scripts\full-stack-init.ps1
```

Review `docker/.env.full`, then:

```powershell
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

Or:

```powershell
.\docker\scripts\full-stack-init.ps1 -Start
```

## What is auto-wired

Operators do not enter Docker-internal addresses. Compose injects them into LlmProxy:

```text
PostgreSQL -> postgres:5432
Redis      -> redis:6379
OTLP       -> http://otel-collector:4317
Tempo      <- otel-collector -> tempo:4317
Loki       <- otel-collector -> HTTP OTLP
Prometheus -> scrape otel-collector:9464
Grafana    -> Prometheus / Tempo / Loki provisioned automatically
```

This keeps environment files limited to secrets, external DGX endpoints and operator policy values.

## URLs

Default host ports:

```text
LlmProxy Admin / API  http://<host>:8080/
Grafana               http://<host>:3000/
```

Read the generated Grafana password from `docker/.env.full`; do not commit that file.

The storage services are not published to the LAN by default. Grafana reaches them over the Docker network.

## Trace a request end-to-end

When OpenTelemetry is enabled LlmProxy returns correlation headers:

```http
X-LlmProxy-Trace-Id: <32-hex trace id>
X-LlmProxy-Request-Id: <gateway request id>
```

Open Grafana -> **Explore** -> **Tempo**, choose Trace ID search and paste `X-LlmProxy-Trace-Id`.

Current explicit LlmProxy application spans include the main inference decision boundaries such as:

```text
llmproxy.auth
llmproxy.governance.rate_limit
llmproxy.routing.select
llmproxy.capacity.acquire
```

A distributed capacity lease loss is also marked explicitly with `capacity_lease_lost`, so operators can correlate the caller-visible failure/stream abort with request metrics and the Tempo trace.

Instrumentation also covers ASP.NET inbound requests, outbound `HttpClient` calls including vLLM, .NET runtime metrics and application logs.

Telemetry must remain metadata-only. Never add prompt bodies, source code, generated output, bearer tokens or raw API secrets to span attributes or logs.

## Redis runtime model

Redis does not replace PostgreSQL and does not replace the local runtime cache:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshot + change propagation + coordination
RAM        = per-gateway L1 used by the inference request path
```

At startup LlmProxy rebuilds local runtime state from PostgreSQL and publishes canonical snapshots to Redis. Persisted node/model/deployment, credential and rate-policy changes are applied to local RAM only after a successful database save and are then queued for Redis publication. Other gateway instances receive the event and update their L1 state. Redis version state plus periodic reconciliation heals missed pub/sub notifications.

`GET /api/admin/runtime-sync` reports provider, instance id, Redis connectivity, last applied version and event counters.

## Current multi-instance behavior

Redis-enabled deployments now coordinate the two admission mechanisms that were previously process-local:

```text
request-rate counters
node/deployment capacity leases
```

Therefore multiple active gateways share the same request-rate windows and physical DGX capacity boundary.

A request admitted on gateway A consumes Redis capacity visible to gateway B. When that lease ends, Redis releases the shared slot. Acquisition fails closed if Redis cannot safely coordinate capacity; LlmProxy does not silently fall back to a process-local guess.

### Active lease loss

If Redis disappears while a long-running inference already owns a distributed capacity lease, the gateway tracks the time since the last successful renewal and cancels inference before the Redis lease can expire and be reused by another replica.

Behavior is deliberately different depending on whether downstream bytes have started:

```text
response not started
  -> HTTP 503
  -> Retry-After: 1
  -> error.code = capacity_lease_lost

streaming response already started
  -> abort connection
  -> never emit [DONE] after safe coordination has been lost
```

The request metric records `capacity_lease_lost`, and the same condition is visible in the Tempo trace.

## Durability boundary and next increment

PostgreSQL remains authoritative. Runtime publication to Redis is asynchronous and retryable so a transient Redis outage does not roll back a successful control-plane database commit.

However, the current path still has a narrow process-crash window:

```text
PostgreSQL commit succeeds
  -> SavedChanges interceptor updates local L1
  -> event is enqueued in-memory
  -> Redis coordinator later persists/publishes it
```

A process crash after the database commit but before durable Redis publication can delay that committed change until reconciliation or restart repairs state.

The next correctness increment is a PostgreSQL transactional outbox. The configuration mutation and outbox row must commit in the same DB transaction; an outbox worker must mark the row delivered only after the required Redis state write and pub/sub publication have succeeded. Merely enqueueing onto the existing in-memory channel is **not** a durable acknowledgement.

## Validation evidence

The full-stack topology and distributed coordination are validated by:

```text
commit     6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

The dedicated Full Stack Smoke verifies PostgreSQL/Redis runtime synchronization, explicit OTLP application spans in Tempo, Grafana datasource wiring, shared rate limiting, shared DGX capacity, normal lease release/recovery and fail-closed cancellation during a Redis outage.

## Storage and production evolution

For a single VM the Docker named volumes are sufficient for evaluation and internal testing. For production consider separately:

- PostgreSQL backup/restore and HA policy;
- redundant/managed Redis for multi-gateway production;
- Tempo object storage;
- Loki production object-storage topology if long retention/HA is needed;
- external Prometheus-compatible long-term storage if required;
- TLS/SSO around Grafana and the gateway public endpoint.

The observability backend is replaceable because LlmProxy emits OTLP rather than depending directly on Grafana products.

## Operations

Status:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml ps
```

Logs:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml logs -f llmproxy otel-collector
```

Update all images:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml pull
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

Stop while preserving volumes:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml down
```

Destructive reset:

```bash
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml down -v
```