# Project status / handover snapshot

Last reviewed: **2026-09-14**.

This is the canonical current-state snapshot for LlmProxy. A new maintainer/chat/LLM should be able to resume from this file without relying on previous conversations.

Read root `AGENTS.md` first for working rules and source-of-truth precedence.

## Current validated runtime baseline

Latest fully validated product baseline:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

The complete quality gate passed for that commit:

- .NET 10 restore/build;
- backend xUnit tests;
- benchmark-harness tests;
- React production build;
- Vitest;
- Playwright Chromium;
- production Docker image build;
- Docker/PostgreSQL inference smoke;
- DGX/DCGM hardware smoke;
- capacity/backpressure smoke.

The repository now also contains a VM/operator quickstart and dedicated quickstart Compose configuration. Those onboarding changes live on later commits and must remain CI-valid before being treated as validated operational packaging.

## Operator onboarding

Start here to install/test LlmProxy:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

The quickstart includes:

- Docker Engine + Compose installation on Ubuntu;
- Docker Desktop + WSL 2 on Windows;
- private GitHub Container Registry authentication;
- pulling `ghcr.io/keyserdsoze/llmproxy:*`;
- `.env`/PostgreSQL setup;
- `docker/docker-compose.quickstart.yml`;
- real vLLM or local mock inference;
- health/readiness/Admin UI/OpenAI API tests;
- optional Microsoft Entra setup;
- update/reset/troubleshooting.

Supporting files:

```text
docker/docker-compose.quickstart.yml
docker/.env.quickstart.example
```

## Implemented and validated

### Gateway / OpenAI compatibility

- .NET 10 ASP.NET Core gateway.
- logical public model aliases hide provider model names and DGX topology.
- `GET /v1/models`.
- `POST /v1/chat/completions`, streaming and non-streaming.
- `POST /v1/responses` compatibility path.
- arbitrary compatible OpenAI JSON fields preserved while only `model` is rewritten upstream.
- incremental SSE forwarding.
- cancellation propagation.
- failover only before response bytes are exposed downstream.

### Security / administration

- bearer/API-key inference authentication for OpenAI-compatible/BYOK clients.
- HMAC-hashed database-backed credentials; raw secret shown once.
- Entra ID admin plumbing.
- `LlmProxy.Admin` and `LlmProxy.Reader` roles.
- React Admin UI.
- administrative audit trail.
- node/model/deployment/routing/credential administration.

### Routing / multi-DGX

- complete HTTP(S) service roots with host/IP/port/path prefixes.
- health hysteresis with persisted diagnostics.
- operational states: `Healthy`, `Degraded`, `Unhealthy`, `Draining`, `Disabled`.
- `WeightedLeastLoaded`, `RoundRobin`, `WeightedRoundRobin`.
- node/deployment weights.
- pre-response failover.
- persisted live routing strategy.
- persisted/audited smart-routing tuning.

### Observability

- per-request duration/status/attempt/failover telemetry.
- upstream-header latency and TTFT.
- token counts when reported by upstream.
- vLLM running/waiting/KV-cache/token Prometheus telemetry.
- per-deployment EWMA TTFT/duration/infrastructure-failure feedback.
- optional DCGM GPU utilization/framebuffer/temperature/power telemetry.
- DCGM failures remain isolated from inference health.

### Benchmarking / capacity control

- .NET 10 benchmark harness under `tests/performance/`.
- direct-vLLM vs gateway comparison.
- Chat Completions / Responses, stream/non-stream.
- warm-up + concurrency sweeps.
- p50/p95/p99 TTFT/duration, req/s and token throughput.
- benchmark output JSON/CSV without prompt bodies or bearer secrets.
- persisted deployment Capacity Profile independent from active concurrency.
- explicit audited “apply recommended capacity”.
- atomic deployment + node-wide admission lease.
- physical DGX aggregate concurrency across deployments.
- `429 capacity_exhausted` + `Retry-After` when physical capacity is full.
- separate `503 no_healthy_deployment` when no operational backend exists.

