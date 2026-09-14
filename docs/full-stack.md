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

Use the full stack when you want the complete runtime-cache and observability experience on one Linux VM or Docker host.

## What is installed

| Component | Responsibility | Durable data |
| --- | --- | --- |
| LlmProxy | auth, governance, routing, inference gateway | configuration/history delegated below |
| PostgreSQL | durable source of truth, usage, audit | `postgres-data` |
| Redis | distributed L2 runtime state and synchronization | AOF in `redis-data` |
| OpenTelemetry Collector | OTLP receive/process/export | none |
| Tempo | distributed traces | `tempo-data` local backend in bundled stack |
| Loki | structured application logs | `loki-data` |
| Prometheus | OpenTelemetry-exported metrics | `prometheus-data` |
| Grafana | common UI for metrics/logs/traces | `grafana-data` |

The bundled stack intentionally uses Tempo local filesystem storage to keep a single-host installation simple. For production/HA tracing, configure Tempo with supported object storage such as S3 or Azure Blob rather than treating the local volume as a production trace lake.

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

The script creates `docker/.env.full` and generates strong random values for:

- PostgreSQL password;
- Redis password;
- bootstrap inference API key;
- API-key HMAC pepper;
- Grafana administrator password.

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
Loki       <- otel-collector -> http://loki:3100/otlp
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

When OpenTelemetry is enabled LlmProxy returns a correlation header:

```http
X-LlmProxy-Trace-Id: <32-hex trace id>
X-LlmProxy-Request-Id: <gateway request id>
```

Open Grafana -> **Explore** -> **Tempo**, choose Trace ID search and paste `X-LlmProxy-Trace-Id`.

The initial instrumentation covers ASP.NET inbound requests, outbound `HttpClient` calls including vLLM, .NET runtime metrics and application logs. More fine-grained LlmProxy child spans for authentication/governance/routing/capacity can be added without changing the storage stack.

Telemetry must remain metadata-only. Never add prompt bodies, source code, generated output, bearer tokens or raw API secrets to span attributes or logs.

## Redis runtime model

Redis does not replace PostgreSQL and does not replace the local runtime cache:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshot + change propagation
RAM        = per-gateway L1 used by the inference request path
```

At startup LlmProxy rebuilds its local runtime state from PostgreSQL and publishes canonical snapshots to Redis. Persisted node/model/deployment, credential and rate-policy changes are applied to local RAM only after a successful database save and are then queued to Redis. Other gateway instances receive the event and update their L1 state. A Redis version plus periodic reconciliation heals missed pub/sub notifications.

`GET /api/admin/runtime-sync` reports provider, instance id, Redis connectivity, last applied version and event counters.

### Current multi-instance boundary

Redis currently synchronizes **configuration/runtime snapshots**. Two request-admission mechanisms are intentionally still process-local:

```text
request-rate counters
node/deployment capacity leases
```

Therefore do not yet run multiple active LlmProxy replicas and assume globally strict rate/capacity admission. The next HA increment should implement atomic distributed counters/semaphores in Redis. Configuration synchronization itself is already isolated behind runtime abstractions so that work does not require changing OpenAI endpoints.

### Durability boundary

PostgreSQL remains authoritative. Redis publication is asynchronous so a Redis outage never blocks a successful control-plane database commit or inference using already-published local state. Periodic reconciliation helps after Redis reconnects.

A future transactional-outbox increment can close the narrow crash window between PostgreSQL commit and enqueueing an outbound Redis event. Do not describe the current implementation as transactional outbox.

## Storage and production evolution

For a single VM the Docker named volumes are sufficient for evaluation and internal testing. For production consider separately:

- PostgreSQL backup/restore and HA policy;
- managed or redundant Redis if multiple gateways are required;
- Tempo object storage (for example S3/Azure Blob);
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
