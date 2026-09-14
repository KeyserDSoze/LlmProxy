# Project status / handover snapshot

Last reviewed: **2026-09-14**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated runtime baseline

Latest fully validated product baseline:

```text
42c44753cd00d679a81bf065f410b7a497cdc000
```

Validation evidence:

```text
GitHub Actions CI 34871542047
- Backend unit tests: success
- Benchmark harness tests: success
- React build / Vitest / Playwright: success
- Production Docker build: success
- PostgreSQL backend smoke: success
- DGX hardware smoke: success
- Capacity/backpressure smoke: success
- Usage governance/rate-limit smoke: success
- Route catalog PostgreSQL-outage smoke: success
```

Focused earlier baselines:

```text
Caller Governance     798f0a460dcc4f89b17e2ce89df66f511d324241 / CI 34859931084
Credential auth cache 1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
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

## Current request flow

```text
GitHub Copilot / OpenAI-compatible client
    -> bearer credential HMAC hash
    -> runtime credential / primary UsageGroup resolution
    -> runtime credential + logical-model rate policy
       -> 429 rate_limit_exceeded when caller policy is exceeded
    -> runtime logical-model -> deployment/node/provider-model catalog
    -> smart routing + node/deployment capacity admission
       -> 429 capacity_exhausted when healthy infrastructure is full
    -> DGX / vLLM
    -> metadata-only request metric
    -> asynchronous persistence / consolidated reporting
```

`503 no_healthy_deployment` remains distinct from both 429 conditions.

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `GET /v1/models`, Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- HMAC-hashed API credentials; raw key shown once and never persisted.
- inference credential lookup from a thread-safe runtime cache.
- credential expiry/revocation evaluated from runtime snapshot.
- credential cache rebuilt from PostgreSQL at startup.
- credential create/revoke/group changes published after successful EF SaveChanges.
- `LastUsedAtUtc` persistence moved off the request path into a buffered background sink.
- Entra ID admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React Admin and administrative audit trail.

### Caller Governance / usage reporting

- persisted `UsageGroup` and one optional primary `UsageGroupId` per API credential.
- `UsageGroupId` snapshot in every request metric for historically stable accounting.
- persisted rate-limit policies by credential with optional logical-model override.
- thread-safe fixed-window limiter enforced before routing/admission.
- calculated `Retry-After` and distinct `429 rate_limit_exceeded`.
- live policy republish after admin changes and restart rebuild from PostgreSQL.
- usage APIs for summary/group/credential/model dimensions.
- React `/admin/governance` for groups, memberships, policies and usage.
- governance changes audited.

### Runtime route catalog / DB-free inference lookup

The former request-time `EfDeploymentCatalog` has been removed.

Current route resolution uses singleton `IRouteCatalog` / `InMemoryRouteCatalog` snapshots containing the routing-relevant portions of:

```text
InferenceNode
ModelDefinition
ModelDeployment
```

Behavior:

- startup rebuild from PostgreSQL;
- copy-on-write/versioned runtime snapshots;
- node/model/deployment mutations published by EF SaveChanges interceptor only after durable save succeeds;
- health/drain/disable changes are published because they mutate the tracked node entity;
- `/v1/models` resolves from runtime state;
- inference logical-model -> provider-model/node/deployment lookup resolves from runtime state;
- `GET /api/admin/routing/catalog` exposes provider/version/node/model/deployment counts.

The dedicated integration smoke deliberately stops PostgreSQL **after startup** and proves both authenticated `/v1/models` and an actual Chat Completion continue through the configured vLLM mock. This is the explicit evidence that ordinary inference authentication and route resolution do not require synchronous request-time SQL.

### Routing / capacity

- complete path-prefixed DGX service roots.
- health hysteresis and node diagnostics.
- weighted least loaded / round robin / weighted round robin.
- pre-response failover only.
- persisted routing strategy and smart-routing tuning with runtime state.
- vLLM queue/running/KV-cache signals and EWMA performance feedback.
- persisted Capacity Profile separate from active concurrency.
- audited apply-recommended-capacity action.
- atomic deployment + node-wide physical capacity admission.
- `429 capacity_exhausted` + `Retry-After`.

### Observability

Request metrics include request id/time/status/duration, logical model, API credential/group snapshot, deployment/node, surface, attempts/failover, streaming, upstream latency/TTFT, token counts and error code.

No prompt, source-code or generated-output bodies are persisted by default.

### Hardware / benchmarking

- optional DCGM telemetry isolated from inference health.
- GPU utilization/framebuffer/temperature/power diagnostics.
- .NET benchmark harness for direct-vLLM vs gateway measurements.
- concurrency sweeps, p50/p95/p99 TTFT/duration, req/s and token throughput.

## Inference hot-path state

After startup/configuration publication, ordinary `/v1` inference decisions are now memory-first:

```text
credential lookup / revocation / expiry
request UsageGroup snapshot
caller request-rate admission
logical model + route catalog resolution
health/drain/disable route snapshot
active request/capacity counters
EWMA routing feedback
vLLM runtime pressure
routing policy/tuning
```

PostgreSQL remains intentionally required for:

```text
migrations/startup rebuild
admin durable configuration
readyz connectivity signal
audit/history/reporting
request metrics persistence
background LastUsedAtUtc persistence
```

Background persistence can fail/retry independently; it is not a synchronous inference admission dependency.

## Runtime cache / Redis direction

Focused architecture: `docs/runtime-cache.md`.

Current single-instance topology:

```text
PostgreSQL -> runtime snapshots in LlmProxy RAM
```

Recommended multi-instance evolution:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshot/version/event synchronization
local RAM  = request-path L1 on each gateway replica
```

