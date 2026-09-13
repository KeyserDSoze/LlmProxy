# Project status / handover snapshot

Last reviewed: **2026-09-13**.

This is the canonical current-state snapshot for LlmProxy. It is intentionally written so a new maintainer, chat or LLM can resume the project without relying on previous conversation history.

For working rules and the required reading order, start with root `AGENTS.md`.

## Current validated baseline

Latest validated application/benchmark baseline:

```text
49e7932f14118be403eec042a1393946143776ae
feat: add capacity benchmark harness
```

The complete quality gate passed for that baseline:

- .NET 10 solution build with warnings treated as errors;
- backend xUnit tests;
- benchmark-harness xUnit tests;
- React production build;
- Vitest;
- Playwright Chromium E2E;
- production Docker image build;
- Docker/PostgreSQL inference smoke suite;
- dedicated DCGM hardware telemetry smoke suite.

The current documentation may live on a later documentation-only commit. When validating product behavior, use the latest code commit with successful CI rather than assuming a documentation commit changed runtime behavior.

## What is implemented and validated

### Gateway and OpenAI compatibility

- .NET 10 / ASP.NET Core gateway.
- Logical public model aliases hide provider model names and DGX topology.
- `GET /v1/models`.
- `POST /v1/chat/completions`, streaming and non-streaming.
- `POST /v1/responses` compatibility path.
- Arbitrary compatible OpenAI payload fields are preserved while model IDs are rewritten internally.
- SSE is forwarded incrementally.
- Client cancellation/disconnect propagates upstream.
- Failover is allowed only before response bytes have been exposed downstream.

### Security and administration

- Static bearer/API-key inference authentication suitable for OpenAI-compatible/BYOK clients.
- Raw API secrets are not persisted; HMAC-hashed secrets are stored in PostgreSQL.
- Entra ID administration plumbing with `LlmProxy.Admin` and `LlmProxy.Reader` roles.
- React administration UI.
- Administrative audit trail.
- Node, model, deployment, routing and credential administration.

### Multi-DGX routing and health

- DGX/vLLM service roots support arbitrary HTTP(S) host/port/path prefixes.
- Health probes with hysteresis and persisted diagnostics.
- `Healthy`, `Degraded`, `Unhealthy`, `Draining`, `Disabled` operational behavior.
- Routing strategies: `WeightedLeastLoaded`, `RoundRobin`, `WeightedRoundRobin`.
- Node and deployment weights.
- Configured node/deployment concurrency limits.
- Pre-response failover.
- Persisted and audited smart-routing tuning applied live without restart.

### Inference observability and smart routing

- Per-request duration/status/attempt/failover telemetry.
- Upstream-header latency and TTFT.
- Token usage when reported by upstream.
- vLLM Prometheus collector for running requests, waiting requests, KV-cache utilization and token counters.
- Per-deployment EWMA TTFT/duration/infrastructure-failure feedback.
- Performance-aware weighted-least-loaded scoring.
- React routing/runtime/metrics visibility.

### DGX hardware telemetry

- Optional `HardwareMetricsBaseAddress` independent from vLLM service root.
- NVIDIA DCGM Prometheus parsing for GPU utilization, framebuffer used/free/total, temperature and power.
- Multi-GPU aggregation.
- Hardware failures do not change inference health.
- Last successful hardware values are retained for diagnostics after transient failure.
- React **DGX Hardware** view and endpoint configuration.
- Dedicated Docker smoke test verifies path prefixes, aggregation, 503 isolation, audit and clear behavior.

### Benchmarking

A .NET 10 benchmark harness exists under `tests/performance/` and is validated in CI.

It supports:

- target through LlmProxy or directly against vLLM;
- path-prefixed service roots;
- Chat Completions and Responses;
- streaming and non-streaming;
- warm-up and configurable concurrency sweeps;
- success/error rate and requests/sec;
- p50/p95/p99 TTFT and total duration;
- input/output/total tokens and output tokens/sec when usage is available;
- JSON and CSV reports;
- bearer secrets read by environment-variable name, not raw CLI arguments;
- prompt text excluded from reports;
- streaming TTFT measured from first meaningful output delta, not headers or role-only metadata.

Real load is deliberately **not** run in standard CI.

See `docs/benchmarking.md`.

## What is NOT implemented yet

The next product increment is **Capacity Profile + node-wide capacity enforcement/backpressure**.

### 1. Capacity Profile

We want benchmark evidence to be persistable against a deployment without automatically changing live capacity.

Conceptually:

```text
Deployment
  active MaxConcurrency = 4

Measured Capacity Profile
  recommended max concurrency = 8
  p95 TTFT                  = ...
  p95 duration              = ...
  sustainable output tok/s  = ...
  benchmark provenance      = ...
  measured at               = ...
```

Important rule:

```text
recommended capacity != active production limit
```

Applying a recommendation must be an explicit administrator operation and must be audited.

No Capacity Profile persistence/API/UI exists yet at the time of this snapshot.

### 2. Aggregate physical-node concurrency

