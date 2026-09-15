# Project status / handover snapshot

Last reviewed: **2026-09-15**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated runtime baseline

Latest fully validated product baseline:

```text
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
```

Validation evidence:

```text
GitHub Actions CI 34961566507 — SUCCESS
- Backend unit tests: success
- Benchmark harness tests: success
- React build / Vitest / Playwright: success
- Production Docker build: success
- PostgreSQL backend smoke: success
- DGX hardware smoke: success
- Capacity/backpressure smoke: success
- Usage governance/rate-limit smoke: success
- Route-catalog PostgreSQL-outage smoke: success
- Data-retention smoke: success

Full Stack Smoke 34961566463 — SUCCESS
- PostgreSQL + Redis full stack: success
- Redis runtime-state synchronization: success
- OTLP -> Tempo traces with explicit LlmProxy application spans: success
- Grafana/Tempo/Loki/Prometheus wiring: success
- cross-gateway rate-limit counters: success
- distributed DGX capacity leases: success
- active inference cancelled before Redis lease expiry when coordination is lost: success
- `capacity_lease_lost` request metric and Tempo trace evidence: success
```

Earlier useful checkpoints:

```text
Route catalog DB-free baseline 42c44753cd00d679a81bf065f410b7a497cdc000 / CI 34871542047
Caller Governance             798f0a460dcc4f89b17e2ce89df66f511d324241 / CI 34859931084
Credential auth cache         1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
Redis lease pre-hardening      3740692ffa3afa140e1a8f0ade5440e430838599 / CI 34942635344 / Full Stack 34942635291
```

## Core product scope

LlmProxy is the enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication
2. rate limiting / quotas
3. consolidated usage accounting
4. configurable usage groups + usage query/UI by group
5. logical-model routing across DGX/vLLM
6. distributed multi-instance coordination when Redis is enabled
7. metadata-only observability and operational controls
```

## Current request flow

```text
GitHub Copilot / OpenAI-compatible client
    -> bearer credential HMAC hash
    -> local L1 credential / UsageGroup resolution
    -> credential + logical-model rate policy
       -> Redis shared counter when Redis is enabled
       -> 429 rate_limit_exceeded when caller policy is exceeded
    -> local L1 logical-model -> deployment/node/provider-model catalog
    -> smart routing
    -> capacity admission
       -> Redis distributed node/deployment lease when Redis is enabled
       -> 429 capacity_exhausted when healthy infrastructure is full
       -> 503 capacity_coordination_unavailable if distributed admission cannot be trusted
    -> DGX / vLLM
    -> metadata-only request metric
    -> OpenTelemetry traces/metrics/logs
```

If an already-admitted Redis capacity lease becomes unsafe during inference, the request is cancelled before lease expiry. Before response start the caller receives `503` + `Retry-After: 1` + `capacity_lease_lost`; after streaming bytes have started the connection is aborted. Metrics/traces record `capacity_lease_lost`.

`503 no_healthy_deployment` remains distinct from the governance/capacity/coordination conditions above.

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `GET /v1/models`, Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- HMAC-hashed API credentials; raw key shown once and never persisted.
- local runtime credential cache with startup rebuild and live updates.
- credential expiry/revocation/group decisions from runtime state.
- buffered background `LastUsedAtUtc` persistence.
- Entra ID admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin and administrative audit trail.

### Caller governance / usage

- persisted `UsageGroup` and optional primary group per API credential.
- stable `UsageGroupId` snapshot in request metrics.
- persisted rate-limit policies by credential with optional logical-model override.
- Redis-backed shared fixed-window counters when Redis is enabled; in-memory provider otherwise.
- calculated `Retry-After` and distinct `429 rate_limit_exceeded`.
- usage APIs and React governance/usage administration.

### Runtime route catalog / distributed state

Normal inference route resolution does not query PostgreSQL after startup/runtime publication.

Runtime state contains routing-relevant nodes/models/deployments plus credential and rate-policy snapshots. The preferred/current Redis-enabled topology is:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshots/version/events
local RAM  = request-path L1 per gateway replica
```

Behavior includes:

- startup rebuild from PostgreSQL;
- local L1 copy-on-write/versioned snapshots;
- Redis publication/subscription and periodic reconciliation;
- cross-replica change propagation;
- runtime synchronization diagnostics at `GET /api/admin/runtime-sync`;
- PostgreSQL-outage smoke proving already-published auth/route state keeps inference operational after startup.

### Routing / physical capacity

