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
- DONE: hashed DB-backed API credentials.
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

## M6 - Operator onboarding / deployability — ACTIVE VALIDATION

- DONE: GHCR publication workflow.
- DONE: private-image deployment path.
- DONE: Linux/Windows quickstart docs.
- DONE: private GHCR login/pull instructions.
- DONE: `docker/docker-compose.quickstart.yml` + env template.
- ACTIVE: CI validation of latest quickstart/docs head.
- EXTERNAL: production Cloudflare Tunnel.
- EXTERNAL: self-hosted deployment runner.

## M7 - Caller governance — ACTIVE

Core product scope: LlmProxy owns inference auth, rate limiting, consolidated usage and configurable usage groups.

- DONE: bearer/API-key inference authentication.
- DONE: credential id and token/model/node metadata in request metrics.
- ACTIVE: request-rate policy per inference credential with optional logical-model override.
- PLANNED: in-memory limiter + computed `Retry-After`.
- PLANNED: distinct `429 rate_limit_exceeded` metrics/error taxonomy.
- PLANNED: Admin API/UI + audit for rate policies.
- PLANNED: token/budget quotas after request-rate limiting.

Required distinction:

```text
caller policy exceeded -> 429 rate_limit_exceeded
physical DGX saturated -> 429 capacity_exhausted
no operational backend -> 503 no_healthy_deployment
```

## M8 - Usage groups and enterprise reporting — PLANNED

- PLANNED: persisted `UsageGroup` CRUD.
- PLANNED: one optional primary `UsageGroupId` per API credential in V1.
- PLANNED: snapshot group id into request metrics for historically stable accounting.
- PLANNED: usage aggregation by time/group/credential/logical model/node.
- PLANNED: request/success/error/rate-limit/capacity-reject/token/TTFT/duration metrics.
- PLANNED: React **Usage & Governance** page.
- PLANNED: group -> credential -> model drill-down.
- EXTERNAL/PLANNED: GitHub Copilot usage-metrics ingestion for per-user/adoption analytics where desired.

Important limitation: if Copilot uses one shared provider key, gateway usage can be attributed to that credential/group but not to individual GitHub users. Do not infer users from IP.

## M9 - Product hardening — PLANNED

- PLANNED: credential rotation workflow.
- PLANNED: backup/restore + restore verification.
- PLANNED: model/runtime upgrade and draining strategy.
- PLANNED: control-plane HA if required.

## Current development order

1. Finish CI validation for the latest onboarding/docs head.
2. Implement credential/model request-rate policy.
3. Enforce it in memory before routing/admission.
4. Add `429 rate_limit_exceeded`, `Retry-After`, audit, metrics and Admin UI.
5. Validate full quality gate.
6. Add UsageGroup persistence and credential assignment.
7. Snapshot group id in request metrics and build grouped usage APIs/UI.
8. Validate full quality gate.
9. Add Prometheus/OpenTelemetry export and remaining hardening.
10. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless explicitly reopened.
