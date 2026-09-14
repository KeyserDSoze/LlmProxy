# Project status / handover snapshot

Last reviewed: **2026-09-14**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated runtime baseline

Latest fully validated product baseline:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

That commit passed the complete quality gate: .NET build/tests, benchmark tests, React/Vitest/Playwright, production Docker build, PostgreSQL inference smoke, DCGM hardware smoke and capacity/backpressure smoke.

Later commits add operator onboarding/documentation and product-scope clarification; treat runtime behavior as validated only when the corresponding CI/integration gate is green.

## Core product scope

LlmProxy is the enterprise governance boundary, not only a DGX router.

Mandatory product responsibilities:

```text
1. inference authentication
2. rate limiting / quotas
3. consolidated usage accounting
4. configurable usage groups + usage query/UI by group
```

Detailed contract: `docs/usage-governance.md`.

### Intended request flow

```text
GitHub Copilot / OpenAI-compatible client
    -> bearer credential authentication
    -> credential / primary usage-group resolution
    -> credential + logical-model rate policy
       -> 429 rate_limit_exceeded when caller policy is exceeded
    -> routing + node/deployment capacity admission
       -> 429 capacity_exhausted when healthy infrastructure is full
    -> DGX / vLLM
    -> metadata-only request metric
    -> usage aggregation by time/group/credential/model/node
```

`503 no_healthy_deployment` remains distinct from both 429 conditions.

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `GET /v1/models`.
- Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- bearer/API-key inference authentication.
- HMAC-hashed persisted credentials; raw key shown once.
- Entra ID admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin and administrative audit trail.

### Routing / capacity

- complete path-prefixed DGX service roots.
- health hysteresis and node diagnostics.
- weighted least loaded / round robin / weighted round robin.
- pre-response failover only.
- persisted routing strategy and smart-routing tuning.
- vLLM queue/running/KV-cache signals and EWMA performance feedback.
- persisted Capacity Profile separate from active concurrency.
- explicit audited apply-recommended-capacity action.
- atomic deployment + node-wide physical capacity admission.
- `429 capacity_exhausted` + `Retry-After`.

### Usage/observability foundation

Already available for consolidated usage reporting:

- request id/time/status/duration;
- logical model;
- API credential id when authenticated;
- deployment and node;
- surface (`chat_completions` / `responses`);
- attempts/failover;
- streaming flag;
- upstream header latency and TTFT;
- input/output/total token counts when the upstream reports them;
- error code.

No prompt, source-code or generated-output bodies are persisted by default.

### Hardware / benchmarking

- optional DCGM telemetry isolated from inference health.
- GPU utilization/framebuffer/temperature/power diagnostics.
- .NET benchmark harness for direct-vLLM vs gateway measurements.
- concurrency sweeps, p50/p95/p99 TTFT/duration, req/s and token throughput.

## Operator onboarding

Installation/testing starts at:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

Supporting files include `docker/docker-compose.quickstart.yml` and `docker/.env.quickstart.example`.

## Current development focus

### Increment 1 — request rate limiting

Implement request-count governance before routing:

1. persisted policy per inference credential;
2. optional logical-model override;
3. requests-per-minute/window V1 semantics;
4. policy published into an in-memory limiter — no DB query per request;
5. distinct OpenAI-style `429 rate_limit_exceeded`;
6. calculated `Retry-After`;
7. metadata metrics for rate-limited requests;
8. Admin API/UI + audit;
9. unit/concurrent Docker integration coverage.

### Increment 2 — Usage Groups and reporting

Implement a persisted `UsageGroup` entity and primary credential membership:

```text
UsageGroup 1 --- N ApiCredential
ApiCredential -> zero or one primary UsageGroup
```

Initial group fields:

```text
Id
Name
Description
ExternalReference
Enabled
CreatedAtUtc
UpdatedAtUtc
```

Requirements:

- CRUD through Admin API;
- React UI for groups and credential assignment;
- snapshot `UsageGroupId` into each request metric so historical accounting does not change if a credential later moves group;
- aggregate usage by group, credential and logical model;
- time-window filters;
- requests, successes/errors, rate-limit/capacity rejects, input/output/total tokens, p50/p95 duration and TTFT;
- drill down group -> credentials -> logical models.

### Increment 3 — quotas

Token/budget quotas follow request rate limiting. Final output token count is generally known only after inference completes, so token quotas must have explicit semantics rather than pretending to be identical to request admission.

## Identity limitation to preserve

A centrally configured GitHub Copilot BYOK provider may use one shared API credential. LlmProxy can reliably attribute that traffic to the credential/group but cannot infer the individual GitHub user from the request.

Therefore:

- distinct provider credentials can be used where configuration allows team-level gateway attribution;
- GitHub Copilot usage metrics can later supply user/adoption analytics;
- never infer users from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark runs;
- representative multi-DGX coding workload;
- real Microsoft Entra application/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- GitHub Copilot usage-metrics/custom-model reporting if per-user analytics are required.

## Important architecture decisions

- Continue custom LlmProxy + vLLM; NVIDIA PAIR was evaluated and rejected for the current direction.
- PostgreSQL stays out of the inference hot path.
- Hardware telemetry stays observational until benchmarks justify routing use.
- Group accounting uses one primary group per credential in V1 to prevent ambiguous/double-counted usage.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file and `docs/usage-governance.md`;
2. inspect latest `main` and GitHub Actions state;
3. finish/validate any pending quickstart documentation CI if still in progress;
4. implement credential/model request rate limiting;
5. after rate limiting is green, implement Usage Groups + metric group snapshot + aggregated API/UI;
6. update development log/roadmap/status after each validated increment.

## Documentation map

| Question | Source |
| --- | --- |
| Install/test | `QUICKSTART.md`, `docs/quickstart.md` |
| Current state / resume | `docs/project-status.md` |
| Auth/rate limits/groups/usage | `docs/usage-governance.md` |
| Engineering rules | `AGENTS.md` |
| Chronology | `docs/development-log.md` |
| Milestones | `docs/roadmap.md` |
| Capacity control | `docs/capacity-control.md` |
| Benchmarking | `docs/benchmarking.md` |
| Routing | `docs/routing.md` |
| Copilot integration | `docs/github-copilot.md` |

Every meaningful increment must keep this snapshot current and must not be marked validated without actual CI/integration evidence.
