# AGENTS.md

This file is the primary handover and working-context document for humans and AI coding agents working on **LlmProxy**. Read it before changing code. Update it whenever an increment changes architecture, behavior, operations, tests, deployment, or the next development step.

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway. The initial target is about 200 developers using GitHub Copilot while inference is served by one NVIDIA DGX Spark and can scale to six DGX Spark nodes. Clients must see stable logical model names while hardware, vLLM model identifiers and deployment topology stay hidden behind the gateway.

The primary end-to-end path is:

```text
GitHub Copilot / OpenAI-compatible client
    -> public HTTPS endpoint
    -> Cloudflare Tunnel
    -> LlmProxy (.NET 10)
    -> smart routing / health / auth / telemetry
    -> vLLM on DGX Spark 1..N
```

## Non-negotiable engineering conventions

- Backend: **.NET 10 / ASP.NET Core**, C#.
- Frontend: **React + TypeScript**.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker containers; GitHub Actions builds/tests/publishes images.
- Repository work currently happens directly on `main` unless branch protection or the project owner says otherwise.
- Keep product code under `src/`, tests under `tests/`, operational containers under `docker/`, documentation under `docs/`.
- Every meaningful feature must update the relevant file in `docs/` and add a short chronological entry to `docs/development-log.md`.
- Every meaningful architecture or workflow change must also update this `AGENTS.md`, especially **Current implementation state** and **Next step**.
- Do not log prompts, source code, model outputs, bearer tokens or API-key secrets.
- Do not make PostgreSQL part of the inference routing hot path. Persistent configuration is loaded/published into in-memory runtime state.
- Preserve SSE streaming end-to-end and propagate cancellation/disconnects.
- Failover is allowed only before response bytes/tokens have been exposed to the client.
- Client-facing model names are logical aliases; never require clients to know DGX node names or provider model identifiers.

## Repository shape

```text
src/
  LlmProxy.Api/             ASP.NET Core host, OpenAI endpoints, admin endpoints
  LlmProxy.Application/     routing/use-case abstractions and in-memory control state
  LlmProxy.Domain/          domain entities and policies
  LlmProxy.Infrastructure/  EF Core, vLLM integration, telemetry, security
  LlmProxy.Admin/           React/TypeScript admin UI

tests/
  backend/LlmProxy.UnitTests/
  backend/integration/
  frontend/unit/
  frontend/e2e/

docker/
docs/
.github/workflows/
```

## Current implementation state

Last reviewed: **2026-09-09**.

Validated before the current hardware-telemetry increment:

- .NET 10 solution and React/TypeScript admin application.
- PostgreSQL persistence and EF Core migrations.
- OpenAI-compatible `GET /v1/models`.
- OpenAI-compatible `POST /v1/chat/completions` with streaming/non-streaming proxying.
- `POST /v1/responses` support used by compatible clients.
- Static bearer/API-key inference authentication with hashed secrets at rest.
- Entra ID admin authentication/authorization model (`LlmProxy.Admin`, `LlmProxy.Reader`).
- Node, model and deployment administration.
- Health monitoring with hysteresis, drain/disable behavior and connection probes.
- Multi-DGX routing strategies: `WeightedLeastLoaded`, `RoundRobin`, `WeightedRoundRobin`.
- Per-node/per-deployment weights and concurrency limits.
- Pre-response failover.
- Request telemetry including duration, upstream header latency, TTFT, token usage, attempts/failover and status.
- Administrative audit trail.
- vLLM Prometheus runtime collector from `<node service root>/metrics`.
- In-memory vLLM runtime snapshots: running requests, waiting requests, KV-cache usage, prompt/generated counters and model label.
- Per-deployment performance EWMA: TTFT, duration and infrastructure-failure feedback.
- Performance-aware `WeightedLeastLoaded` routing.
- Persisted Smart Routing Tuning policy with live in-memory publication and no restart required.
- React Routing view showing performance feedback, live vLLM capacity and editable tuning coefficients.
- Docker/PostgreSQL integration smoke suite with two path-prefixed fake vLLM runtimes.
- Backend unit tests, frontend Vitest tests, Playwright E2E tests and Docker integration tests in CI.
- Container publication to GHCR after successful CI.

Current increment in development:

- optional `HardwareMetricsBaseAddress` on each inference node;
- PostgreSQL migration for that optional hardware service root;
- NVIDIA DCGM Prometheus parser;
- in-memory `INodeHardwareMetricsTracker` / `NodeHardwareMetricsTracker`;
- background `NodeHardwareMetricsCollector`;
- `GET /api/admin/hardware` for current snapshots;
- `PUT /api/admin/nodes/{id}/hardware-metrics` to configure/clear the hardware root with audit;
- Docker/bootstrap settings for hardware collection;
- backend unit coverage for address normalization, DCGM parsing and snapshot failure behavior;
- focused documentation in `docs/hardware-telemetry.md`.

