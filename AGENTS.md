# AGENTS.md

This file is the mandatory entry point and working-context document for humans and AI coding agents working on **LlmProxy**. Read it before changing code. It explains the engineering rules, where authoritative project state lives, and the exact resume point.

## Mandatory resume protocol

A new chat, coding agent, maintainer or LLM must **not** reconstruct project state from conversation history. The repository is the handover mechanism.

Read these sources in this order before implementing anything:

1. **`AGENTS.md`** — engineering rules, architecture constraints, handover protocol and current next step.
2. **`docs/project-status.md`** — canonical current snapshot: what is validated, what is unfinished, external dependencies and the exact resume point.
3. **`docs/development-log.md`** — chronological record of meaningful increments and the CI/integration evidence that validated them.
4. **`docs/roadmap.md`** — milestone/backlog view of DONE, ACTIVE, PLANNED and EXTERNAL work.
5. The focused document relevant to the task, for example `docs/benchmarking.md`, `docs/hardware-telemetry.md`, `docs/routing.md`, `docs/security.md`, `docs/testing.md`, `docs/deployment.md` or `docs/github-copilot.md`.
6. Inspect the latest `main` commit and its GitHub Actions result before assuming an increment is complete.

### Source-of-truth precedence

If documents ever disagree, use this precedence and fix the stale document as part of the next change:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md current snapshot
    > docs/development-log.md chronological evidence
    > docs/roadmap.md future/status planning
    > focused design/operations documents
    > old chat context
```

`docs/project-status.md` is the single best file to give another LLM when the question is: **“Where are we, what have we done, what is missing, and where do I resume?”**

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
- Every meaningful feature must update the relevant focused document, `docs/project-status.md`, and `docs/development-log.md`.
- Every meaningful architecture/workflow change must also update this `AGENTS.md`, especially **Current implementation state** and **Resume here / next step**.
- Update `docs/roadmap.md` whenever milestone state or implementation order changes.
- Do not log prompts, source code, model outputs, bearer tokens or API-key secrets.
- Do not make PostgreSQL part of the inference routing hot path. Persistent configuration is loaded/published into in-memory runtime state.
- Preserve SSE streaming end-to-end and propagate cancellation/disconnects.
- Failover is allowed only before response bytes/tokens have been exposed to the client.
- Client-facing model names are logical aliases; never require clients to know DGX node names or provider model identifiers.
- Capacity claims must come from benchmark evidence, not developer/license count.
- Never mark work DONE only because code was committed: require the appropriate CI/integration evidence.

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

Last reviewed: **2026-09-13**.

The latest validated application/benchmark baseline is commit `49e7932f14118be403eec042a1393946143776ae`. Its complete quality gate passed: .NET build/unit tests, benchmark-harness tests, React build/Vitest/Playwright, production Docker build, PostgreSQL/inference integration smoke and DCGM hardware smoke.

Validated capabilities include:

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
- per-node/per-deployment weights and configured concurrency limits.
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
- dedicated fake-DCGM Docker integration smoke test proving health isolation, path prefixes, aggregation, audit and clear behavior.
- .NET 10 benchmark harness under `tests/performance/` for direct-vLLM vs gateway capacity comparison.
- benchmark Chat Completions/Responses, streaming/non-streaming, warm-up, concurrency sweeps, p50/p95/p99 TTFT/duration, req/s, token throughput, JSON/CSV reports and safe env-var credential handling.
- backend xUnit, benchmark xUnit, frontend Vitest, Playwright and Docker/PostgreSQL integration tests in CI.
- container publication to GHCR after successful CI.

For the concise current-state view and outstanding work, prefer `docs/project-status.md` over duplicating assumptions here.

## Current development focus — not implemented yet

The next product increment is **capacity profile + node-wide capacity enforcement/backpressure**.

The important distinction is:

```text
benchmark evidence / recommended capacity
    !=
