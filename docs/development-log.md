# Development log

This file is the chronological engineering trace for LlmProxy. Keep entries concise but specific enough that a new maintainer can understand what changed and how it was validated.

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

Introduced root `AGENTS.md` as the primary project handover for AI agents and maintainers. From this point forward every meaningful technical increment must update focused documentation, append this development log, update roadmap status when relevant and keep `AGENTS.md`'s current state/next step accurate.

CI is configured to cancel superseded runs for the same branch or pull request so rapid development on `main` does not waste runners testing obsolete commits.

## 2026-09-09 - DGX/DCGM hardware telemetry backend - IN DEVELOPMENT

Started the first hardware-observability increment. The key architectural decision is to keep NVIDIA/DCGM telemetry separate from both vLLM runtime pressure and node health.

Added an optional `HardwareMetricsBaseAddress` to each `InferenceNode`, persisted through an EF Core migration. This allows vLLM and DCGM exporter to live on different ports or path-prefixed service roots. Added a dedicated runtime endpoint to configure or clear that address with an audit event.

Added a DCGM Prometheus parser for `DCGM_FI_DEV_GPU_UTIL`, `DCGM_FI_DEV_FB_USED`, `DCGM_FI_DEV_FB_FREE`, optional `DCGM_FI_DEV_FB_TOTAL`, `DCGM_FI_DEV_GPU_TEMP` and `DCGM_FI_DEV_POWER_USAGE`. Multi-GPU snapshots aggregate average/max utilization, total framebuffer usage/free memory, memory usage ratio, maximum temperature and total power.

Added an in-memory hardware snapshot tracker and background collector. HTTP/parse failures mark only the hardware snapshot unavailable while preserving the last successful sample; they do not update `InferenceNode.Status` and do not block inference. Added `GET /api/admin/hardware` and `PUT /api/admin/nodes/{id}/hardware-metrics`.

Added Docker/bootstrap settings and backend unit coverage for address normalization, DCGM parsing and failure-state preservation. Detailed behavior is documented in `docs/hardware-telemetry.md`.

This increment is **not complete yet**: backend CI must pass, then Docker integration plus React/Vitest/Playwright visibility must be added before changing the status to validated.

## Next increment

Validate the hardware backend quality gate, add fake-DCGM integration coverage, then expose hardware telemetry and node hardware-endpoint configuration in the React admin UI. After that, move to the real GitHub Copilot BYOK spike and benchmark harness.
