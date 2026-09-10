# AGENTS.md

This file is the primary handover and working-context document for humans and AI coding agents working on **LlmProxy**. Read it before changing code. Update it whenever an increment changes architecture, behavior, operations, tests, deployment, or the next development step.

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway. The initial target is about 200 developers using GitHub Copilot while inference is served by one NVIDIA DGX Spark and can scale to six DGX Spark nodes. Clients see stable logical model names while hardware, vLLM model identifiers and deployment topology stay hidden behind the gateway.

```text
GitHub Copilot / OpenAI-compatible client
    -> public HTTPS endpoint
    -> Cloudflare Tunnel
    -> LlmProxy (.NET 10)
    -> auth / logical model / smart routing / telemetry
    -> vLLM on DGX Spark 1..N
```

## Non-negotiable engineering conventions

- Backend: **.NET 10 / ASP.NET Core**, C#.
- Frontend: **React + TypeScript**.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker containers; GitHub Actions builds/tests/publishes images.
- Repository work currently happens directly on `main` unless branch protection or the project owner says otherwise.
- Keep product code under `src/`, tests/test tooling under `tests/`, operational containers under `docker/`, documentation under `docs/`.
- Every meaningful feature must update the relevant file in `docs/` and append `docs/development-log.md`.
- Every meaningful architecture/workflow change must update this `AGENTS.md`, especially **Current implementation state** and **Resume here / next step**.
- Do not log prompts, source code, model outputs, bearer tokens or API-key secrets.
- Do not make PostgreSQL part of the inference routing hot path. Persistent configuration is loaded/published into in-memory runtime state.
- Preserve SSE streaming end-to-end and propagate cancellation/disconnects.
- Failover is allowed only before response bytes/tokens have been exposed to the client.
- Client-facing model names are logical aliases; never require clients to know DGX node names or provider model identifiers.
- Capacity claims must come from benchmark evidence, not developer/license count.

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
  performance/              benchmark/load tooling + its tests

