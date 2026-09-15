# Development log

This file is the chronological engineering trace for LlmProxy. For the current canonical snapshot and exact resume point, use `docs/project-status.md`.

## 2026-09-09 - Repository and gateway foundation

Implemented the .NET 10 solution, React/TypeScript admin application, PostgreSQL persistence, Docker packaging and GitHub Actions workflows. The gateway was structured around logical client-facing models, internal DGX nodes and model deployments so physical model identifiers remain hidden from clients.

Implemented the initial OpenAI-compatible surface with `/v1/models`, `/v1/chat/completions`, SSE streaming and later `/v1/responses`. Added inference bearer credentials, hashed secrets, Entra ID administration, node/model/deployment management, health probes, drain/disable states and administrative audit events.

## 2026-09-09 - Multi-DGX routing, health and observability

Added `WeightedLeastLoaded`, `RoundRobin` and `WeightedRoundRobin`, node/deployment weights, concurrency ceilings and pre-response failover. Streaming responses are never retried after bytes have been exposed to the caller.

Added path-safe service-root handling, health-monitor hysteresis, request metrics for status/duration/attempts/upstream-header latency/TTFT/token counts and streaming/failover visibility. Prompt and generated content are deliberately excluded from telemetry.

Added vLLM runtime pressure signals, per-deployment EWMA TTFT/duration/infrastructure-failure feedback and persisted smart-routing tuning.

## 2026-09-09 - Repository-first handover discipline

Introduced root `AGENTS.md`. Meaningful increments must update focused docs, project status, development log, roadmap where relevant and the agent resume point. CI cancels superseded runs for the same branch/PR.

## 2026-09-10 - DGX/DCGM hardware telemetry - VALIDATED

Added optional per-node NVIDIA/DCGM telemetry on a service root separate from vLLM. Collector parses GPU utilization, framebuffer memory, temperature and power into an in-memory multi-GPU snapshot. Transient DCGM failures do not change inference health.

Validated baseline:

```text
6c238a095273843e713a72fb2e26b2c7c434fc62
```

## 2026-09-10 - Architecture decision: no NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. Project owner chose to continue with the custom LlmProxy + vLLM architecture.

## 2026-09-10 - Benchmark harness - VALIDATED

Added `tests/performance/`, a .NET 10 benchmark harness targeting LlmProxy or direct vLLM, with path-prefixed service roots, Chat Completions/Responses, streaming/non-streaming and concurrency sweeps. Measurements include success/error rate, p50/p95/p99 TTFT/duration, requests/sec and token throughput where available.

Validated baseline:

```text
49e7932f14118be403eec042a1393946143776ae
```

## 2026-09-13 - Canonical handover snapshot

Added `docs/project-status.md` as the canonical current-state/resume document and linked it from `AGENTS.md` with explicit source-of-truth precedence.

## 2026-09-13 - Capacity Profile + node-wide admission/backpressure - VALIDATED

Implemented persisted benchmark-derived Capacity Profiles while keeping recommendations independent from live production limits. Added explicit audited Save / Apply / Clear workflows and React capacity administration.

Extended request-load tracking to atomically acquire both deployment-level and physical-node slots. Multiple deployments on one DGX can no longer overcommit physical capacity.

Defined saturation behavior:

```text
429 Too Many Requests
Retry-After: 1
error.code = capacity_exhausted
```

Validated baseline:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

## 2026-09-14 - Operator quickstart and private GHCR deployment path

Added `QUICKSTART.md`, `docs/quickstart.md`, `docker/docker-compose.quickstart.yml` and `docker/.env.quickstart.example`, covering Linux/Windows installation, private GHCR pull, PostgreSQL/env setup, real vLLM or mock runtime, smoke tests and optional Entra setup.

## 2026-09-14 - Caller Governance + Usage Groups - VALIDATED

Confirmed LlmProxy owns inference authentication, rate limiting/quotas, consolidated usage accounting and configurable Usage Groups/query UI. V1 accounting deliberately uses one primary group per API credential; shared GitHub Copilot provider credentials are not individual user identity.

Implemented persisted `UsageGroup`, request-time group snapshots, persisted credential/model rate policies, runtime fixed-window admission, calculated `Retry-After`, governance audit and usage reporting/UI.

Validated baseline:

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

## 2026-09-14 - In-memory inference credential authentication - VALIDATED

Removed API-credential PostgreSQL lookup from the `/v1` authentication path. Added copy-on-write credential cache, startup rebuild, post-save publication and buffered `LastUsedAtUtc` persistence.

Validated baseline:

```text
commit 1f607c8433fe2ca08a1c243b68d87587204f35ee
CI     34860662747
```

## 2026-09-14 - Runtime route/model/deployment catalog - VALIDATED

Removed the final synchronous route-catalog PostgreSQL lookup from ordinary `/v1` inference. Added `IRouteCatalog` / `IDeploymentCatalog`, copy-on-write node/model/deployment snapshots, startup rebuild, post-save publication and catalog diagnostics.

The PostgreSQL-outage smoke deliberately stops PostgreSQL after startup and proves `/v1/models` and authenticated Chat Completions still use already-published runtime state.

Validated baseline:

```text
commit 42c44753cd00d679a81bf065f410b7a497cdc000
CI     34871542047
```

## 2026-09-14 - Retention and operational hygiene - IMPLEMENTED

Added separate request-metric and audit retention policies, defaulting to 90 and 365 days respectively, with batched background cleanup and an explicit admin cleanup endpoint. The dedicated Docker smoke verifies independent cutoffs, retained recent rows and the cleanup audit event.

