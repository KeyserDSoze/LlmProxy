# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not yet complete; `EXTERNAL` requires tenant/hardware/environment validation.

For current state use `docs/project-status.md`. For auth/rate limits/groups/usage reporting use `docs/usage-governance.md`. For installation use `QUICKSTART.md`.

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

## M3 - Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra ID plumbing.
- DONE: `LlmProxy.Admin` / `LlmProxy.Reader`.
- DONE: React control plane.
- DONE: HMAC-hashed DB-backed API credentials.
- DONE: in-memory credential lookup with startup rebuild and post-commit live publication.
- DONE: asynchronous/batched credential last-used persistence.
- DONE: audit trail.
- EXTERNAL: real Entra app registration/roles.

## M4 - Observability — DONE FOR CURRENT MVP / EXPORTS PLANNED

- DONE: request/status/duration metrics.
- DONE: TTFT and upstream latency.
- DONE: token usage extraction.
- DONE: failover/attempt visibility.
- DONE: vLLM running/waiting/KV-cache/token metrics.
- DONE: optional DCGM hardware telemetry.
- DONE: React Routing / Request Metrics / DGX Hardware views.
- PLANNED: Prometheus gateway exporter.
- PLANNED: OpenTelemetry export.

## M5 - Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/external-load/KV-cache routing signals.
- DONE: per-deployment EWMA TTFT/failure feedback.
- DONE: persisted smart-routing tuning.
- DONE: benchmark harness.
- DONE: persisted benchmark-derived Capacity Profile.
- DONE: recommendation separate from active concurrency.
- DONE: explicit/audited Apply recommended capacity.
- DONE: aggregate node-wide admission/backpressure.
- EXTERNAL: real DGX benchmark profiles and representative Copilot load.
- PLANNED: tune routing from measured DGX evidence.

Validated capacity-control commit:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

## M6 - Operator onboarding / deployability — DONE FOR REPOSITORY PATH / EXTERNAL DEPLOYMENT REMAINS

- DONE: GHCR publication workflow.
- DONE: private-image deployment path.
- DONE: Linux/Windows quickstart docs.
- DONE: private GHCR login/pull instructions.
- DONE: `docker/docker-compose.quickstart.yml` + env template.
- DONE: CI validation of quickstart Compose configuration.
- EXTERNAL: production Cloudflare Tunnel.
- EXTERNAL: self-hosted deployment runner.

## M7 - Caller governance — DONE FOR CURRENT MVP

Core product scope: LlmProxy owns inference auth, request-rate limiting, consolidated usage and configurable usage groups.

- DONE: bearer/API-key inference authentication.
- DONE: persisted request-rate policy per credential with optional logical-model override.
- DONE: fixed-window in-memory limiter and calculated `Retry-After`.
- DONE: distinct `429 rate_limit_exceeded` metrics/error taxonomy.
- DONE: Admin API/UI + audit for rate policies.
- DONE: runtime credential cache and DB-free credential auth decision.
- PLANNED: token/budget quotas with explicit reservation/settlement semantics.

Validated governance/auth baselines:

```text
Caller Governance     798f0a460dcc4f89b17e2ce89df66f511d324241 / CI 34859931084
Credential auth cache 1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
```

Required distinction:

```text
caller policy exceeded -> 429 rate_limit_exceeded
physical DGX saturated -> 429 capacity_exhausted
no operational backend -> 503 no_healthy_deployment
```

## M8 - Usage groups and enterprise reporting — DONE FOR CURRENT MVP

- DONE: persisted `UsageGroup` list/create/update administration.
- DONE: one optional primary `UsageGroupId` per API credential in V1.
- DONE: request-time Usage Group snapshot for historically stable accounting.
- DONE: usage aggregation by group, credential and logical model over a configurable day window.
- DONE: requests/errors/rate-limit/token/average TTFT/duration reporting.
- DONE: React **Usage & Governance** page.
- DONE: group membership management and credential/model breakdown.
- PLANNED: explicit Usage Group deletion/archive semantics if product requirements need them.
- EXTERNAL/PLANNED: GitHub Copilot usage-metrics ingestion for per-user/adoption analytics where desired.

Important limitation: if Copilot uses one shared provider key, gateway usage can be attributed to that credential/group but not to individual GitHub users. Do not infer users from IP.

## M9 - Inference hot-path hardening — ACTIVE

- DONE: routing policy/tuning kept in runtime state.
- DONE: caller rate policy/admission kept in runtime state.
- DONE: API credential authentication kept in runtime state.
- DONE: asynchronous/batched `LastUsedAtUtc` persistence.
- ACTIVE: replace request-time `EfDeploymentCatalog` SQL lookup with in-memory logical-model/node/deployment catalog.
- PLANNED: publish admin configuration mutations into catalog after successful DB save.
- PLANNED: clean separation between durable node config and volatile health/runtime state.
- PLANNED: integration assertion proving ordinary inference does not query route-catalog tables.

Do not claim the entire inference path is DB-free before M9 route-catalog work is validated.

## M10 - Product hardening — PLANNED

- PLANNED: configurable request-metric retention/background cleanup.
- PLANNED: audit retention policy.
- PLANNED: credential rotation workflow.
- PLANNED: reproducible frontend lockfiles + `npm ci`.
- PLANNED: backup/restore + restore verification.
- PLANNED: model/runtime upgrade and draining strategy.
- PLANNED: control-plane HA if required.

## Current development order

1. Replace `EfDeploymentCatalog` request-time DB lookup with a validated in-memory route catalog.
2. Add integration evidence for DB-free auth + route resolution on normal inference.
3. Add metrics/audit retention and cleanup.
4. Define/implement token-budget quota semantics.
5. Add Prometheus/OpenTelemetry exports and remaining hardening.
6. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.
