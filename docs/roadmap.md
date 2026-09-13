# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current development focus; `PLANNED` not yet complete; `EXTERNAL` requires tenant/hardware/environment validation.

For the exact current-state handover and resume point, use `docs/project-status.md`.

## M0 - Repository bootstrap — DONE

- .NET 10 solution and layered single-domain structure.
- React/TypeScript admin shell.
- PostgreSQL persistence and EF Core migrations.
- Docker packaging.
- GitHub Actions CI/container/deploy foundations.
- Architecture/security/deployment/testing documentation.

## M1 - Copilot -> gateway -> one DGX — ACTIVE / EXTERNAL VALIDATION REMAINS

- DONE: seed one DGX node, logical model and deployment.
- DONE: OpenAI-compatible `/v1/models`.
- DONE: `/v1/chat/completions` with SSE streaming.
- DONE: `/v1/responses` compatibility path.
- DONE: preserve arbitrary OpenAI payload fields including tool/function-calling payloads while rewriting logical model IDs.
- DONE: bearer/API-key inference authentication.
- EXTERNAL: validate the complete path with the real GitHub Copilot BYOK configuration in the target tenant/public endpoint.

## M2 - Multi-DGX — DONE FOR MVP

- DONE: node/model/deployment administration.
- DONE: active health monitor with hysteresis.
- DONE: weighted least-loaded routing.
- DONE: round-robin and weighted-round-robin alternatives.
- DONE: pre-response failover.
- DONE: drain mode.
- DONE: node/deployment configured concurrency controls.
- DONE: path-prefixed DGX service roots.
- ACTIVE: enforce node-wide aggregate concurrency across multiple deployments sharing one DGX.
- ACTIVE: explicit saturation/backpressure behavior when capacity is exhausted.

## M3 - Enterprise administration — DONE FOR MVP

- DONE: Entra ID production sign-in plumbing.
- DONE: `LlmProxy.Admin` and `LlmProxy.Reader` roles.
- DONE: React operational dashboard.
- DONE: database-backed hashed API credentials.
- DONE: administrative audit trail.
- EXTERNAL: register/configure the real Entra application and production role assignments.

## M4 - Observability — DONE FOR CURRENT MVP / MORE EXPORTS PLANNED

- DONE: request metrics and per-request status/duration.
- DONE: TTFT/upstream latency and percentiles.
- DONE: token usage extraction when reported by upstream.
- DONE: failover/attempt visibility.
- DONE: vLLM runtime running/waiting/KV-cache telemetry.
- DONE: optional NVIDIA/DCGM hardware telemetry for GPU utilization, framebuffer memory, temperature and power, isolated from inference health/routing.
- DONE: React DGX Hardware administration/visibility and dedicated Docker integration smoke coverage.
- PLANNED: OpenTelemetry export.
- PLANNED: Prometheus/Grafana integration option for gateway metrics.

## M5 - Capacity and smart routing — ACTIVE

- DONE: queue-depth-aware routing using vLLM waiting requests.
- DONE: external-runtime-load awareness using vLLM running requests.
- DONE: KV-cache pressure signal.
- DONE: per-deployment EWMA TTFT/failure feedback.
- DONE: live, persisted, audited smart-routing tuning profile.
- DONE: repeatable .NET benchmark harness for gateway-vs-direct-vLLM concurrency profiling; full CI green on commit `49e7932f14118be403eec042a1393946143776ae`.
- ACTIVE: persisted benchmark-derived Capacity Profile kept separate from the active runtime concurrency limit.
- ACTIVE: explicit/audited application of recommended capacity.
- ACTIVE: aggregate physical-node concurrency enforcement and backpressure.
- PLANNED/EXTERNAL: representative Copilot coding load tests on real DGX Spark.
- PLANNED/EXTERNAL: measured model-specific concurrency profiles from real DGX/vLLM runs.
- PLANNED: calibration of smart-routing coefficients from measured DGX Spark data.
- PLANNED: capacity planning for roughly 200 assigned developers based on measured concurrency, not license count.
- DEFERRED: GPU hardware signals in routing until benchmarks prove useful thresholds.

## M6 - Product hardening — PLANNED / PARTIALLY DONE

- DONE: automated EF Core migrations at startup.
- DONE: immutable container publication path through GHCR.
- PLANNED: backup/restore runbook and automated verification.
- PLANNED: credential rotation workflow.
- PLANNED: rate limits/quotas by credential/model.
- PLANNED: gateway high availability if required.
- PLANNED: model/runtime upgrade and draining strategy.
- PLANNED: second control-plane instance / database HA design for product-grade availability.

## M7 - Usage analytics / enterprise reporting — PLANNED / EXTERNAL

- PLANNED/EXTERNAL: validate GitHub Copilot usage-metrics APIs/reports against the target tenant and custom model configuration.
- PLANNED: ingest user/adoption/model usage snapshots if required by the product/reporting scope.
- PLANNED: correlate GitHub usage analytics with gateway infrastructure telemetry without pretending there is per-request user identity when the provider credential is shared.

## Current development order

1. Implement Capacity Profile persistence/API/admin visibility while keeping recommendations separate from active limits.
2. Add explicit audited application of recommended deployment concurrency.
3. Enforce aggregate node-wide concurrency across deployments sharing one DGX and define saturation/backpressure semantics.
4. Validate the capacity-control increment with unit, frontend and Docker/PostgreSQL integration tests.
5. Run measured concurrency profiles on real DGX/vLLM once hardware is available and populate/calibrate model/deployment capacity profiles.
6. Validate real GitHub Copilot BYOK through the public/tunnel endpoint when tenant/infrastructure access is available.
7. Tune smart-routing parameters from benchmark evidence.
8. Add gateway metrics export, rate limits/quotas and production hardening.

NVIDIA Personal AI Router (PAIR) was reviewed as a possible southbound alternative, but the project decision is to continue with the current LlmProxy/vLLM architecture rather than adopt PAIR.
