# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**.

The repository — not old chat history — is the handover mechanism.

## Mandatory resume protocol

Before changing code, read these sources in this order:

1. `AGENTS.md` — engineering rules, architecture constraints and current resume point.
2. `docs/project-status.md` — canonical snapshot of validated work, unfinished work and external dependencies.
3. `docs/development-log.md` — chronological engineering trace and validation evidence.
4. `docs/roadmap.md` — DONE / ACTIVE / PLANNED / EXTERNAL milestones.
5. The focused document relevant to the task.
6. Inspect latest `main` and the GitHub Actions result before treating an increment as complete.

Source-of-truth precedence:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md
    > docs/development-log.md
    > docs/roadmap.md
    > focused technical documents
    > old chat context
```

If documentation disagrees with validated code, fix the stale documentation as part of the next change.

## Operator / installation onboarding

For installing and testing the product on a Linux VM or Windows workstation start here:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

`docs/quickstart.md` is the operational “from zero to first request” guide and covers:

- Docker Engine + Compose on Ubuntu;
- Docker Desktop + WSL 2 on Windows;
- private GHCR authentication;
- pulling `ghcr.io/keyserdsoze/llmproxy:*`;
- `docker/docker-compose.quickstart.yml`;
- PostgreSQL and `.env` setup;
- real vLLM or local mock runtime;
- health/readiness/Admin UI/OpenAI endpoint tests;
- optional Microsoft Entra configuration;
- update/reset/troubleshooting.

Do not duplicate those setup instructions elsewhere unless there is a specific operational reason; link to the quickstart instead.

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway. Initial target: roughly 200 developers using GitHub Copilot while inference is served by one NVIDIA DGX Spark and can scale to six nodes.

```text
GitHub Copilot / OpenAI-compatible client
    -> public HTTPS endpoint
    -> Cloudflare Tunnel
    -> LlmProxy (.NET 10)
    -> auth / logical model / capacity / smart routing / telemetry
    -> vLLM on DGX Spark 1..N
```

Clients see logical models such as `agic-code-fast`; physical DGX topology and provider model identifiers stay internal.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code only under `src/`.
- Automated tests/test tooling only under `tests/`.
- Docker/deployment assets under `docker/`.
- Documentation under `docs/` plus root onboarding files such as this file and `QUICKSTART.md`.
- Work currently happens directly on `main` unless project owner says otherwise.
- Do not log prompts, source code, model outputs, bearer tokens or raw API secrets.
- Raw API keys must never be persisted; only secure hashes/metadata.
- PostgreSQL must stay out of the inference hot path.
- Preserve SSE streaming end-to-end and propagate cancellation.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases, not provider/DGX identifiers.
- Capacity claims must come from benchmark evidence, not license count.
- GPU/DCGM telemetry is observational unless benchmark evidence justifies scheduling use.
- Every meaningful increment must update focused docs, `docs/project-status.md`, `docs/development-log.md`, `docs/roadmap.md` when milestone state changes, and this file when architecture/next-step/onboarding changes.
- Never call work DONE merely because it was committed: require the relevant CI/integration evidence.

## Repository shape

```text
src/
  LlmProxy.Api/
  LlmProxy.Application/
  LlmProxy.Domain/
  LlmProxy.Infrastructure/
  LlmProxy.Admin/

tests/
  backend/LlmProxy.UnitTests/
  backend/integration/
  frontend/unit/
  frontend/e2e/
  performance/

docker/
docs/
.github/workflows/
QUICKSTART.md
AGENTS.md
```

## Current validated baseline

Last reviewed: **2026-09-14**.

Latest fully validated capacity-control baseline:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

The complete quality gate passed for that commit:

- .NET 10 restore/build;
- backend xUnit;
- benchmark-harness xUnit;
- React production build;
- Vitest;
- Playwright Chromium;
- production Docker build;
- Docker/PostgreSQL inference smoke;
- DCGM hardware smoke;
- capacity/backpressure smoke.

Operational onboarding files were added after that baseline and must themselves remain CI-valid.

## Validated capabilities

### Public inference surface

- `GET /v1/models`.
- `POST /v1/chat/completions` streaming/non-streaming.
- `POST /v1/responses` compatibility path.
- OpenAI-compatible arbitrary payload preservation with only logical model rewriting.
- incremental SSE forwarding.
- cancellation propagation.
- pre-response failover only.

### Security / administration

- bearer/API-key inference authentication suitable for Copilot BYOK.
- HMAC-hashed persisted credentials; raw secret shown once.
- Entra ID admin plumbing.
- roles `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin UI.
- administrative audit trail.

### Multi-DGX / routing

- full HTTP(S) service roots with host/IP/port/path prefixes.
- health hysteresis and diagnostics.
- `Healthy`, `Degraded`, `Unhealthy`, `Draining`, `Disabled` behavior.
- `WeightedLeastLoaded`, `RoundRobin`, `WeightedRoundRobin`.
- node/deployment weights.
- pre-response failover.
- live persisted routing strategy and smart-routing tuning.

### Observability

- request duration/status/attempt/failover metrics.
- upstream-header latency and TTFT.
- token counts when upstream reports usage.
- vLLM Prometheus running/waiting/KV-cache/token telemetry.
- per-deployment EWMA TTFT/duration/infrastructure-failure feedback.
- optional DCGM GPU utilization/framebuffer/temperature/power telemetry.
- hardware telemetry isolated from inference health.

### Benchmarking and capacity