currently active production concurrency limit
```

The intended direction is to persist benchmark-derived capacity metadata for a deployment (for example recommended max concurrency, baseline TTFT/throughput and benchmark provenance) without silently changing runtime behavior. Applying a recommendation must be explicit and audited.

A second issue must be fixed at the same time: node capacity must be enforceable across all deployments sharing one physical DGX. If one node has physical capacity 8, two deployments configured at 8 each must not accidentally allow 16 concurrent requests on that node.

Do not claim this increment exists until domain/persistence/API/UI/tests/integration are committed and green.

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
2. benchmark-harness unit tests.
3. React production build + Vitest.
4. Playwright Chromium E2E.
5. production Docker image build.
6. Docker/PostgreSQL inference smoke suite.
7. dedicated DCGM hardware smoke suite.

CI uses concurrency cancellation so obsolete runs on the same branch/PR are stopped. Integration scripts are invoked via `bash` rather than relying on executable mode.

Performance load itself is never an automatic CI action. Real benchmark sweeps must be deliberate/manual and target an explicitly configured environment.

## CI/CD expectations

Container publication must happen only after successful CI for the exact commit. Production deployment is expected to use an on-prem self-hosted GitHub Actions runner so the VM initiates outbound HTTPS connections instead of accepting inbound SSH from GitHub-hosted runners.

## Documentation discipline

For every meaningful increment:

1. update the most relevant focused document;
2. update `docs/project-status.md` with validated baseline, open work and exact resume point;
3. append a dated entry to `docs/development-log.md` with what/why/validation;
4. update `docs/roadmap.md` when status/order changes;
5. update this file's current state and resume point when architecture/constraints/next work change;
6. do not label an increment DONE/validated until actual CI/integration evidence exists.

A development session is not considered cleanly handed over if `docs/project-status.md` still describes an old next step.

## External validation still required

- real Entra application registration and production role assignments;
- Cloudflare/public domain on the target VM;
- real GitHub Copilot custom/BYOK provider flow;
- real DGX Spark/vLLM model runtime and benchmark sweeps;
- representative multi-DGX concurrent load;
- self-hosted deployment runner;
- validation of GitHub Copilot usage-metrics ingestion/custom-model reporting if used for per-user adoption analytics.

Copilot central BYOK may identify only a shared provider credential at the gateway. Do not infer user identity from source IP. Individual GitHub-user adoption/usage should come from GitHub Copilot usage metrics and be analytically combined with gateway infrastructure telemetry unless a supported per-user provider identity mechanism is proven.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router (PAIR) was evaluated on 2026-09-10. The project owner explicitly chose to **continue with our own LlmProxy/vLLM architecture**. Do not redirect implementation toward PAIR unless this decision is explicitly reopened.

## Resume here / next step

Before coding, read `docs/project-status.md`.

Current sequence:

1. Implement persisted **Capacity Profile** metadata separately from the active concurrency limit.
2. Add explicit, audited application of a recommended capacity to a deployment rather than automatic mutation from benchmark results.
3. Make request-load accounting enforce a **node-wide aggregate concurrency ceiling** across deployments sharing the same DGX.
4. Define and implement OpenAI-compatible backpressure behavior when all eligible deployment/node capacity is exhausted.
5. Add backend/unit/integration coverage and React administration visibility/actions.
6. Only after the complete quality gate is green, mark this capacity-control increment validated in `docs/project-status.md`, `docs/development-log.md`, `docs/roadmap.md` and this file.
7. When real DGX access exists, run direct-vLLM and gateway benchmark sweeps and use measured profiles to calibrate concurrency and smart routing.
8. In parallel, run the real GitHub Copilot BYOK spike when tenant/public-endpoint access is available.

After capacity control/profiling, priority product work is rate limits/quotas, OpenTelemetry/Prometheus gateway export, backup/restore/credential rotation and production HA/hardening.

## Things not to do without an explicit design change

- Do not SSH from the gateway into DGX nodes to manage model processes.
- Do not store raw API keys.
- Do not expose provider model names as the stable public contract.
- Do not retry after partial SSE/body output has reached the client.
- Do not identify Copilot users by source IP.
- Do not assume 200 licensed developers means 200 concurrent inference requests.
- Do not make GPU/DCGM telemetry a hard availability dependency.
- Do not silently change routing coefficients or capacity limits without persistence/audit.
- Do not place production load targets or raw bearer secrets in CI/source control.

## Handover checklist

Before ending a development session, ensure the repository — especially `docs/project-status.md` — states:

- what changed;
- what is actually green in CI and the relevant commit SHA;
- what remains unverified or not implemented;
- current architecture decisions/constraints;
- exact next technical step;
- external setup still required;
- which focused docs were changed.
