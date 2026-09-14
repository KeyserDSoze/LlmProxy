# Development log

This file is the chronological engineering trace for LlmProxy. For the current snapshot and exact resume point, use `docs/project-status.md`.

## 2026-09-09 - Repository and gateway foundation

Implemented the .NET 10 solution, React/TypeScript admin application, PostgreSQL persistence, Docker packaging and GitHub Actions workflows. The gateway was structured around logical client-facing models, internal DGX nodes and model deployments so physical model identifiers remain hidden from clients.

Implemented the initial OpenAI-compatible surface with `/v1/models`, `/v1/chat/completions`, SSE streaming and later `/v1/responses`. Added inference bearer credentials, hashed secrets, Entra ID administration, node/model/deployment management, health probes, drain/disable states and administrative audit events.

## 2026-09-09 - Multi-DGX routing and failover

Added `WeightedLeastLoaded`, `RoundRobin` and `WeightedRoundRobin`, node/deployment weights, concurrency ceilings and pre-response failover. Routing excludes unavailable, draining, disabled and saturated candidates. Streaming responses are never retried after bytes have been exposed to the caller.

Added path-safe service-root handling for host/IP/port/path-prefixed inference roots.

## 2026-09-09 - Health, audit and inference observability

Added health-monitor hysteresis, last-health diagnostics and connection tests. Added request metrics for status, duration, attempts, upstream-header latency, TTFT, token counts and streaming/failover visibility. Prompt and generated content are deliberately excluded from telemetry.

## 2026-09-09 - Performance-aware routing and vLLM telemetry

Added per-deployment EWMA TTFT/duration/infrastructure-failure feedback. Added a vLLM Prometheus collector for running/waiting requests, KV-cache utilization, token counters and model labels.

`WeightedLeastLoaded` combines configured capacity/weights, gateway active work, health, EWMA feedback and vLLM runtime pressure. PostgreSQL is not queried per inference request.

## 2026-09-09 - Persisted smart-routing tuning

Moved routing coefficients into a persisted/audited `RoutingTuningPolicy` and thread-safe in-memory state. Changes apply live without restart.

Full backend/frontend/Docker integration passed for the tuning increment; container publication succeeded.

## 2026-09-09 - Documentation/handover discipline

Introduced root `AGENTS.md` and established repository-first handover rules. Meaningful increments must update focused docs, project status, development log, roadmap where relevant and the agent resume point.

CI cancels superseded runs for the same branch/PR.

## 2026-09-10 - DGX/DCGM hardware telemetry - VALIDATED

Added optional per-node NVIDIA/DCGM hardware telemetry on a service root separate from vLLM. Collector parses GPU utilization, framebuffer memory, temperature and power into an in-memory multi-GPU snapshot.

Transient DCGM failures do not change inference health. Explicit endpoint clear removes the runtime snapshot. React DGX Hardware and dedicated fake-DCGM Docker smoke were added.

Full quality gate passed on commit:

```text
6c238a095273843e713a72fb2e26b2c7c434fc62
```

## 2026-09-10 - Architecture decision: no NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. Project owner chose to continue with the custom LlmProxy + vLLM architecture.

## 2026-09-10 - Benchmark harness - VALIDATED

Added `tests/performance/`, a .NET 10 benchmark harness that can target LlmProxy or direct vLLM, including path-prefixed service roots, Chat Completions/Responses and stream/non-stream runs.

Measurements include concurrency sweep, success/error rate, p50/p95/p99 TTFT/duration, requests/sec and token throughput when available. Credentials are read from environment variables and prompt bodies are excluded from reports.

Full quality gate passed on commit:

```text
49e7932f14118be403eec042a1393946143776ae
```

## 2026-09-13 - Canonical handover snapshot

Added `docs/project-status.md` as the canonical “where are we / what is missing / where do I resume?” document and linked it from `AGENTS.md` with explicit source-of-truth precedence.

## 2026-09-13 - Capacity Profile + node-wide admission/backpressure - VALIDATED

Implemented persisted benchmark-derived Capacity Profiles while keeping recommendations independent from live production limits. Added explicit audited Save / Apply / Clear workflows and React capacity administration.

Extended request-load tracking to atomically acquire both deployment-level and physical-node slots. Multiple deployments on the same DGX can no longer overcommit the physical node simply because each deployment has its own limit.

Defined deliberate saturation behavior:

```text
429 Too Many Requests
Retry-After: 1
error.code = capacity_exhausted
```

This remains distinct from `503 no_healthy_deployment`.

A dedicated Docker smoke test starts a real SSE request that holds the only slot, verifies a concurrent request receives `429 capacity_exhausted`, then verifies a new request succeeds once the stream releases the lease. Capacity Profile persistence/restart behavior is also covered.

Full backend, benchmark, React/Vitest, Playwright, Docker/PostgreSQL, DCGM and capacity smoke quality gate passed on commit:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

## 2026-09-14 - Operator quickstart and private GHCR deployment path

Added a repository-native onboarding path for installing/testing LlmProxy without compiling the source:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

Added `docker/docker-compose.quickstart.yml` and `docker/.env.quickstart.example`. The guide covers Ubuntu Docker/Compose, Windows Docker Desktop + WSL 2, private GHCR authentication/pull, PostgreSQL/env setup, real vLLM or mock runtime, smoke tests and optional Entra setup.

CI was extended to validate that the quickstart Compose file can be rendered with required secrets supplied through environment variables.

## 2026-09-14 - Usage governance product scope

Clarified that LlmProxy is not only an inference router. The product owner confirmed four core responsibilities:

```text
1. inference authentication
2. rate limiting / quotas
3. consolidated usage accounting
4. configurable usage groups queryable through API and React UI
```

Added `docs/usage-governance.md` as the focused contract for this area.

V1 group accounting is intentionally unambiguous:

```text
UsageGroup 1 --- N ApiCredential
ApiCredential -> zero or one primary UsageGroup
```

The planned implementation snapshots `UsageGroupId` into request metrics so historical usage remains assigned to the group that owned the credential at request time even if the credential is moved later.

The target report dimensions include time window, group, credential, logical model, node/deployment and surface; metrics include request/success/error counts, rate-limit/capacity rejects, token totals, duration and TTFT percentiles.

Important identity rule: a shared GitHub Copilot provider credential can be attributed to its gateway group, but it does not expose individual GitHub users. Per-user/adoption analytics require GitHub Copilot usage metrics or another supported identity signal; source IP must not be used as identity.

## Next increment

Implement credential/model request rate limiting first:

```text
credential/model policy exceeded -> 429 rate_limit_exceeded
physical DGX admission exhausted -> 429 capacity_exhausted
no operational backend -> 503 no_healthy_deployment
```

Use persisted policy with in-memory enforcement, computed `Retry-After`, Admin API/UI, audit, metrics and concurrent integration coverage. Once validated, implement `UsageGroup` persistence, credential assignment, request-metric group snapshot and grouped usage APIs/UI.

Token/budget quotas can follow separately because final output token usage is generally known only after inference completes.

Real DGX benchmark sweeps, Entra production registration, Cloudflare/public endpoint and the real GitHub Copilot BYOK spike remain external validation activities.
