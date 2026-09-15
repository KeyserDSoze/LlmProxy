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
- DONE: in-place credential rotation with one-time secret, safe audit and cross-replica hard cutover.
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
- DONE: PostgreSQL backup/restore Bash + PowerShell operator scripts.
- DONE: destructive clean-target restore CI proof and PowerShell operator-path CI proof.
- EXTERNAL: production Cloudflare Tunnel + self-hosted deployment runner.
- EXTERNAL: customer backup destination, encryption, retention schedule and native deployment-host acceptance.

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

Future quota extensions are PLANNED only when requirements justify them: input/total-token budgets after tokenizer/estimation semantics; monetary/cost budgets after pricing/accounting semantics; independent token-budget periods; provider-contract anomaly handling.

## M8 - Usage Groups and reporting — DONE FOR CURRENT MVP

- DONE: UsageGroup administration and primary group per credential.
- DONE: request-time group snapshot for stable historical accounting.
- DONE: usage aggregation/UI by group, credential and logical model.
- DONE: credential rotation preserves group and historical credential identity.
- PLANNED: archive/deletion semantics if required.
- PLANNED: long-term rollups if reporting must outlive raw retention.
- PLANNED/EXTERNAL: Copilot usage-metrics ingestion for per-user/adoption analytics.

Shared Copilot credentials are not individual user identity. Never infer users from IP.

## M9 - Inference hot-path hardening — DONE FOR CURRENT RUNTIME

- DONE: credential, route and caller-policy definitions from local L1.
- DONE: DB-free ordinary configuration lookup after startup.
- DONE: PostgreSQL-outage inference smoke.
- DONE: Redis L2 synchronization while preserving L1.
- DONE: credential hash replacement removes old secret acceptance from local L1.

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
- DONE: credential rotation propagation and old-hash removal across replicas.
- DONE: clean Redis runtime reconstruction from restored PostgreSQL demonstrated in restore smoke.
- PLANNED: customer-specific Redis HA/redundancy production guidance.

## M12 - Product hardening — ACTIVE

- DONE: V1 output-token quota reservation/settlement.
- DONE: credential rotation workflow.
- DONE: backup/restore + actual clean-target restore verification.
- ACTIVE NEXT: model/runtime upgrade + draining strategy.
- PLANNED: long-term reporting rollups where required.

Credential-rotation validation checkpoint:

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467
Full Stack 34996328588
```

Backup/restore validation checkpoint:

```text
commit     66d7a809936f0f21f330d84887c1bb6a4e536f97
CI         35018579785 SUCCESS
```

The backup/restore CI proves destructive Linux clean-target recovery plus PowerShell operator semantics. Native customer Windows/Docker Desktop remains deployment-environment acceptance rather than repository CI.

## Current development order

1. Implement safe model/runtime upgrade + draining sequencing with no new work routed to a draining target and no unsafe interruption of in-flight streaming work.
2. Expand quota semantics only if requirements call for input/total/cost budgets or independent periods.
3. Add reporting rollups / customer-specific production HA-storage and scheduled-backup guidance as required.
4. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.
