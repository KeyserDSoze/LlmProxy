# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not yet complete; `EXTERNAL` requires tenant/hardware/environment validation.

For the exact handover/resume point use `docs/project-status.md`. For installation/testing use `QUICKSTART.md` / `docs/quickstart.md`.

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
- EXTERNAL: real GitHub Copilot BYOK through the target public endpoint.

## M2 - Multi-DGX — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration.
- DONE: health hysteresis and diagnostics.
- DONE: weighted least-loaded / round-robin / weighted-round-robin.
- DONE: pre-response failover.
- DONE: drain/disable states.
- DONE: path-prefixed complete service roots.
- DONE: deployment concurrency ceilings.
- DONE: aggregate physical-node concurrency across deployments.
- DONE: atomic node/deployment admission leases.
- DONE: explicit `429 capacity_exhausted` backpressure.

## M3 - Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra ID application plumbing.
- DONE: `LlmProxy.Admin` / `LlmProxy.Reader` policies.
- DONE: React control plane.
- DONE: hashed DB-backed API credentials.
- DONE: audit trail.
- EXTERNAL: real Entra app registration, redirect URI, client credential and role assignment.

## M4 - Observability — DONE FOR CURRENT MVP / EXPORTS PLANNED

- DONE: request metrics/status/duration.
- DONE: TTFT and upstream latency.
- DONE: token usage extraction.
- DONE: failover/attempt visibility.
- DONE: vLLM running/waiting/KV-cache/token metrics.
- DONE: optional DCGM GPU utilization/memory/temperature/power.
- DONE: React Routing / Request Metrics / DGX Hardware views.
- PLANNED: Prometheus gateway exporter.
- PLANNED: OpenTelemetry export.

## M5 - Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/external-load/KV-cache routing signals.
- DONE: per-deployment EWMA TTFT/failure feedback.
- DONE: persisted live smart-routing tuning.
- DONE: benchmark harness for direct-vLLM vs gateway profiling.
- DONE: persisted benchmark-derived Capacity Profile.
- DONE: recommendation separate from active production concurrency.
- DONE: explicit/audited Apply recommended capacity.
- DONE: aggregate node-wide admission and capacity backpressure.
- EXTERNAL: real DGX Spark benchmark profiles by model/runtime/quantization/context.
- EXTERNAL: representative Copilot coding load.
- PLANNED: tune smart-routing coefficients from measured DGX evidence.
- DEFERRED: direct GPU/DCGM routing penalties until benchmarks prove they help.

Validated capacity-control commit:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

## M6 - Operator onboarding / deployability — ACTIVE VALIDATION

- DONE: GHCR publication workflow after CI success.
- DONE: private-image deployment path.
- DONE: `QUICKSTART.md` / `docs/quickstart.md`.
- DONE: Ubuntu Docker Engine/Compose installation instructions.
- DONE: Windows Docker Desktop + WSL 2 instructions.
- DONE: private GHCR PAT/login/pull instructions.
- DONE: `docker/docker-compose.quickstart.yml` + env template.
- ACTIVE: final CI validation of the quickstart Compose/config documentation head.
- PLANNED/EXTERNAL: production Cloudflare Tunnel setup.
- PLANNED/EXTERNAL: self-hosted deployment runner on target VM.

## M7 - Caller governance / product hardening — ACTIVE

- ACTIVE: rate limits by inference credential and logical model.
- PLANNED: token/budget quotas after request-rate limiting.
- PLANNED: credential rotation workflow.
- PLANNED: backup/restore runbook + restore verification.
- PLANNED: model/runtime upgrade and draining strategy.
- PLANNED: control-plane HA if required.

Rate-limit target behavior:

```text
caller policy exceeded
  -> 429 rate_limit_exceeded

physical DGX saturated
  -> 429 capacity_exhausted
```

These conditions must remain operationally distinguishable.

## M8 - Usage analytics / enterprise reporting — PLANNED / EXTERNAL

- EXTERNAL: validate GitHub Copilot usage-metrics/custom-model reporting against target tenant.
- PLANNED: ingest user/adoption/model usage snapshots if needed.
- PLANNED: correlate GitHub user analytics with gateway infrastructure telemetry without claiming per-request user identity when the provider key is shared.

## Current development order

1. Finish CI validation for the operator quickstart/config head.
2. Implement persisted credential/model request-rate policy.
3. Enforce it in memory before routing/admission.
4. Return distinct `429 rate_limit_exceeded` + computed `Retry-After`.
5. Add Admin API/UI, audit, metrics and concurrent integration tests.
6. Validate complete quality gate.
7. Add Prometheus/OpenTelemetry gateway export.
8. Add backup/restore and credential rotation hardening.
9. When hardware/tenant access exists, run real DGX benchmark + Copilot BYOK + Entra/Cloudflare acceptance.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue with LlmProxy + vLLM unless the project owner explicitly reopens that decision.
