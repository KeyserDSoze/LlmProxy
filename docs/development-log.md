# Development log

This file is the chronological engineering trace for LlmProxy. Keep entries concise but specific enough that a new maintainer can understand what changed and how it was validated. For the current snapshot and exact resume point, use `docs/project-status.md`.

## 2026-09-09 - Repository and gateway foundation

Implemented the .NET 10 solution, React/TypeScript admin application, PostgreSQL persistence, Docker packaging and GitHub Actions workflows. The gateway was structured around logical client-facing models, internal DGX nodes and model deployments so physical model identifiers remain hidden from clients.

Implemented the initial OpenAI-compatible surface with `/v1/models`, `/v1/chat/completions`, SSE streaming and later `/v1/responses`. Added inference bearer credentials, hashed secrets, Entra ID administration, node/model/deployment management, health probes, drain/disable states and administrative audit events.

## 2026-09-09 - Multi-DGX routing and failover

Added routing strategies `WeightedLeastLoaded`, `RoundRobin` and `WeightedRoundRobin`, node/deployment weights, concurrency ceilings and pre-response failover. Routing excludes unavailable, draining, disabled and saturated candidates. Streaming responses are never retried after bytes have been exposed to the caller.

Added path-safe service-root handling so a DGX can be configured as `http://host:port`, `http://host:port/vllm` or another prefixed HTTP(S) root without losing the prefix when `/health`, `/metrics` or `/v1/*` paths are appended.

## 2026-09-09 - Health diagnostics, audit and inference observability

Added health-monitor hysteresis, last-health latency/error timestamps and explicit connection-test diagnostics. Added request metrics for status, duration, attempts, upstream-header latency, TTFT, token counts and streaming/failover visibility. Added summary endpoints and React observability views. Prompt and generated content are deliberately excluded from telemetry.

## 2026-09-09 - Performance-aware routing

Added an in-memory per-deployment performance tracker. Completed requests feed exponentially weighted moving averages for TTFT and duration plus an infrastructure-failure score. `WeightedLeastLoaded` uses these signals only after a warm-up sample threshold so one cold start cannot dominate routing decisions.

## 2026-09-09 - vLLM runtime telemetry

Added a background collector for the Prometheus exposition returned by `<DGX service root>/metrics`. The collector tracks vLLM running requests, waiting requests, KV-cache utilization, cumulative prompt/generated token counters and the reported model label. Collection failures are non-fatal and do not change node health.

The router compares vLLM running requests with the gateway's own active count. Excess running work is treated as external load, while queue depth and KV-cache pressure add routing penalties. The React Routing page exposes both vLLM runtime snapshots and per-deployment performance feedback.

Validated with backend tests plus the Docker/PostgreSQL smoke suite using two fake path-prefixed vLLM endpoints.

## 2026-09-09 - Persisted smart-routing tuning

Moved smart-routing coefficients out of hard-coded scoring logic into a singleton `RoutingTuningPolicy` persisted in PostgreSQL and published into a thread-safe in-memory `RoutingTuningState`.

The policy controls warm-up samples, TTFT target/weight, infrastructure failure weight, external-load weight, queue weight, KV-cache threshold/weight and degraded/unknown node penalties. Values are range-validated, changes are audited and are applied to new requests without a gateway restart. Persistence is verified across container restart.

Added:

```http
GET /api/admin/routing/tuning
PUT /api/admin/routing/tuning
```

The React Routing view exposes the tuning profile and a reset-to-default workflow. Backend unit tests, Vitest, Playwright and the Docker/PostgreSQL integration suite cover the feature. Commit `6f9557262ce25f1084214bf8f99768245d5fc80b` completed successfully in CI and the container publish workflow succeeded.

## 2026-09-09 - Documentation/handover discipline

Introduced root `AGENTS.md` as the primary working rules/handover entry point for AI agents and maintainers. From this point forward every meaningful technical increment must update focused documentation, append this development log, update roadmap status when relevant and keep the current resume point accurate.

CI is configured to cancel superseded runs for the same branch or pull request so rapid development on `main` does not waste runners testing obsolete commits.