- .NET 10 benchmark harness under `tests/performance/`.
- direct-vLLM vs gateway comparison.
- Chat Completions / Responses, stream/non-stream.
- warm-up and concurrency sweeps.
- p50/p95/p99 TTFT/duration, req/s and token throughput.
- persisted deployment Capacity Profile kept separate from active limit.
- explicit audited “apply recommended capacity”.
- atomic deployment + node-wide capacity lease.
- aggregate physical DGX concurrency across deployments.
- capacity exhaustion returns deliberate `429` + `Retry-After` + `capacity_exhausted`.
- unavailable healthy backends remain distinct (`503 no_healthy_deployment`).

## Routing hot path

Routing and admission use in-memory runtime/configuration state. Do not query PostgreSQL per inference request.

`WeightedLeastLoaded` currently considers:

```text
configured node/deployment weight and capacity
+ gateway active request count
+ node health
+ deployment EWMA TTFT/failure feedback
+ vLLM running requests
+ vLLM waiting queue
+ vLLM KV-cache pressure
= routing score
```

Admission must then atomically acquire both deployment and node-wide capacity before sending upstream.

## Hardware telemetry boundary

```text
InferenceNode.BaseAddress
    -> /health, /v1/*, vLLM /metrics

InferenceNode.HardwareMetricsBaseAddress (optional)
    -> NVIDIA/DCGM /metrics
```

DCGM failure must not make an inference node unhealthy.

## Important admin endpoints

```http
GET  /api/admin/overview
GET  /api/admin/routing
PUT  /api/admin/routing
GET  /api/admin/routing/tuning
PUT  /api/admin/routing/tuning
GET  /api/admin/routing/performance
GET  /api/admin/routing/runtime
GET  /api/admin/hardware
GET  /api/admin/capacity
GET  /api/admin/nodes
POST /api/admin/nodes/{id}/test-connection
PUT  /api/admin/nodes/{id}/hardware-metrics
PUT  /api/admin/deployments/{id}/capacity-profile
DELETE /api/admin/deployments/{id}/capacity-profile
POST /api/admin/deployments/{id}/capacity-profile/apply
GET  /api/admin/metrics
GET  /api/admin/metrics/summary
GET  /api/admin/audit
```

## Testing rules

Mock external boundaries, not domain behavior.

CI quality gate includes:

1. .NET restore/build/backend unit tests.
2. benchmark-harness tests.
3. React build + Vitest.
4. Playwright Chromium.
5. production Docker image build.
6. Docker/PostgreSQL inference smoke.
7. DCGM hardware smoke.
8. capacity/backpressure smoke.

Real performance load is never an automatic CI action.

CI uses `cancel-in-progress` so obsolete runs on the same branch/PR are stopped.

## Current development focus / resume point

Capacity Profile + node-wide capacity/backpressure is now validated.

The next product increment that does not require external hardware/tenant access is **rate limits / quotas by credential and logical model**, intentionally separate from physical-capacity backpressure.

Recommended sequence:

1. define persisted rate-limit policy keyed by inference credential, with optional logical-model override;
2. start with requests-per-minute/window admission rather than token quotas;
3. publish policy into an in-memory limiter so PostgreSQL is not on the hot path;
4. return a distinct OpenAI-style `429 rate_limit_exceeded` with computed `Retry-After`;
5. keep physical saturation as `429 capacity_exhausted` so operators can distinguish quota vs infrastructure pressure;
6. add audit + Admin API/UI;
7. add unit/concurrent integration tests and metrics;
8. run complete quality gate;
9. update handover docs only to VALIDATED after CI is green.

After that, priority work includes gateway Prometheus/OpenTelemetry export, backup/restore verification, credential rotation, model/runtime upgrade/draining runbook and production HA/hardening.

## External validation still required

- real DGX Spark + intended vLLM/model profile;
- matched direct-vLLM vs gateway benchmark runs;
- representative multi-DGX coding load;
- real Microsoft Entra app registration/role assignment;
- Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted GitHub Actions deployment runner;
- GitHub Copilot usage-metrics behavior if used for per-user analytics.

Central Copilot BYOK may expose only a shared provider credential at the gateway. Do not infer people from source IP.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router (PAIR) was evaluated. The project owner explicitly chose to continue with our custom **LlmProxy + vLLM** architecture. Do not redirect implementation toward PAIR unless that decision is explicitly reopened.

## Documentation map

```text
QUICKSTART.md / docs/quickstart.md
    installation, downloads, GHCR, Linux/Windows, first smoke test

docs/project-status.md
    canonical current snapshot and exact resume point

docs/development-log.md
    chronological engineering/validation trace

docs/roadmap.md
    milestone state and backlog

docs/benchmarking.md
    benchmark protocol and capacity evidence

docs/capacity-control.md
    Capacity Profile, node-wide admission and backpressure

docs/hardware-telemetry.md
    DCGM telemetry boundary

docs/routing.md
    routing and smart-routing tuning

docs/github-copilot.md
    Copilot/BYOK integration and remaining external spike

docs/deployment.md / docs/operations.md / docs/security.md
    deployment, runtime operations and security
```

## Handover checklist

Before ending a meaningful development session, make sure the repository states:

- what changed;
- exact green CI commit/run evidence;
- what remains unverified;
- current architecture decisions;
- exact next technical step;
- external dependencies;
- focused docs changed.

If `docs/project-status.md` still points to an already-completed increment, fix it before considering the handover clean.
