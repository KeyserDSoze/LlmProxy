# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not complete; `EXTERNAL` requires target environment/hardware.

For canonical current state use `docs/project-status.md`.

## M0 - Repository bootstrap — DONE

- .NET 10 layered solution, React/TypeScript admin, PostgreSQL/EF, Docker/GitHub Actions/GHCR.
- repository-first handover discipline.

## M1 - Copilot -> gateway -> one DGX — ACTIVE / EXTERNAL VALIDATION REMAINS

- DONE: logical models, `/v1/models`, Chat Completions + SSE, Responses compatibility.
- DONE: bearer/API-key auth from runtime L1.
- DONE: runtime route/model/deployment catalog.
- EXTERNAL: real GitHub Copilot BYOK through target public endpoint.

## M2 - Multi-DGX — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration, health hysteresis, drain/disable.
- DONE: weighted least loaded / round robin / weighted round robin.
- DONE: pre-response-only failover.
- DONE: deployment + physical-node capacity admission.
- DONE: Redis distributed capacity leases and fail-closed active lease-loss handling.

## M3 - Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra plumbing and Admin/Reader roles.
- DONE: React control plane.
- DONE: HMAC-hashed DB-backed credentials + runtime cache.
- DONE: asynchronous last-used persistence + audit.
- EXTERNAL: real Entra app registration/roles.

## M4 - Observability — DONE FOR CURRENT MVP / STORAGE EVOLUTION REMAINS

- DONE: request/status/duration/TTFT/token/attempt metrics.
- DONE: vLLM pressure + optional DCGM telemetry.
- DONE: OTEL spans and Collector + Tempo + Loki + Prometheus + Grafana bundle.
- DONE: trace correlation and `capacity_lease_lost` evidence.
- PLANNED: customer-specific production HA/object-storage choices.

## M5 - Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/running/KV-cache signals and EWMA feedback.
- DONE: persisted routing tuning and benchmark-derived Capacity Profiles.
- DONE: benchmark harness + audited capacity apply workflow.
- DONE: Redis lease renewal/recovery and proactive safety watchdog.
- EXTERNAL: real DGX benchmark profiles + representative Copilot load.

## M6 - Operator onboarding / deployability — DONE FOR REPOSITORY PATH / EXTERNAL DEPLOYMENT REMAINS

- DONE: private GHCR path, Linux/Windows quickstart, minimal/full Compose and init scripts.
- DONE: production image/Compose CI validation.
- DONE: outbox batch/poll and processed-outbox retention knobs.
- EXTERNAL: production Cloudflare Tunnel + self-hosted deployment runner.

## M7 - Caller governance — DONE FOR CURRENT V1

- DONE: bearer/API-key inference authentication.
- DONE: credential/model request-rate policies.
- DONE: fixed-window rate admission + `Retry-After` + `429 rate_limit_exceeded`.
- DONE: Redis shared request counters across replicas.
- DONE: output-token budget policy on credential/model scope.
- DONE: pre-inference reservation preventing concurrent oversubscription.
- DONE: Chat/Responses output-cap injection/capping.
- DONE: actual-output settlement and unused-reservation refund.
- DONE: conservative full charge when post-upstream usage is uncertain.
- DONE: `429 token_budget_exceeded`.
- DONE: Redis shared output-token window across replicas.
- DONE: fail-closed `503 token_budget_coordination_unavailable` when Redis cannot coordinate.
- DONE: runtime/outbox propagation of quota policy to peer L1.
- DONE: Admin API + React UI + audit for Apply/Clear output-token budget.
- DONE: local restart and distributed Redis outage/recovery smoke coverage.

Validated runtime quota checkpoint:

```text
commit     887ebfac98389c0115eaf9c102a60133ede745ff
CI         34987407172
Full Stack 34987407169
```

React Admin quota management checkpoint:

```text
commit     426c545e841865406615998ca50b28a45c40e6f4
CI         34988084106
```

Future quota extensions are PLANNED only when requirements justify them:

- input/total-token budgets after tokenizer/estimation semantics are defined;
- monetary/cost budgets after pricing/accounting semantics are defined;
- independent token-budget period separate from request-rate `WindowSeconds`;
- provider-contract anomaly handling if actual output exceeds the enforced cap.

## M8 - Usage Groups and reporting — DONE FOR CURRENT MVP

- DONE: UsageGroup administration and primary group per credential.
- DONE: request-time group snapshot for stable historical accounting.
- DONE: usage aggregation/UI by group, credential and logical model.
- PLANNED: archive/deletion semantics if required.
- PLANNED/EXTERNAL: Copilot usage-metrics ingestion for per-user/adoption analytics.

Shared Copilot credentials are not individual user identity. Never infer users from IP.

## M9 - Inference hot-path hardening — DONE FOR CURRENT RUNTIME

- DONE: credential, route and caller-policy definitions from local L1.
- DONE: DB-free ordinary configuration lookup after startup.
- DONE: PostgreSQL-outage inference smoke.
- DONE: Redis L2 synchronization while preserving L1.

## M10 - Retention and operational hygiene — DONE FOR CURRENT MVP

- DONE: request metrics 90-day default.
- DONE: audit 365-day default.
- DONE: processed runtime outbox 30-day default.
- DONE: pending outbox never retention-deleted.
- DONE: batched worker + manual audited cleanup + Docker smoke.
- PLANNED: long-term usage rollups if reporting must exceed raw retention.

## M11 - Distributed runtime state / HA — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + runtime-state outbox
Redis      = distributed L2 + shared request/capacity/token-budget coordination
local RAM  = per-replica request-path L1
```

- DONE: Redis snapshot/version/event synchronization + reconciliation.
- DONE: peer L1 updates and runtime-sync diagnostics.
- DONE: global request-rate counters.
- DONE: distributed capacity leases + active lease-loss safety.
- DONE: PostgreSQL transactional outbox for DB -> Redis publication.
- DONE: globally ordered advisory-lock worker with retry/idempotent replay.
- DONE: non-originating publisher self-L1 application.
- DONE: outbox backlog/retry/error diagnostics + processed-only retention.
- DONE: Redis shared output-token budget admission/settlement.
- PLANNED: customer-specific Redis HA/redundancy production guidance.

## M12 - Product hardening — ACTIVE

- DONE: V1 output-token quota reservation/settlement.
- ACTIVE NEXT: credential rotation workflow.
- PLANNED: backup/restore + restore verification.
- PLANNED: long-term reporting rollups where required.
- PLANNED: model/runtime upgrade and draining strategy.

## Current development order

1. Implement credential rotation without ever persisting/re-exposing raw credential secrets.
2. Add backup/restore procedures and automated/explicit restore verification.
3. Expand quota semantics only if requirements call for input/total/cost budgets or independent periods.
4. Add reporting rollups / production HA-storage guidance as required.
5. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.