The retention smoke is green in the current canonical CI baseline `34961566507`.

## 2026-09-15 - Redis L2 runtime synchronization and full observability stack

Promoted Redis from a future architecture note to an implemented shared runtime layer while retaining local L1 request-path snapshots and PostgreSQL durable truth.

The full-stack deployment now runs:

```text
LlmProxy + PostgreSQL + Redis
OpenTelemetry Collector
Tempo + Loki + Prometheus + Grafana
```

Runtime state for routes, credentials and rate policies propagates between gateway replicas through Redis snapshot/version/event synchronization with periodic reconciliation. `GET /api/admin/runtime-sync` exposes synchronization diagnostics.

OpenTelemetry exports explicit application spans for authentication, governance, routing and capacity. `X-LlmProxy-Trace-Id` directly correlates a request with Tempo.

## 2026-09-15 - Shared Redis request-rate counters

Request-rate policies remain in local runtime state, while admission counters use Redis when distributed runtime mode is enabled. The full-stack smoke proves requests sent through different gateway replicas consume the same credential/model rate window.

This closes the former process-local rate-limit boundary for Redis-enabled deployments.

## 2026-09-15 - Distributed DGX capacity leases

Introduced provider-neutral `IRequestCapacityGate` / `IRequestCapacityLease` and Redis-backed capacity admission.

Key implementation milestone:

```text
cc454c325810b39a419107f49ad42a3ab7b70769
feat: coordinate physical capacity through redis leases
```

Redis atomically coordinates deployment + physical-node slots, while local load tracking remains available for routing telemetry. Distributed admission fails closed if Redis cannot safely coordinate capacity.

The full-stack smoke proves one gateway can hold the physical DGX slot and another gateway observes the same Redis lease and receives `429 capacity_exhausted`.

## 2026-09-15 - Active capacity lease-loss cancellation

Added a cancellation signal to `IRequestCapacityLease` and armed a safety watchdog around Redis lease renewal:

```text
a12afa877e6b44538f45bc60061a56c34a5895b2
feat: signal unsafe Redis capacity lease renewal

5d49f464829525c69621504321c95209002c9884
feat: cancel inference when Redis capacity coordination is lost

12535e439b6b17413a01942ebeb8504ad655a5ca
test: cancel long inference before Redis capacity lease expiry
```

The active inference token links client cancellation and lease coordination loss. If coordination becomes unsafe before response start, LlmProxy returns a retriable 503; if streaming has already started, it aborts the connection rather than letting generation continue beyond the lease safety boundary.

## 2026-09-15 - Full-stack fault-injection stabilization

The capacity fault test was made deterministic by waiting for the previous recovered request to finish actual Redis lease disposal before beginning the Redis outage scenario:

```text
3740692ffa3afa140e1a8f0ade5440e430838599
test: wait for capacity release before Redis fault
```

This checkpoint had both standard CI and Full Stack Smoke green:

```text
CI         34942635344 SUCCESS
Full Stack 34942635291 SUCCESS
```

## 2026-09-15 - Redis lease-loss hardening and observability

Hardened lease validity tracking with a dedicated monotonic `CapacityLeaseValidityTracker`, explicit `capacity_lease_lost` taxonomy, request-metric evidence and trace annotations:

```text
edb7008ca1e3548f80dde2f7242ed30f779b13a7
feat: harden Redis capacity lease loss handling
```

Added an explicit application child span that ends before a streaming connection abort so lease loss is exported deterministically through OpenTelemetry:

```text
9d00c74c6ce50bf25004ea443f10e46fe0c43d2f
fix: export capacity lease loss span before abort
```

The first Full Stack run after that span change exposed a different issue, not a Tempo issue: with a 20-second lease and 4-second renewal interval, the safety deadline was 16 seconds but the watchdog also polled on a coarse 4-second cadence. Timer-boundary jitter could evaluate just before 16 seconds and not run again until the 20-second Redis TTL.

The final fix preserved the logical safety deadline but increased watchdog sampling to at most 1 second (0.5 seconds for a 1-second renewal interval):

```text
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
fix: preserve Redis lease safety margin
```

No smoke assertion was weakened. TTL and renewal semantics were not relaxed.

Final validation:

```text
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

The Full Stack Smoke now proves together:

- Redis runtime-state synchronization;
- explicit application spans in Tempo;
- cross-gateway request-rate admission;
- distributed node/deployment capacity leases;
- normal lease release/recovery;
- Redis outage during a long SSE inference cancels before lease expiry;
- the stream never reaches `[DONE]` after coordination loss;
- PostgreSQL records `capacity_lease_lost`;
- Tempo exposes `capacity_lease_lost` for the same trace.

## 2026-09-15 - Next correctness increment: transactional outbox

The remaining distributed-runtime durability gap is between a committed PostgreSQL configuration mutation and durable Redis publication.

Current path:

```text
PostgreSQL commit
  -> EF SavedChanges interceptor
  -> local L1 update
  -> in-memory outbound runtime event
  -> Redis coordinator persist + pub/sub
```

A process crash after commit but before Redis publication can lose that outbound event until reconciliation/restart repairs state.

Next implementation requirement:

```text
same PostgreSQL transaction
  configuration mutation
  + RuntimeStateOutbox row
commit

outbox worker
  -> acknowledged Redis state write + pub/sub
  -> mark delivered only after Redis success
```

Do not treat enqueueing to the existing in-memory channel as delivery acknowledgement. Add retry/backoff, idempotency, observability and integration fault coverage.