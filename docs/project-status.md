# Project status / handover snapshot

Last reviewed: **2026-09-15**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

```text
commit     79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

The standard CI proves backend/unit/benchmark, frontend/Vitest/Playwright, production image build and all Docker/PostgreSQL smoke suites. The dedicated Full Stack run additionally proves Redis runtime sync, OTLP/Tempo, shared rate limits, distributed DGX capacity leases, proactive lease-loss cancellation and transactional outbox outage/recovery behavior.

## Core product scope

LlmProxy is the enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication
2. rate limiting / quotas
3. consolidated usage accounting
4. configurable Usage Groups + usage query/UI
5. logical-model routing across DGX/vLLM
6. distributed multi-instance coordination when Redis is enabled
7. metadata-only observability and operational controls
```

## Current request path

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC-hashed bearer credential resolved from local L1
  -> UsageGroup + request-rate policy from local L1
  -> Redis shared rate counter when distributed mode is enabled
  -> logical model -> deployment/node catalog from local L1
  -> smart routing
  -> capacity admission
       Redis distributed node/deployment lease when enabled
  -> vLLM
  -> metadata-only request metric + OTEL telemetry
```

Normal inference route/credential/policy decisions do not synchronously query PostgreSQL after startup.

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `/v1/models`, Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- HMAC-hashed API credentials; raw key shown once and never persisted.
- local runtime credential cache with startup rebuild/live updates.
- Entra admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin + administrative audit trail.

### Caller governance / usage

- persisted Usage Groups and credential attribution.
- request-time UsageGroup snapshot in metrics.
- persisted credential/model request-rate policies.
- calculated `Retry-After` + distinct `429 rate_limit_exceeded`.
- Redis-backed global request counters in distributed mode.
- usage aggregation/UI by group, credential and logical model.

### Routing / physical capacity

- logical models hide provider/DGX topology.
- weighted least loaded, round robin and weighted round robin.
- health hysteresis, drain/disable and path-prefixed service roots.
- pre-response failover only; never retry after downstream bytes start.
- vLLM pressure + EWMA feedback.
- persisted benchmark-derived Capacity Profiles.
- atomic deployment + physical-node admission.
- Redis capacity leases across gateway replicas.
- fail-closed acquisition if Redis coordination cannot be trusted.
- active inference cancellation before a lost Redis lease can expire.

Error taxonomy remains distinct:

```text
429 rate_limit_exceeded
429 capacity_exhausted
503 capacity_coordination_unavailable
503/abort capacity_lease_lost
503 no_healthy_deployment
```

### Distributed runtime state + transactional outbox

Current topology:

```text
PostgreSQL = durable source of truth + transactional outbox
Redis      = distributed L2 snapshots/version/events + shared coordination
local RAM  = per-gateway request-path L1
```

For Redis-enabled live runtime mutations, the database mutation and `runtime_state_outbox` row are inserted in the same EF/PostgreSQL transaction. Local L1 is updated only after DB success.

The outbox worker:

- elects one publisher at a time through a PostgreSQL advisory lock;
- processes pending rows strictly by monotonically increasing outbox Id;
- blocks later events behind the oldest failed/backing-off event;
- retries with bounded exponential backoff;
- treats Redis state persistence + global version increment + pub/sub publication as the acknowledgement boundary;
- updates the publishing gateway's own L1 after acknowledgement, which is required when a non-originating replica wins the outbox lock;
- marks `ProcessedAtUtc` only after Redis acknowledgement.

The delivery model is at-least-once. Redis upsert/delete operations are idempotent and global ordering prevents stale mutations overtaking newer ones.

Full Stack `34976465149` deliberately commits a rate-policy mutation while Redis is stopped, verifies a pending/retried outbox row, stops the originating gateway, restarts Redis and proves the surviving peer replays the mutation, writes Redis and enforces the policy from its own L1.

`GET /api/admin/runtime-sync` now exposes Redis status plus outbox backlog/retry diagnostics:

```text
pendingCount
failedPendingCount
oldestPendingAtUtc
oldestPendingAgeSeconds
maxPendingAttemptCount
lastProcessedAtUtc
lastError
```

The same smoke proves these diagnostics are clean before the fault, expose backlog/retry/error while Redis is unavailable, and return to a drained healthy state after replay.

### Retention / operational hygiene

Defaults:

```text
request_metrics              90 days
audit_events                 365 days
processed runtime outbox      30 days
cleanup interval              24 hours
batch size                    5000
```

Outbox retention only deletes rows with `ProcessedAtUtc != null`. Pending rows are never removed by retention, even if older than the configured cutoff. The Docker retention smoke explicitly validates this safety property.

### Observability / full stack

The bundled full stack runs PostgreSQL, Redis, OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana. Explicit application spans cover auth/governance/routing/capacity, and requests expose `X-LlmProxy-Trace-Id` for Tempo correlation. Telemetry remains metadata-only.

### Hardware / benchmarking

- optional DCGM telemetry isolated from inference health;
- GPU utilization/framebuffer/temperature/power diagnostics;
- .NET benchmark harness for direct-vLLM vs gateway tests;
- concurrency sweeps, TTFT/duration percentiles, requests/sec and token throughput.

## Operational knobs now exposed

Full-stack operators can configure:

```text
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
```

The first two map to `Redis:OutboxBatchSize` and `Redis:OutboxPollMilliseconds`; retention maps to `Retention:RuntimeStateOutboxDays`.

## Current development focus

### Increment 1 — token / budget quotas — ACTIVE NEXT

Request-rate limiting is complete. Token/budget quotas require explicit reservation and settlement semantics because actual token usage may only become known after inference and because concurrent requests across replicas must not oversubscribe the same budget.

The increment must define and test:

- persisted policy shape and scoping (credential/group/model and time period);
- distributed pre-admission reservation;
- settlement against actual prompt/completion/total usage;
- cancellation/failure/missing-usage behavior;
- reservation TTL/recovery;
- actual usage greater than reserved amount;
- streaming semantics;
- distinct error/metric taxonomy;
- Admin API/UI + audit and usage visibility where appropriate.

Do not implement only a post-response token counter; it is insufficient under concurrency.

### Increment 2 — remaining product hardening

- credential rotation workflow;
- backup/restore + restore verification;
- long-term usage rollups if reporting must outlive raw metric retention;
- production HA/storage choices for Redis/Tempo/Loki/Prometheus as required.

### Increment 3 — physical acceptance

Run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance when external environment access is available.

## Identity limitation to preserve

A centrally configured GitHub Copilot BYOK provider may use one shared API credential. LlmProxy can attribute that traffic to the credential/group but cannot infer the individual GitHub user from the request. Never infer users from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmarks;
- representative multi-DGX coding workload;
- real Entra application/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- Copilot usage-metrics/custom-model reporting if per-user analytics are needed.

## Important architecture decisions

- Continue custom LlmProxy + vLLM; NVIDIA PAIR was evaluated and rejected for the current direction.
- PostgreSQL remains durable authority.
- Redis-enabled replicas keep local request-path L1; do not replace this with Redis reads for every route/credential decision.
- Shared request counters and capacity leases are Redis coordinated when enabled.
- Distributed capacity coordination fails closed.
- DB -> Redis runtime publication is now transactionally protected by the PostgreSQL outbox.
- Pending outbox records are operationally sacred: retry them; never age-delete them.
- API credential runtime state stores HMAC hashes/safe metadata, never raw secrets.

## Exact resume point

A new session should:

1. read `AGENTS.md`, this file, `docs/usage-governance.md` and `docs/runtime-cache.md`;
2. inspect latest `main` and Actions;
3. treat `79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72` / CI `34976465066` / Full Stack `34976465149` as the canonical validated code baseline;
4. begin the token/budget quota increment from explicit reservation/settlement semantics;
5. preserve outbox ordering, Redis fail-closed capacity behavior and DB-free request-path configuration lookups;
6. update development log/roadmap/status after validation.
