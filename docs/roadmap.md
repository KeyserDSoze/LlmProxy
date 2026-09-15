# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not yet complete; `EXTERNAL` requires tenant/hardware/environment validation.

For current state use `docs/project-status.md`. For runtime cache/Redis/outbox evolution use `docs/runtime-cache.md`. For auth/rate limits/groups/usage reporting use `docs/usage-governance.md`. For installation use `QUICKSTART.md` and `docs/full-stack.md`.

## M0 - Repository bootstrap — DONE

- .NET 10 layered solution.
- React/TypeScript admin shell.
- PostgreSQL + EF Core migrations.
- Docker packaging.
- GitHub Actions CI/container/deploy foundations.
- repository-first documentation/handover discipline.

## M1 - Copilot -> gateway -> one DGX — ACTIVE / EXTERNAL VALIDATION REMAINS

- DONE: logical model + deployment bootstrap.
- DONE: `/v1/models`.
- DONE: `/v1/chat/completions` + SSE.
- DONE: `/v1/responses` compatibility.
- DONE: arbitrary compatible payload preservation + logical model rewrite.
- DONE: bearer/API-key inference authentication.
- DONE: runtime API-credential cache; no per-request credential SQL lookup.
- DONE: runtime route/model/deployment catalog; no per-request route-catalog SQL lookup.
- EXTERNAL: real GitHub Copilot BYOK through target public endpoint.

## M2 - Multi-DGX — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration.
- DONE: health hysteresis and diagnostics.
- DONE: weighted least loaded / round robin / weighted round robin.
- DONE: pre-response failover.
- DONE: drain/disable states.
- DONE: path-prefixed service roots.
- DONE: deployment concurrency ceilings.
- DONE: aggregate node-wide concurrency.
- DONE: atomic node/deployment leases.
- DONE: explicit `429 capacity_exhausted`.
- DONE: Redis-coordinated node/deployment capacity leases across gateway replicas.
- DONE: fail-closed active-inference cancellation if a distributed lease becomes unsafe.

## M3 - Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra ID plumbing.
- DONE: `LlmProxy.Admin` / `LlmProxy.Reader`.
- DONE: React control plane.
- DONE: HMAC-hashed DB-backed API credentials.
- DONE: runtime credential lookup with startup rebuild and live publication.
- DONE: asynchronous/batched credential last-used persistence.
- DONE: audit trail.
- EXTERNAL: real Entra app registration/roles.

## M4 - Observability — DONE FOR CURRENT MVP / PRODUCTION STORAGE EVOLUTION REMAINS

- DONE: request/status/duration metrics.
- DONE: TTFT and upstream latency.
- DONE: token usage extraction.
- DONE: failover/attempt visibility.
- DONE: vLLM running/waiting/KV-cache/token metrics.
- DONE: optional DCGM hardware telemetry.
- DONE: React Routing / Request Metrics / DGX Hardware views.
- DONE: OpenTelemetry instrumentation and OTLP export.
- DONE: explicit application spans for auth/governance/routing/capacity.
- DONE: bundled Collector + Tempo + Loki + Prometheus + Grafana stack.
- DONE: trace correlation through `X-LlmProxy-Trace-Id`.
- DONE: lease-loss trace evidence with `capacity_lease_lost`.
- PLANNED: production-grade storage/HA choices for Tempo/Loki/Prometheus when required.
- PLANNED: additional native Prometheus business metrics only where they add value beyond current OTEL export.

## M5 - Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/external-load/KV-cache routing signals.
- DONE: per-deployment EWMA TTFT/failure feedback.
- DONE: persisted smart-routing tuning.
- DONE: benchmark harness.
- DONE: persisted benchmark-derived Capacity Profile.
- DONE: recommendation separate from active concurrency.
- DONE: explicit/audited Apply recommended capacity.
- DONE: aggregate node-wide admission/backpressure.
- DONE: Redis capacity lease renewal and expiry recovery.
- DONE: proactive lease-loss watchdog with pre-TTL cancellation.
- EXTERNAL: real DGX benchmark profiles and representative Copilot load.
- PLANNED: tune routing from measured DGX evidence.

Current validated distributed-capacity baseline:

```text
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
CI 34961566507
Full Stack 34961566463
```

## M6 - Operator onboarding / deployability — DONE FOR REPOSITORY PATH / EXTERNAL DEPLOYMENT REMAINS

- DONE: GHCR publication workflow.
- DONE: private-image deployment path.
- DONE: Linux/Windows quickstart docs.
- DONE: private GHCR login/pull instructions.
- DONE: minimal and full-stack Compose configurations.
- DONE: full-stack initialization scripts.
- DONE: CI validation of Compose configurations.
- EXTERNAL: production Cloudflare Tunnel.
- EXTERNAL: self-hosted deployment runner.