docker/
docs/
.github/workflows/
```

## Current implementation state

Last reviewed: **2026-09-10**.

Validated through commit `6c238a095273843e713a72fb2e26b2c7c434fc62`:

- .NET 10 solution and React/TypeScript admin application.
- PostgreSQL persistence and EF Core migrations.
- OpenAI-compatible `GET /v1/models`.
- `POST /v1/chat/completions`, streaming/non-streaming.
- `POST /v1/responses` compatibility path.
- arbitrary compatible payload preservation while logical model IDs are rewritten internally.
- SSE incremental forwarding, cancellation propagation and no failover after downstream bytes start.
- static bearer/API-key inference authentication with HMAC-hashed secrets at rest.
- Entra ID administration model with `LlmProxy.Admin` / `LlmProxy.Reader` roles.
- node/model/deployment administration and full path-prefixed HTTP(S) service roots.
- health hysteresis, drain/disable behavior and connection probes.
- `WeightedLeastLoaded`, `RoundRobin`, `WeightedRoundRobin`.
- per-node/per-deployment weights and concurrency limits.
- pre-response failover.
- request telemetry: duration, upstream-header latency, TTFT, token usage, attempts/failover/status.
- administrative audit trail.
- vLLM Prometheus runtime collector: running, waiting, KV-cache, token counters, model label.
- per-deployment performance EWMA: TTFT, duration and infrastructure-failure feedback.
- performance-aware `WeightedLeastLoaded` routing.
- persisted Smart Routing Tuning policy with live publication/no restart.
- React Routing view with performance, vLLM capacity and editable tuning.
- optional per-node NVIDIA/DCGM `HardwareMetricsBaseAddress`.
- DCGM Prometheus collection for GPU utilization, framebuffer memory, temperature and power.
- React **DGX Hardware** view and live endpoint configuration.
- transient DCGM failure preserves last diagnostic values but does not affect inference health.
- explicit DCGM endpoint clear removes the in-memory hardware snapshot.
- dedicated fake-DCGM Docker integration smoke test proving health isolation, path prefixes, aggregation, audit and clear behavior.
- backend xUnit, frontend Vitest, Playwright and Docker/PostgreSQL integration tests.
- container publication to GHCR after successful CI.

### Current increment in development: benchmark harness

A .NET 10 console tool is being added under:

```text
tests/performance/LlmProxy.Benchmark/
tests/performance/LlmProxy.Benchmark.Tests/
```

It must support:

- explicit gateway or direct-vLLM service root target;
- path-prefixed roots and roots already ending in `/v1`;
- logical model (gateway) or provider model (direct vLLM);
- Chat Completions and Responses;
- streaming and non-streaming;
- warm-up and configurable concurrency sweep;
- per-level success/error, req/s, p50/p95/p99 TTFT and duration;
- token totals/output tokens per second when usage exists;
- JSON + CSV output;
- bearer token only via environment variable name (`--api-key-env`), never raw CLI secret;
- prompt body excluded from reports;
- first streaming TTFT based on meaningful output delta, not response headers/role-only metadata.

CI must compile/test the harness but must **not** run a real load sweep.

Do not mark this increment complete until solution build + benchmark tests + full existing CI quality gate are green.

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

The selector uses in-memory trackers/states. Database reads do not happen per inference request.

The persisted smart-routing tuning policy controls:

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

Defaults are starting values, not capacity guarantees. Calibrate them only with measured DGX/vLLM data.

## Hardware telemetry boundary

Hardware telemetry is separate from vLLM runtime telemetry and node health.

```text
InferenceNode.BaseAddress
    -> vLLM health, /v1/* and vLLM /metrics

InferenceNode.HardwareMetricsBaseAddress (optional)
    -> NVIDIA/DCGM /metrics
```

Hardware telemetry is observational. Do **not** add GPU utilization, temperature or power directly to routing until representative DGX Spark benchmarks show it improves latency/stability/throughput. High GPU utilization alone is not an error condition.

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

## Testing rules

Mock boundaries, not domain behavior. Keep all test code under `tests/`.

CI quality gate includes:

1. .NET restore/build/backend unit tests.
2. benchmark-harness unit tests (once current increment lands).
3. React production build + Vitest.
4. Playwright Chromium E2E.
5. production Docker image build.
6. Docker/PostgreSQL inference smoke suite.
7. dedicated DCGM hardware smoke suite.

CI uses concurrency cancellation so obsolete runs on the same branch/PR are stopped. Integration scripts are invoked via `bash` rather than relying on executable mode.

Performance load itself is never an automatic CI action. Any future real benchmark workflow must be deliberate/manual and target an explicitly configured environment.

## CI/CD expectations

Container publication must happen only after successful CI for the exact commit. Production deployment is expected to use an on-prem self-hosted GitHub Actions runner so the VM initiates outbound HTTPS connections instead of accepting inbound SSH from GitHub-hosted runners.

## Documentation discipline

For every meaningful increment:

1. update the most relevant focused document;
2. append a dated entry to `docs/development-log.md` with what/why/validation;
3. update `docs/roadmap.md` when status/order changes;
4. update this file's current state and resume point;
5. do not label an increment DONE/validated until actual CI/integration evidence exists.

## External validation still required

- real Entra application registration and production role assignments;
- Cloudflare/public domain on the target VM;
- real GitHub Copilot custom/BYOK provider flow;
- real DGX Spark/vLLM model runtime;
- representative multi-DGX concurrent load;
- self-hosted deployment runner.

Copilot central BYOK may identify only a shared provider credential at the gateway. Do not infer user identity from source IP. Individual GitHub-user adoption/usage should come from GitHub Copilot usage metrics and be analytically combined with gateway infrastructure telemetry unless a supported per-user provider identity mechanism is proven.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router (PAIR) was evaluated on 2026-09-10. The project owner explicitly chose to **continue with our own LlmProxy/vLLM architecture**. Do not redirect implementation toward PAIR unless this decision is explicitly reopened.

## Resume here / next step

1. Finish the benchmark harness currently being added under `tests/performance/`.
2. Run solution build and benchmark unit tests through CI; fix all warnings/errors because warnings are treated as errors.
3. Update this section from IN DEVELOPMENT to VALIDATED only when the complete CI gate is green.
4. Once real DGX access exists, run direct-vLLM and gateway sweeps with the same model/prompt profile and derive a model/deployment capacity profile.
5. Use measured profiles to set recommended max concurrency and calibrate smart-routing tuning.
6. In parallel, run the real GitHub Copilot BYOK spike when tenant/public-endpoint access is available.

After capacity profiling, priority product work is rate limits/quotas, OpenTelemetry/Prometheus gateway export, backup/restore/credential rotation and production HA/hardening.

## Things not to do without an explicit design change

- Do not SSH from the gateway into DGX nodes to manage model processes.
- Do not store raw API keys.
- Do not expose provider model names as the stable public contract.
- Do not retry after partial SSE/body output has reached the client.
- Do not identify Copilot users by source IP.
- Do not assume 200 licensed developers means 200 concurrent inference requests.
- Do not make GPU/DCGM telemetry a hard availability dependency.
- Do not silently change routing coefficients without persistence/audit.
- Do not place production load targets or raw bearer secrets in CI/source control.

## Handover checklist

Before ending a development session, ensure the repository states:

- what changed;
- what is actually green in CI;
- what remains unverified;
- current architecture decisions/constraints;
- exact next technical step;
- external setup still required.
