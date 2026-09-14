# Project status / handover snapshot

Last reviewed: **2026-09-14**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated runtime baseline

Latest fully validated product baseline:

```text
1f607c8433fe2ca08a1c243b68d87587204f35ee
```

Validation evidence:

```text
GitHub Actions CI 34860662747
- Backend unit tests: success
- Benchmark harness tests: success
- React build / Vitest / Playwright: success
- Production Docker build: success
- PostgreSQL backend smoke: success
- DGX hardware smoke: success
- Capacity/backpressure smoke: success
- Usage governance/rate-limit smoke: success
```

Caller Governance first reached a complete green quality gate on:

```text
798f0a460dcc4f89b17e2ce89df66f511d324241
CI 34859931084
```

## Core product scope

LlmProxy is the enterprise inference-governance boundary, not only a DGX router.

```text
1. inference authentication
2. rate limiting / quotas
3. consolidated usage accounting
4. configurable usage groups + usage query/UI by group
```

Detailed contract: `docs/usage-governance.md`.

### Current request flow

```text
GitHub Copilot / OpenAI-compatible client
    -> bearer credential HMAC hash
    -> in-memory credential / primary UsageGroup resolution
    -> in-memory credential + logical-model rate policy
       -> 429 rate_limit_exceeded when caller policy is exceeded
    -> logical-model deployment lookup
       -> currently still reads persisted route catalog from PostgreSQL
    -> smart routing + node/deployment capacity admission
       -> 429 capacity_exhausted when healthy infrastructure is full
    -> DGX / vLLM
    -> metadata-only request metric
    -> usage aggregation by time/group/credential/model/node
```

`503 no_healthy_deployment` remains distinct from both 429 conditions.

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `GET /v1/models`, Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- HMAC-hashed API credentials; raw key shown once and never persisted.
- inference credential lookup from a thread-safe in-memory cache.
- credential expiry/revocation evaluated from the runtime snapshot.
- cache rebuilt from PostgreSQL at startup.
- credential create/revoke/group changes published after successful EF SaveChanges.
- `LastUsedAtUtc` persistence moved off the request path into a buffered background sink.
- Entra ID admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin and administrative audit trail.

### Caller Governance / usage reporting

- persisted `UsageGroup` with `Id`, `Name`, `Description`, `CreatedAtUtc`, `UpdatedAtUtc`.
- one optional primary `UsageGroupId` per API credential.
- `UsageGroupId` snapshot in every request metric for historically stable accounting.
- persisted rate-limit policies by credential with optional logical-model override.
- thread-safe fixed-window limiter enforced in memory before routing/admission.
- calculated `Retry-After`.
- distinct OpenAI-style `429 rate_limit_exceeded`.
- rate-limited requests captured as metadata metrics without forwarding to DGX.
- live policy republish after admin changes and restart rebuild from PostgreSQL.
- usage APIs for summary/group/credential/model dimensions.
- React `/admin/governance` control plane for groups, memberships, policies and usage.
- audit for Usage Group, membership and rate-policy changes.

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

### Observability

Request metrics include:

- request id/time/status/duration;
- logical model;
- API credential id and request-time Usage Group id;
- deployment and node;
- surface (`chat_completions` / `responses`);
- attempts/failover;
- streaming flag;
- upstream header latency and TTFT;
- input/output/total token counts when upstream reports them;
- error code.

No prompt, source-code or generated-output bodies are persisted by default.

### Hardware / benchmarking

- optional DCGM telemetry isolated from inference health.
- GPU utilization/framebuffer/temperature/power diagnostics.
- .NET benchmark harness for direct-vLLM vs gateway measurements.
- concurrency sweeps, p50/p95/p99 TTFT/duration, req/s and token throughput.

## Current hot-path state

The following are now in-memory runtime decisions:

```text
credential lookup / revocation / expiry
request UsageGroup snapshot
caller request-rate admission
active request/capacity counters
EWMA routing feedback
vLLM runtime pressure
routing policy/tuning
```

However, the entire inference path is **not yet DB-free**. `EfDeploymentCatalog` still queries PostgreSQL per inference request for logical model -> eligible deployment/node/model configuration. Historical documentation that implied no PostgreSQL query per inference request was too broad; the next increment should fix this remaining lookup.

## Operator onboarding

Installation/testing starts at:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

Supporting files include `docker/docker-compose.quickstart.yml` and `docker/.env.quickstart.example`.

## Current development focus

### Increment 1 — in-memory route/deployment catalog

Eliminate the remaining request-time PostgreSQL route lookup while keeping PostgreSQL authoritative for durable configuration:

1. publish logical models, nodes and deployments into a thread-safe runtime catalog;
2. rebuild catalog at startup;
3. update catalog only after successful persisted admin mutations;
4. keep volatile node health/runtime signals separate from durable configuration where appropriate;
5. preserve drain/disable semantics and path-prefixed service roots;
6. verify create/update/disable/drain/restart behavior with unit + Docker integration tests;
7. prove that ordinary `/v1` inference no longer performs catalog SQL reads.

### Increment 2 — retention / operational hygiene

- configurable request-metric retention, initial target 30–90 days;
- separate audit retention policy if required;
- background cleanup and optional rollups for long history.

### Increment 3 — token/budget quotas

Token/budget quotas follow validated request-rate limiting. Final output token count is generally known only after inference completes, so quota semantics must explicitly cover reservation/settlement/overage behavior rather than pretending to be simple request admission.

### Increment 4 — exports / production hardening

Prometheus/OpenTelemetry gateway export, reproducible frontend package locking, backup/restore verification and remaining operational hardening.

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
- PostgreSQL is durable source of truth; latency-sensitive runtime decisions are progressively published to in-memory state.
- API credential cache stores only the HMAC hash and safe credential metadata, never the raw secret.
- `LastUsedAtUtc` is eventually consistent by design and is persisted outside the request path.
- Hardware telemetry stays observational until benchmarks justify routing use.
- Group accounting uses one primary group per credential in V1 to prevent ambiguous/double-counted usage.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file and the focused document;
2. inspect latest `main` and GitHub Actions state;
3. treat `1f607c8433fe2ca08a1c243b68d87587204f35ee` / CI `34860662747` as the latest validated runtime baseline;
4. inspect `EfDeploymentCatalog` and all node/model/deployment mutation paths;
5. implement an in-memory route catalog without weakening health/drain/capacity semantics;
6. add explicit integration evidence that inference authentication and route resolution do not issue request-time SQL lookups;
7. update development log/roadmap/status after validation.

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