Current deployment routing respects the selected deployment's effective concurrency ceiling, but physical DGX capacity must also be enforced across all deployments sharing the same node.

Example problem to solve:

```text
DGX-01 physical max = 8

agic-code      deployment max = 8
agic-reasoning deployment max = 8

INVALID outcome: 16 simultaneous requests on DGX-01
```

The request-load tracker/routing eligibility must gain a node-wide aggregate ceiling so the physical node cannot be overcommitted merely because multiple deployments exist.

### 3. Backpressure behavior

When every otherwise eligible deployment is saturated, the gateway needs a deliberate OpenAI-compatible backpressure/error behavior rather than accidental failure. Status/error shape and retry guidance must be defined, tested and documented.

### 4. Admin and tests

The capacity-control increment should include:

- domain/persistence model;
- EF migration;
- admin API;
- audit events;
- React visibility/action;
- unit tests;
- integration coverage proving aggregate node capacity and persistence;
- documentation updates.

Do not mark it validated until the complete CI/integration gate is green.

## External validation still required

These items cannot be fully proven from the current repository/CI environment:

- real DGX Spark hardware with production-intended model(s) and vLLM configuration;
- direct-vLLM vs gateway benchmark sweeps on real DGX;
- representative multi-DGX concurrent load;
- real GitHub Copilot custom/BYOK provider configuration through the public endpoint;
- real Entra application registration and production role assignments;
- Cloudflare Tunnel/public production domain;
- on-prem self-hosted GitHub Actions deployment runner;
- GitHub Copilot usage-metrics/custom-model reporting behavior if used for per-user adoption analytics.

## Important architecture decisions

### Keep our LlmProxy + vLLM architecture

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction. Continue with the custom LlmProxy/vLLM architecture unless the project owner explicitly reopens that decision.

### Hardware telemetry stays observational

Do not add GPU utilization/temperature/power directly into routing simply because they are available. vLLM queue/KV-cache/application latency signals are currently the scheduling signals. Hardware-aware routing requires benchmark evidence.

### PostgreSQL stays out of the inference hot path

Persist configuration/history in PostgreSQL, but routing decisions should use in-memory runtime/configuration state rather than querying the database per inference request.

### Copilot user identity is not guaranteed at the gateway

Central GitHub Copilot BYOK may present a shared provider credential rather than a unique user identity per request. Do not infer identity from source IP. If per-user adoption analytics are needed, ingest GitHub Copilot usage metrics and combine them analytically with gateway telemetry unless a supported per-user provider identity mechanism is proven.

## Exact resume point

A new development session should proceed in this order:

1. Read root `AGENTS.md` and this file.
2. Inspect the latest `main` commit and latest CI result.
3. Implement persisted **Capacity Profile** metadata independently from active `MaxConcurrency`.
4. Add explicit audited “apply recommended capacity” behavior.
5. Extend request-load tracking/routing eligibility with aggregate **node-wide concurrency** across all deployments on the same DGX.
6. Define OpenAI-compatible saturation/backpressure semantics.
7. Add backend tests, migration/integration coverage and React admin support.
8. Run the complete quality gate.
9. Only then update this file, `AGENTS.md`, `docs/development-log.md` and `docs/roadmap.md` from ACTIVE/PLANNED to VALIDATED/DONE.
10. When real DGX access becomes available, run matched direct-vLLM and gateway benchmark profiles and use the evidence to set real capacity values and tune routing.
11. Run the GitHub Copilot BYOK spike in parallel when tenant/public-endpoint access is available.

## Work after the current capacity-control increment

Planned product work includes:

- rate limits/quotas by credential/model;
- OpenTelemetry and/or Prometheus gateway export;
- backup/restore runbook and automated restore verification;
- credential rotation workflow;
- model/runtime upgrade/draining strategy;
- production control-plane HA if required;
- GitHub Copilot usage-metrics ingestion/user analytics if desired;
- real benchmark-based smart-routing calibration.

## Documentation map

Use these files for specific questions:

| Question | Source |
| --- | --- |
| Where are we now / what is missing / where do I resume? | `docs/project-status.md` |
| What rules must an agent follow? | `AGENTS.md` |
| What happened over time and how was it validated? | `docs/development-log.md` |
| What milestones/backlog remain? | `docs/roadmap.md` |
| How does benchmarking work? | `docs/benchmarking.md` |
| How does DGX/DCGM hardware telemetry work? | `docs/hardware-telemetry.md` |
| How should tests/quality gates work? | `docs/testing.md` |
| What is the broad product overview? | `README.md` |
| How does GitHub Copilot integration work/what remains external? | `docs/github-copilot.md` |
| Deployment/operations/security details | `docs/deployment.md`, `docs/operations.md`, `docs/security.md` |

## Handover maintenance rule

Every meaningful increment must leave this document accurate. At minimum update:

- latest validated commit/CI evidence;
- newly completed capability;
- anything that is still unverified;
- exact next technical step;
- external dependencies/blockers;
- links to any new focused documentation.

If this file says an already-completed item is still the next step, the handover is considered stale and must be fixed before ending the development session.