## 2026-09-10 - DGX/DCGM hardware telemetry - VALIDATED

Completed optional NVIDIA/DCGM hardware observability while keeping hardware telemetry independent from vLLM runtime pressure and inference node health.

Each `InferenceNode` can persist a separate path-safe `HardwareMetricsBaseAddress`. A background collector parses DCGM Prometheus GPU utilization, framebuffer used/free/total, GPU temperature and power series into an in-memory multi-GPU snapshot. `GET /api/admin/hardware` exposes runtime state and `PUT /api/admin/nodes/{id}/hardware-metrics` configures/clears the service root with audit.

Transient DCGM failures mark only the hardware snapshot unavailable and retain the last successful numeric sample for diagnostics. Explicitly clearing the endpoint removes the in-memory hardware snapshot. Neither case modifies `InferenceNode.Status`; inference health remains controlled by vLLM health checks.

Added the React **DGX Hardware** view, Vitest coverage, Playwright administrator flow and a dedicated integration suite using a fake DCGM exporter on a different port/path from the fake vLLM runtime. The smoke test verifies two-GPU aggregation, path-prefix preservation, DCGM 503 isolation, retained diagnostics, audit and clear semantics against the real Docker/PostgreSQL stack.

Full backend, frontend and integration quality gate passed on commit `6c238a095273843e713a72fb2e26b2c7c434fc62`.

## 2026-09-10 - Custom architecture decision

NVIDIA Personal AI Router (PAIR) was reviewed as a possible alternative for the southbound inference fabric. The project decision is to continue with the existing LlmProxy + vLLM architecture and not adopt PAIR. Continue investing in our own gateway/routing/control-plane implementation unless this decision is explicitly revisited.

## 2026-09-10 - Benchmark harness - VALIDATED

Added a dedicated .NET 10 capacity benchmark under `tests/performance/`. The harness can target either LlmProxy or a direct vLLM service root, including path-prefixed roots, and supports Chat Completions/Responses plus streaming/non-streaming execution.

The measurement model includes warm-up, concurrency sweeps, success/error breakdown, p50/p95/p99 TTFT and total duration, requests/second and token throughput when upstream usage is available. Streaming TTFT is measured from the first meaningful output delta rather than response headers or metadata-only SSE chunks.

Secrets are intentionally kept out of command-line arguments: the harness reads bearer credentials from an environment variable named with `--api-key-env`. Prompt bodies and bearer tokens are excluded from JSON/CSV output. The built-in default is a small synthetic coding prompt; custom prompts are loaded from a file but only a safe label and character count are reported.

CI compiles and unit-tests the benchmark harness but does not generate load against a remote endpoint. Real DGX capacity sweeps remain deliberate environment tests.

The full quality gate passed on commit `49e7932f14118be403eec042a1393946143776ae`, including backend tests, benchmark tests, React/Vitest/Playwright, Docker/PostgreSQL inference smoke and DCGM smoke.

Detailed protocol: `docs/benchmarking.md`.

## 2026-09-13 - Canonical project handover snapshot

Added `docs/project-status.md` as the canonical current-state/resume document so future chats, maintainers and other LLMs do not need previous conversation history.

Updated `AGENTS.md` with a mandatory read order and source-of-truth precedence:

```text
code/tests/successful CI
  > docs/project-status.md
  > docs/development-log.md
  > docs/roadmap.md
  > focused documentation
  > old chat context
```

The handover now explicitly distinguishes implemented/validated work, external validation still required and the exact next product increment.

## Next increment

Implement **Capacity Profile + node-wide capacity enforcement/backpressure**:

- persist benchmark-derived recommended capacity separately from the active runtime limit;
- make application of recommendations explicit and audited;
- enforce a physical DGX aggregate concurrency ceiling across all deployments sharing that node;
- define OpenAI-compatible saturation/backpressure behavior;
- add API/UI/unit/integration coverage before marking the increment validated.

Real DGX benchmark sweeps and the real GitHub Copilot BYOK spike remain external validation activities and can proceed once the required hardware/tenant/public-endpoint access exists.
