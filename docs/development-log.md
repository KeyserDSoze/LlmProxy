# Development log

This is the chronological engineering trace for LlmProxy. For the canonical current state and exact resume point use `docs/project-status.md`.

## 2026-09-09 — Repository, gateway and multi-DGX foundation

Created the .NET 10 layered solution, React/TypeScript admin, PostgreSQL persistence, Docker packaging and GitHub Actions foundations. Added logical client-facing models, internal DGX nodes/deployments, `/v1/models`, `/v1/chat/completions`, SSE streaming and `/v1/responses`.

Added bearer credentials, Entra administration plumbing, node/model/deployment management, health hysteresis, drain/disable, administrative audit, weighted least loaded / round robin / weighted round robin and pre-response-only failover. Added metadata-only request metrics, vLLM pressure signals and smart-routing tuning. Prompts/source/generated content remain excluded from telemetry.

## 2026-09-09 — Repository-first handover discipline

Introduced root `AGENTS.md` and the rule that meaningful increments update focused docs, canonical project status, development log and roadmap with actual validation evidence.

## 2026-09-10 — DGX/DCGM hardware telemetry — VALIDATED

Added optional per-node NVIDIA/DCGM telemetry on a service root independent from vLLM health. GPU utilization, framebuffer memory, temperature and power are observational and transient telemetry failure does not alter inference health.

Checkpoint: `6c238a095273843e713a72fb2e26b2c7c434fc62`.

## 2026-09-10 — Architecture decision: custom LlmProxy, not NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. The project owner chose to continue with custom LlmProxy + vLLM.

## 2026-09-10 — Benchmark harness — VALIDATED

Added the .NET benchmark harness under `tests/performance/` for direct-vLLM vs gateway measurements, streaming/non-streaming, Chat/Responses and concurrency sweeps. Measures success/error rate, TTFT/duration percentiles, requests/sec and token throughput where available.

Checkpoint: `49e7932f14118be403eec042a1393946143776ae`.

## 2026-09-13 — Capacity Profiles + physical-node admission — VALIDATED

Added persisted benchmark-derived Capacity Profiles with explicit audited apply workflow. Extended admission to atomically enforce deployment and aggregate physical-node concurrency.

Defined saturation as `429 capacity_exhausted` with `Retry-After: 1`.

Checkpoint: `600ad42cc53ad1e97a259819654ca5cf5480e1db`.

## 2026-09-14 — Operator onboarding / GHCR deployment

Added `QUICKSTART.md`, Linux/Windows instructions, private GHCR path and minimal/full Compose installation shapes.

## 2026-09-14 — Caller governance + Usage Groups — VALIDATED

Implemented persisted Usage Groups, request-time group snapshots, credential/model request-rate policies, calculated Retry-After, governance audit and usage reporting/UI.

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

## 2026-09-14 — Runtime credential authentication — VALIDATED

Removed API-credential SQL lookup from `/v1` inference. Added local copy-on-write credential cache, startup rebuild, live publication and buffered last-used persistence.

```text
commit 1f607c8433fe2ca08a1c243b68d87587204f35ee
CI     34860662747
```

## 2026-09-14 — Runtime route/model/deployment catalog — VALIDATED

Removed synchronous route-catalog SQL lookup from ordinary inference. Added versioned local runtime snapshots and PostgreSQL-outage smoke proving already-published auth/route state continues to serve `/v1/models` and authenticated inference after startup.

```text
commit 42c44753cd00d679a81bf065f410b7a497cdc000
CI     34871542047
```

## 2026-09-14 — Retention foundation

Added independent request-metric and audit retention, background batched cleanup and manual audited cleanup. Initial defaults: request metrics 90 days, audit 365 days.

## 2026-09-15 — Redis L2 synchronization + observability stack

Promoted Redis to an implemented shared runtime/coordination layer while preserving local request-path L1 and PostgreSQL durable authority. Added Redis snapshot/version/event synchronization and periodic reconciliation.

Bundled PostgreSQL + Redis + OpenTelemetry Collector + Tempo + Loki + Prometheus + Grafana. Added explicit application spans and `X-LlmProxy-Trace-Id` correlation.

## 2026-09-15 — Shared Redis request-rate counters

Request-rate policy remains local runtime state while fixed-window counters are Redis shared in distributed mode. Full-stack smoke proves requests through separate gateway replicas consume the same rate window.

## 2026-09-15 — Distributed DGX capacity leases

Added provider-neutral capacity-gate/lease abstractions and Redis atomic deployment + physical-node leases.

Key milestone:

```text
cc454c325810b39a419107f49ad42a3ab7b70769
feat: coordinate physical capacity through redis leases
```

Acquisition fails closed when Redis cannot be trusted.

## 2026-09-15 — Active lease-loss cancellation and hardening

Introduced active lease coordination-loss signaling and inference cancellation:

```text
a12afa877e6b44538f45bc60061a56c34a5895b2
5d49f464829525c69621504321c95209002c9884
12535e439b6b17413a01942ebeb8504ad655a5ca
```

Fault-injection stabilization checkpoint:

```text
3740692ffa3afa140e1a8f0ade5440e430838599
CI         34942635344 SUCCESS
Full Stack 34942635291 SUCCESS
```

Subsequent hardening added monotonic validity tracking and explicit `capacity_lease_lost` metric/trace evidence:

```text
edb7008ca1e3548f80dde2f7242ed30f779b13a7
9d00c74c6ce50bf25004ea443f10e46fe0c43d2f
```

A Full Stack failure exposed watchdog sampling jitter: the logical safety deadline was earlier than TTL, but a coarse timer could skip from immediately before that deadline to the lease expiry itself. The fix retained the deadline but samples at most every one second:

```text
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
fix: preserve Redis lease safety margin
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## 2026-09-15 — Transactional runtime-state outbox — IMPLEMENTED

Closed the DB-commit -> Redis-publication process-crash window.

Core implementation:

```text
9c6289ef172bed0502068df112b6e6aec4ee8521
feat: add transactional runtime-state outbox
```

Added `runtime_state_outbox` and same-transaction capture for runtime Node/Model/Deployment/Credential/RatePolicy mutations. Existing post-save interceptors now update only local L1; durable Redis publication belongs to the outbox worker.

Worker behavior:

- one global publisher via PostgreSQL session advisory lock;
- strict outbox-Id ordering;
- oldest failed/backoff row blocks later publication;
- exponential retry metadata;
- acknowledged Redis state write + version increment + pub/sub before `ProcessedAtUtc`;
- at-least-once/idempotent replay.

A critical HA case was explicitly handled: a non-originating gateway can win the advisory lock. The durable publisher therefore applies its acknowledged Redis event to its own L1 because same-origin pub/sub is intentionally ignored.

## 2026-09-15 — Transactional outbox fault validation — VALIDATED

Added deterministic full-stack outage/recovery smoke:

```text
4d9e241f8f8feee5afdaae6f7926cb6bdca70439
test: validate transactional outbox recovery across replicas
```

The smoke stops Redis, commits a rate policy, proves a pending/retried outbox row exists, stops the originating gateway, restarts Redis and requires the surviving peer to replay the event and enforce the new policy from its own L1.

Initial xUnit analyzer cleanup:

```text
e5b3bad2d46d7c61997f29f1b840b2f9ac601283
```

Validation:

```text
CI         34968324786 SUCCESS
Full Stack 34968114492 SUCCESS
```

## 2026-09-15 — Outbox diagnostics + retention hardening — VALIDATED

Added `/api/admin/runtime-sync` outbox diagnostics: pending count, failed pending count, oldest pending timestamp/age, max attempts, last processed timestamp and last error.

Added independent processed-outbox retention, default 30 days. The deletion predicate always requires `ProcessedAtUtc != null`; pending events are never deleted by age. The Docker retention smoke inserts an intentionally old pending row and proves it survives cleanup.

Implementation/fix:

```text
97e3b8f6b909156cbd58363d12f2fcbaf0627f5a
feat: expose outbox diagnostics and retention

cfc742db84223a7bed2f8a80cab3e5680615efad
fix: type outbox pending age as nullable
```

Validation:

```text
CI         34969410867 SUCCESS
Full Stack 34969410860 SUCCESS
```

## 2026-09-15 — Outbox deployment wiring + operational diagnostics — VALIDATED

Exposed operator configuration:

```text
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
```

Mapped processed-outbox retention into all PostgreSQL deployment shapes and outbox worker tuning into the Redis full stack. Strengthened the fault smoke so `/api/admin/runtime-sync` must report clean state before fault, failed pending backlog during Redis outage and drained healthy state after replay.

Canonical validated baseline:

```text
79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
chore: wire outbox operations into deployment
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

## Next increment — token / budget quotas

Request-rate admission is complete. Token/budget quotas require a distributed reservation/settlement model so concurrent requests cannot oversubscribe a shared budget while final token usage is still unknown.

The next implementation must define reservation amount, policy scope/period, Redis atomic reservation, reservation TTL/recovery, settlement to actual usage, overage behavior and cancellation/failure/streaming semantics before code is considered correct.