## M7 - Caller governance — DONE FOR CURRENT MVP / TOKEN BUDGETS PLANNED

- DONE: bearer/API-key inference authentication.
- DONE: persisted request-rate policy per credential with optional logical-model override.
- DONE: fixed-window limiter and calculated `Retry-After`.
- DONE: distinct `429 rate_limit_exceeded` metrics/error taxonomy.
- DONE: Admin API/UI + audit for rate policies.
- DONE: runtime credential cache and DB-free credential auth decision.
- DONE: Redis-backed request-rate counters shared across gateway replicas.
- PLANNED: token/budget quotas with explicit reservation/settlement semantics.

Required distinction:

```text
caller policy exceeded            -> 429 rate_limit_exceeded
physical DGX saturated            -> 429 capacity_exhausted
capacity coordinator unavailable  -> 503 capacity_coordination_unavailable
active capacity lease unsafe      -> 503/abort capacity_lease_lost
no operational backend            -> 503 no_healthy_deployment
```

## M8 - Usage groups and enterprise reporting — DONE FOR CURRENT MVP

- DONE: persisted `UsageGroup` list/create/update administration.
- DONE: one optional primary `UsageGroupId` per API credential in V1.
- DONE: request-time Usage Group snapshot for historically stable accounting.
- DONE: usage aggregation by group, credential and logical model over configurable time window.
- DONE: requests/errors/rate-limit/token/TTFT/duration reporting.
- DONE: React **Usage & Governance** page.
- DONE: group membership management and credential/model breakdown.
- PLANNED: explicit Usage Group deletion/archive semantics if product requirements need them.
- EXTERNAL/PLANNED: GitHub Copilot usage-metrics ingestion for per-user/adoption analytics where desired.

Important limitation: if Copilot uses one shared provider key, gateway usage can be attributed to that credential/group but not to individual GitHub users. Do not infer users from IP.

## M9 - Inference hot-path hardening — DONE FOR CURRENT RUNTIME

- DONE: routing policy/tuning kept in runtime state.
- DONE: caller rate policy kept in local L1.
- DONE: API credential authentication kept in runtime state.
- DONE: asynchronous/batched `LastUsedAtUtc` persistence.
- DONE: versioned copy-on-write logical-model/node/deployment runtime catalog.
- DONE: startup catalog rebuild from PostgreSQL.
- DONE: node health/drain/disable reflected in route snapshots.
- DONE: legacy request-time `EfDeploymentCatalog` removed.
- DONE: PostgreSQL-outage assertion proving `/v1/models` + authenticated inference continue after startup.
- DONE: Redis L2 synchronization while retaining local L1.

## M10 - Retention and operational hygiene — DONE FOR CURRENT MVP

- DONE: configurable request-metric retention; default 90 days.
- DONE: independent audit retention; default 365 days.
- DONE: background batched cleanup.
- DONE: manual admin cleanup endpoint + audit.
- DONE: dedicated Docker validation of independent cutoffs and retained recent rows.
- PLANNED: optional long-term usage rollups before deleting raw request metrics if reporting must exceed raw retention.

See `docs/data-retention.md`.

## M11 - Distributed runtime state / HA — ACTIVE

Current validated topology:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshots/events + coordination
local RAM  = per-replica request-path L1
```

- DONE: provider-neutral credential, route, rate-counter and capacity-gate abstractions.
- DONE: Redis snapshot/version/event synchronization.
- DONE: local L1 update on peer replicas.
- DONE: periodic reconciliation after missed Redis pub/sub/reconnect.
- DONE: runtime synchronization diagnostics.
- DONE: global Redis-backed rate-limit counters.
- DONE: distributed Redis node/deployment capacity leases.
- DONE: fail closed when Redis capacity admission is unavailable.
- DONE: active lease-loss cancellation before lease expiry.
- DONE: metric + Tempo trace evidence for `capacity_lease_lost`.
- ACTIVE: PostgreSQL transactional outbox for reliable DB -> Redis publication.
- PLANNED: outbox lag/delivery diagnostics and operational SLOs.
- PLANNED: production Redis HA/redundancy deployment guidance.

The outbox acknowledgement boundary must be a successful durable Redis state write/publication, not an in-memory channel enqueue.

See `docs/runtime-cache.md`.

## M12 - Product hardening — PLANNED

- PLANNED: token/budget quota semantics and implementation.
- PLANNED: credential rotation workflow.
- PLANNED: backup/restore + restore verification.
- PLANNED: long-term reporting rollups where required.
- PLANNED: model/runtime upgrade and draining strategy.

## Current development order

1. Implement PostgreSQL transactional outbox for durable runtime-state publication to Redis.
2. Define/implement token-budget quota reservation/settlement semantics.
3. Finish credential rotation, backup/restore and other production hardening.
4. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.