Do not call this current increment complete until CI is green and frontend/integration coverage has been added.

The smart-routing tuning policy currently controls:

```text
WarmupSamples
TtftTargetMilliseconds
TtftPenaltyWeight
FailurePenaltyWeight
ExternalLoadPenaltyWeight
QueuePenaltyWeight
KvCacheThreshold
KvCachePenaltyWeight
DegradedNodePenalty
UnknownNodePenalty
```

Defaults are conservative starting values, not capacity guarantees. They must be calibrated with real DGX/vLLM benchmark data.

## Routing hot path

`WeightedLeastLoaded` evaluates eligible deployments using:

```text
configured node/deployment weight and concurrency
+ gateway active request count
+ node health state
+ deployment EWMA TTFT/failure feedback
+ vLLM running requests
+ vLLM waiting queue
+ vLLM KV-cache pressure
= routing score
```

The routing selector uses in-memory trackers/states. Database reads do not happen per inference request.

vLLM telemetry failure is deliberately non-fatal. The router falls back to gateway load, health and recent request performance if `/metrics` cannot be read.

## Hardware telemetry boundary

Hardware telemetry is separate from vLLM runtime telemetry and node health.

```text
InferenceNode.BaseAddress
    -> vLLM health, /v1/* and vLLM /metrics

InferenceNode.HardwareMetricsBaseAddress (optional)
    -> NVIDIA/DCGM /metrics
```

The first DCGM parser recognizes GPU utilization, framebuffer used/free/total, GPU temperature and power usage. Hardware collection failures only affect the hardware snapshot; they do not modify `InferenceNode.Status` and do not block inference.

Hardware telemetry is initially observational. Do **not** add GPU utilization, temperature or power directly to `WeightedLeastLoaded` until representative DGX Spark benchmarks show that doing so improves latency/throughput. vLLM queue and KV-cache pressure remain the primary runtime scheduling signals.

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
GET  /api/admin/nodes
POST /api/admin/nodes/{id}/test-connection
PUT  /api/admin/nodes/{id}/hardware-metrics
GET  /api/admin/metrics
GET  /api/admin/metrics/summary
GET  /api/admin/audit
```

OpenAI-compatible inference endpoints are protected by inference credentials rather than interactive Entra authentication.

## CI/CD expectations

CI must continue to cover:

1. .NET restore/build/unit tests.
2. React production build and Vitest.
3. Playwright Chromium E2E.
4. Production Docker image build.
5. Docker Compose integration smoke suite with PostgreSQL and fake inference nodes.

CI uses concurrency cancellation so obsolete runs for the same branch/PR are stopped when a newer commit supersedes them.

Container publication must happen only after successful CI. Production deployment is expected to use an on-prem self-hosted GitHub Actions runner so the VM initiates outbound HTTPS connections instead of accepting inbound SSH from GitHub-hosted runners.

## Documentation discipline

When implementing a feature:

1. Update the most relevant focused document (`routing.md`, `operations.md`, `security.md`, `deployment.md`, `testing.md`, etc.).
2. Add a dated entry to `docs/development-log.md` describing what changed, why, and how it was validated.
3. Update `docs/roadmap.md` if milestone status changed.
4. Update this file if the architecture, implementation state, constraints, or next step changed.
5. Do not mark an increment as complete until CI/integration evidence is available.

## Current next step

1. Commit the DGX/DCGM backend hardware-telemetry increment and make backend CI green.
2. Add Docker integration coverage with a fake DCGM Prometheus endpoint, including a path-prefixed hardware service root.
3. Add React/TypeScript hardware visibility and node hardware-endpoint configuration, plus Vitest/Playwright coverage.
4. Update this file and `docs/development-log.md` from “in development” to validated only after the full quality gate passes.
5. Then move to the real GitHub Copilot BYOK spike and benchmark harness.

After hardware telemetry the next major work items are:

- real GitHub Copilot BYOK spike against this gateway;
- benchmark harness for model/concurrency profiles;
- calibration of smart-routing coefficients using measured data;
- OpenTelemetry/Prometheus gateway export;
- rate limits/quotas and production hardening.

## Things not to do without an explicit design change

- Do not SSH from the gateway into DGX nodes to manage model processes.
- Do not store raw API keys.
- Do not expose provider model names as the stable public contract.
- Do not retry after partial SSE/body output has reached the client.
- Do not identify Copilot users by source IP.
- Do not assume 200 licensed developers means 200 concurrent inference requests; benchmark real concurrency.
- Do not make GPU telemetry a hard availability dependency.
- Do not silently change routing coefficients without persisting/auditing them.

## Handover checklist

Before ending a development session, ensure the repository tells the next agent:

- what was changed;
- what is already green in CI;
- what remains unverified;
- current architectural decisions and constraints;
- exact next technical step;
- any external setup still required (DGX, Entra, Cloudflare, GitHub Copilot tenant, self-hosted runner).
