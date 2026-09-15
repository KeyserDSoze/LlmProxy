# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not yet complete; `EXTERNAL` requires tenant/hardware/environment validation.

For canonical current state use `docs/project-status.md`.

## M0 - Repository bootstrap — DONE

- .NET 10 layered solution, React/TypeScript admin, PostgreSQL/EF migrations, Docker/GitHub Actions/GHCR foundations.
- repository-first handover discipline through `AGENTS.md` and `docs/`.

## M1 - Copilot -> gateway -> one DGX — ACTIVE / EXTERNAL VALIDATION REMAINS

- DONE: logical models, `/v1/models`, Chat Completions + SSE, Responses, arbitrary compatible payload preservation.
- DONE: bearer/API-key auth from runtime L1.
- DONE: route/model/deployment catalog from runtime L1.
- EXTERNAL: real GitHub Copilot BYOK through target public endpoint.

## M2 - Multi-DGX — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration, health hysteresis, drain/disable.
- DONE: weighted least loaded / round robin / weighted round robin.
- DONE: pre-response failover only.
- DONE: deployment + aggregate physical-node admission.
- DONE: `429 capacity_exhausted`.
- DONE: Redis-coordinated capacity leases across replicas.
- DONE: fail-closed active-inference cancellation before unsafe lease expiry.

## M3 - Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra plumbing and Admin/Reader roles.
- DONE: React control plane.
- DONE: HMAC-hashed DB-backed API credentials + runtime cache.
- DONE: asynchronous credential last-used persistence + audit trail.
- EXTERNAL: real Entra app registration/roles.

## M4 - Observability — DONE FOR CURRENT MVP / STORAGE EVOLUTION REMAINS

- DONE: request/status/duration, TTFT, token, attempt/failover metrics.
- DONE: vLLM runtime signals + optional DCGM telemetry.
- DONE: OTEL instrumentation and explicit auth/governance/routing/capacity spans.
- DONE: Collector + Tempo + Loki + Prometheus + Grafana bundle.
- DONE: `X-LlmProxy-Trace-Id` correlation.
- DONE: `capacity_lease_lost` metric/trace evidence.
- PLANNED: production HA/object-storage choices where required.

## M5 - Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/running/KV-cache signals and EWMA feedback.
- DONE: persisted routing tuning and benchmark-derived Capacity Profiles.
- DONE: benchmark harness and audited capacity apply workflow.
- DONE: Redis lease renewal/expiry recovery and proactive safety watchdog.
- EXTERNAL: real DGX benchmark profiles + representative Copilot load.
- PLANNED: tune routing thresholds from measured hardware evidence.

## M6 - Operator onboarding / deployability — DONE FOR REPOSITORY PATH / EXTERNAL DEPLOYMENT REMAINS

- DONE: private GHCR path, Linux/Windows quickstart, minimal/full Compose, initialization scripts.
- DONE: CI validation of Compose and production image.
- DONE: full-stack operator knobs for outbox batch/poll and processed-outbox retention.
- EXTERNAL: production Cloudflare Tunnel and self-hosted deployment runner.

## M7 - Caller governance — ACTIVE FOR TOKEN BUDGETS

- DONE: request authentication.
- DONE: persisted credential/model request-rate policies.
- DONE: fixed-window limiter + calculated `Retry-After` + `429 rate_limit_exceeded`.
- DONE: Admin API/UI + audit.
- DONE: Redis shared request counters across replicas.
- ACTIVE: token/budget quotas with explicit reservation/settlement semantics.

The quota increment must remain distinct from request-rate and physical-capacity admission.

## M8 - Usage groups and reporting — DONE FOR CURRENT MVP

- DONE: UsageGroup administration and primary group per credential.
- DONE: request-time UsageGroup snapshot for stable historical accounting.
- DONE: usage aggregation/UI by group, credential and logical model.
- PLANNED: archive/deletion semantics if later required.
- EXTERNAL/PLANNED: Copilot usage-metrics ingestion for per-user/adoption analytics.

Important limitation: a shared Copilot provider key is not individual identity. Never infer users from IP.

## M9 - Inference hot-path hardening — DONE FOR CURRENT RUNTIME

- DONE: credential, route and rate-policy lookup from local L1.
- DONE: DB-free ordinary route/auth decisions after startup.
- DONE: PostgreSQL-outage smoke for already-published inference state.
- DONE: Redis L2 synchronization while preserving L1.

## M10 - Retention and operational hygiene — DONE FOR CURRENT MVP

- DONE: request metrics default 90 days.
- DONE: audit default 365 days.
- DONE: processed runtime-state outbox default 30 days.
- DONE: pending outbox rows are never retention-deleted.
- DONE: background batched cleanup + manual audited cleanup.
- DONE: Docker smoke validates independent cutoffs and pending-outbox preservation.
- PLANNED: long-term usage rollups if reporting must exceed raw request retention.

## M11 - Distributed runtime state / HA — DONE FOR CURRENT MVP

Validated topology:

```text
PostgreSQL = durable source of truth + runtime-state outbox
Redis      = distributed L2 snapshots/events + shared coordination
local RAM  = per-replica request-path L1
```

- DONE: Redis snapshot/version/event synchronization and periodic reconciliation.
- DONE: peer L1 updates + runtime-sync diagnostics.
- DONE: Redis global request-rate counters.
- DONE: Redis distributed node/deployment capacity leases.
- DONE: fail-closed acquisition and active lease-loss cancellation.
- DONE: PostgreSQL transactional outbox for DB -> Redis publication.
- DONE: same-transaction outbox capture for runtime mutations.
- DONE: acknowledged Redis publisher; in-memory enqueue is not delivery acknowledgement.
- DONE: globally ordered advisory-lock worker with retry/backoff/idempotent replay.
- DONE: non-originating publisher applies acknowledged event to its own L1.
- DONE: Redis outage/recovery fault smoke with originating gateway stopped.
- DONE: outbox backlog/retry/error diagnostics at `/api/admin/runtime-sync`.
- DONE: processed-outbox retention with pending-row preservation.
- PLANNED: customer-specific Redis HA/redundancy production guidance.

Canonical distributed-runtime baseline:

```text
commit     79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI         34976465066
Full Stack 34976465149
```

## M12 - Product hardening — ACTIVE

- ACTIVE: token/budget quota semantics and implementation.
- PLANNED: credential rotation workflow.
- PLANNED: backup/restore + restore verification.
- PLANNED: long-term reporting rollups where required.
- PLANNED: model/runtime upgrade and draining strategy.

## Current development order

1. Define and implement token/budget quota reservation/settlement across streaming, cancellation, failures and multi-replica Redis mode.
2. Implement credential rotation workflow.
3. Add backup/restore verification and remaining production hardening.
4. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.