## Current development focus

Next product increment: **rate limits / quotas by inference credential and logical model**.

Keep it distinct from physical capacity backpressure:

```text
request
  -> credential/model rate policy
       -> 429 rate_limit_exceeded when caller quota is exceeded
  -> routing/admission
       -> 429 capacity_exhausted when infrastructure is full
  -> DGX/vLLM
```

Recommended implementation order:

1. persist a rate-limit policy per credential with optional logical-model override;
2. initially support requests-per-minute/window limits;
3. publish policies into an in-memory limiter — no PostgreSQL query per inference request;
4. compute `Retry-After` from the limiter window;
5. add distinct metrics/error code `rate_limit_exceeded`;
6. add audit + Admin API/UI;
7. add unit + concurrent Docker integration tests;
8. run complete CI quality gate;
9. only then mark the increment VALIDATED.

Token quotas can follow as a separate increment because actual output token usage is known only after inference.

## External validation still required

- real DGX Spark with intended vLLM/model configuration;
- matched direct-vLLM vs gateway benchmark sweeps;
- representative multi-DGX coding workload;
- real Microsoft Entra application registration and role assignment;
- Cloudflare Tunnel/public HTTPS domain;
- real GitHub Copilot custom/BYOK flow against LlmProxy;
- on-prem self-hosted GitHub Actions deployment runner;
- GitHub Copilot usage-metrics/custom-model reporting if required for per-user adoption analytics.

## Important architecture decisions

### Keep custom LlmProxy + vLLM

NVIDIA Personal AI Router (PAIR) was evaluated. Project owner explicitly chose to continue with the custom LlmProxy/vLLM architecture unless that decision is reopened.

### PostgreSQL stays out of the inference hot path

Persist configuration/history in PostgreSQL, but route/admission decisions use in-memory state.

### Hardware telemetry remains observational

Do not add GPU utilization/temperature/power directly to routing without benchmark evidence.

### Copilot user identity is not guaranteed per request

Central BYOK may use one shared provider credential. Do not infer individual users from IP. If user/adoption analytics are needed, use GitHub Copilot usage metrics and correlate analytically with gateway telemetry.

## Exact resume point

A new development session should:

1. read `AGENTS.md` and this file;
2. inspect latest `main` and current CI result;
3. if doing installation/testing, follow `QUICKSTART.md` / `docs/quickstart.md`;
4. if doing product development, start the credential/model rate-limit increment described above;
5. update focused docs + this file + development log + roadmap whenever status changes;
6. never mark work validated without actual CI/integration evidence.

## Work after rate limiting

Planned product work includes:

- gateway Prometheus/OpenTelemetry export;
- backup/restore runbook and automated restore verification;
- credential rotation workflow;
- model/runtime upgrade/draining strategy;
- control-plane HA if required;
- GitHub Copilot usage-metrics ingestion/user analytics if desired;
- real benchmark-based smart-routing calibration.

## Documentation map

| Question | Source |
| --- | --- |
| How do I install/test it? | `QUICKSTART.md`, `docs/quickstart.md` |
| Where are we / what is next? | `docs/project-status.md` |
| What rules must an agent follow? | `AGENTS.md` |
| What happened over time? | `docs/development-log.md` |
| What milestones remain? | `docs/roadmap.md` |
| Capacity control | `docs/capacity-control.md` |
| Benchmark protocol | `docs/benchmarking.md` |
| DGX/DCGM telemetry | `docs/hardware-telemetry.md` |
| Routing | `docs/routing.md` |
| GitHub Copilot | `docs/github-copilot.md` |
| Deployment/operations/security | `docs/deployment.md`, `docs/operations.md`, `docs/security.md` |

## Handover maintenance rule

Every meaningful increment must leave this file accurate: validated commit/CI evidence, completed capability, unverified items, exact next step and external blockers. If this file points to an already-completed next step, it is stale and must be fixed before ending the session.