A direct `RedisRouteCatalog` is technically straightforward behind the current abstractions, but Redis-on-every-request is not the preferred default because it adds a network dependency to inference.

For production-grade synchronization use a PostgreSQL transactional outbox -> Redis publication/version event -> replica L1 refresh. True HA/global limits also require distributed handling for rate-limit counters and physical capacity leases, not only route-cache synchronization.

## Operator onboarding

Installation/testing starts at:

```text
QUICKSTART.md
  -> docs/quickstart.md
```

Supporting files include `docker/docker-compose.quickstart.yml` and `docker/.env.quickstart.example`.

## Current development focus

### Increment 1 — retention / operational hygiene

- configurable request-metric retention, initial target 30–90 days;
- separate audit retention policy;
- background cleanup;
- optional rollups for long history.

### Increment 2 — token / budget quotas

Request-rate limiting is already validated. Token/budget quotas need explicit reservation/settlement/overage semantics for streaming, cancellation and failures because final token usage is known only after inference.

### Increment 3 — exports / production hardening

- Prometheus gateway metrics exporter;
- OpenTelemetry export;
- frontend lockfiles + `npm ci` reproducibility;
- backup/restore verification;
- credential rotation workflow.

### Increment 4 — Redis / multi-instance runtime coordination when required

Do not add Redis merely to replace a fast local lookup. Add it when shared multi-replica synchronization or HA requires it, preserving local L1 state.

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
- PostgreSQL is durable source of truth; normal inference decisions use runtime snapshots.
- Runtime snapshots are disposable/rebuildable and are never intentionally published ahead of durable persistence.
- Redis, when introduced, should normally synchronize replicas rather than replace the local request-path L1.
- API credential runtime state stores HMAC hash and safe metadata, never raw secrets.
- `LastUsedAtUtc` is eventually consistent by design.
- Hardware telemetry stays observational until benchmarks justify routing use.
- Group accounting uses one primary group per credential in V1.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file and the focused document;
2. inspect latest `main` and GitHub Actions state;
3. treat `42c44753cd00d679a81bf065f410b7a497cdc000` / CI `34871542047` as the latest validated runtime baseline;
4. read `docs/runtime-cache.md` before touching runtime/cache architecture;
5. continue with request-metric/audit retention unless the product owner reprioritizes Redis/HA or physical acceptance;
6. update development log/roadmap/status after validation.

## Documentation map

| Question | Source |
| --- | --- |
| Install/test | `QUICKSTART.md`, `docs/quickstart.md` |
| Current state / resume | `docs/project-status.md` |
| Runtime cache / Redis | `docs/runtime-cache.md` |
| Auth/rate limits/groups/usage | `docs/usage-governance.md` |
| Engineering rules | `AGENTS.md` |
| Chronology | `docs/development-log.md` |
| Milestones | `docs/roadmap.md` |
| Capacity control | `docs/capacity-control.md` |
| Benchmarking | `docs/benchmarking.md` |
| Routing | `docs/routing.md` |
| Copilot integration | `docs/github-copilot.md` |

Every meaningful increment must keep this snapshot current and must not be marked validated without actual CI/integration evidence.