- path-prefixed DGX service roots.
- health hysteresis and node diagnostics.
- weighted least loaded / round robin / weighted round robin.
- pre-response failover only.
- persisted routing strategy and smart-routing tuning.
- vLLM queue/running/KV-cache signals and EWMA performance feedback.
- persisted benchmark-derived Capacity Profiles.
- atomic deployment + node-wide physical capacity admission.
- Redis shared capacity leases across gateway replicas when enabled.
- lease renewal, expiry recovery and fail-closed coordination handling.
- proactive cancellation of active inference if safe renewal can no longer be guaranteed.

### Retention / operational hygiene

Retention is implemented and validated, not pending:

```text
request_metrics default retention = 90 days
audit_events default retention    = 365 days
background cleanup                = 24 hours
batched deletion                  = 5000 rows by default
```

Admin endpoints expose current retention and manual cleanup. See `docs/data-retention.md`.

### Observability / full stack

The full-stack deployment includes PostgreSQL, Redis, OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana.

Validated traces include explicit LlmProxy application spans for authentication, governance, routing and capacity. Requests expose `X-LlmProxy-Trace-Id` for direct Tempo correlation. Lease-loss handling records `capacity_lease_lost` in both request metrics and trace data.

Telemetry remains metadata-only; prompts/source/output/secrets are excluded.

### Hardware / benchmarking

- optional DCGM telemetry isolated from inference health;
- GPU utilization/framebuffer/temperature/power diagnostics;
- .NET benchmark harness for direct-vLLM vs gateway measurements;
- concurrency sweeps, p50/p95/p99 TTFT/duration, req/s and token throughput.

## Current distributed consistency boundary

The important remaining correctness gap is **DB -> Redis publication durability**.

Current configuration mutation path is effectively:

```text
PostgreSQL SaveChanges succeeds
    -> EF SavedChanges interceptor
    -> update local L1
    -> enqueue runtime event for Redis coordinator
    -> coordinator persists/publishes to Redis asynchronously
```

This is operationally resilient and periodically reconciled, but it still has a crash window between the durable PostgreSQL commit and durable Redis publication. A process crash in that window can delay a committed mutation until reconciliation/restart.

The next increment is a PostgreSQL transactional outbox:

```text
same DB transaction
  durable config mutation
  + RuntimeStateOutbox row
commit

outbox worker
  -> acknowledged Redis state/snapshot write + pub/sub event
  -> mark row delivered only after Redis success
```

The existing fire-and-forget `IRuntimeStateEventSink`/in-memory outbound channel must **not** be treated as the outbox acknowledgement boundary.

## Current development focus

### Increment 1 — transactional outbox — ACTIVE NEXT

- outbox entity/table/EF migration;
- outbox row inserted in the same transaction as runtime configuration mutations;
- durable Redis dispatcher returning only after the Redis write + publication succeeds;
- worker with retry/backoff/idempotency;
- delivery/lag/failure observability;
- integration coverage for Redis outage/recovery and process-safe replay semantics.

### Increment 2 — token / budget quotas

Request-rate limiting is already validated. Token/budget quotas still need explicit reservation/settlement/overage semantics for streaming, cancellation and failures because final token usage is known only after inference.

### Increment 3 — remaining production hardening

- credential rotation workflow;
- backup/restore verification;
- long-term usage rollups if reporting must outlive raw metric retention;
- production storage/HA choices for Redis/Tempo/Loki as required.

### Increment 4 — physical acceptance

Run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance when the external environment is available.

## Identity limitation to preserve

A centrally configured GitHub Copilot BYOK provider may use one shared API credential. LlmProxy can reliably attribute that traffic to the credential/group but cannot infer the individual GitHub user from the request. Never infer users from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark runs;
- representative multi-DGX coding workload;
- real Microsoft Entra application/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- GitHub Copilot usage-metrics/custom-model reporting if per-user analytics are required.

## Important architecture decisions

- Continue custom LlmProxy + vLLM; NVIDIA PAIR was evaluated and rejected for the current direction.
- PostgreSQL remains durable authority.
- Redis-enabled replicas keep request-path L1 state; do not replace it with Redis reads on every inference request.
- Shared rate-limit counters and capacity leases are Redis coordinated when enabled.
- Distributed capacity coordination fails closed.
- Runtime configuration publication is not yet transactional across PostgreSQL -> Redis; the outbox is the next correctness increment.
- API credential runtime state stores HMAC hash and safe metadata, never raw secrets.
- Hardware telemetry stays observational until benchmarks justify routing use.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file and `docs/runtime-cache.md`;
2. inspect latest `main` and GitHub Actions state;
3. treat `6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f` / CI `34961566507` / Full Stack `34961566463` as the validated baseline;
4. start the transactional-outbox increment without weakening the existing Redis fail-closed semantics;
5. ensure outbox delivery is acknowledged only after durable Redis work, not after in-memory enqueue;
6. update development log/roadmap/status after validation.

Every meaningful increment must keep this snapshot current and must not be marked validated without actual CI/integration evidence.