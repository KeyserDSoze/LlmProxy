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

`WeightedLeastLoaded` combines configured capacity/weights, gateway active work, health, EWMA feedback and vLLM runtime pressure.

## 2026-09-09 - Persisted smart-routing tuning

Moved routing coefficients into a persisted/audited `RoutingTuningPolicy` and thread-safe runtime state. Changes apply live without restart.

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

## 2026-09-14 - Caller Governance scope

Confirmed that LlmProxy owns inference authentication, rate limiting/quotas, consolidated usage accounting and configurable Usage Groups/query UI.

V1 accounting deliberately uses one primary group per API credential. Shared GitHub Copilot provider credentials are attributable to their gateway credential/group, not to individual GitHub users; source IP must never be used as identity.

## 2026-09-14 - Caller Governance + Usage Groups - VALIDATED

Implemented persisted `UsageGroup`, nullable primary group membership on `ApiCredential`, request-time `UsageGroupId` snapshots in request metrics, persisted credential rate policies with optional logical-model override and a thread-safe fixed-window runtime limiter.

Caller policy is enforced before routing/physical capacity admission and is intentionally distinct from DGX saturation:

```text
caller policy exceeded -> 429 rate_limit_exceeded
physical capacity full -> 429 capacity_exhausted
no backend -> 503 no_healthy_deployment
```

Added calculated `Retry-After`, governance audit events, usage aggregation by group/credential/logical model and React `/admin/governance` administration/reporting.

The Docker governance smoke exposed and helped correct two real integration defects rather than papering over them:

1. EF migration snapshot initially lagged the model; the snapshot was corrected while keeping `PendingModelChangesWarning` protection enabled.
2. Npgsql could not translate `GroupBy -> custom record constructor -> OrderBy`. The report now performs aggregation in PostgreSQL using SQL-translatable anonymous projections and materializes only aggregate rows before final record mapping/order in memory.

The smoke verifies two admitted calls followed by `429 rate_limit_exceeded`, `Retry-After`, stable Usage Group attribution, aggregate report dimensions, persistence/restart republish and audit.

Full quality gate passed on:

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

## 2026-09-14 - In-memory inference credential authentication - VALIDATED

Removed the API-credential PostgreSQL lookup from the `/v1` authentication path.

Added:

- thread-safe copy-on-write `IApiCredentialCache`, keyed by HMAC hash and containing only safe credential metadata;
- startup cache rebuild from PostgreSQL;
- expiry/revocation/group decisions from the runtime snapshot;
- EF SaveChanges interceptor that publishes credential create/revoke/group changes only after the database save succeeds;
- buffered background `LastUsedAtUtc` persistence so authentication middleware does not perform synchronous database writes;
- concurrent unit coverage for atomic cache publication.

Full quality gate passed on:

```text
commit 1f607c8433fe2ca08a1c243b68d87587204f35ee
CI     34860662747
```

## 2026-09-14 - Runtime route/model/deployment catalog - VALIDATED

Removed the final synchronous route-catalog PostgreSQL lookup from ordinary `/v1` inference.

Introduced provider-neutral `IRouteCatalog` / `IDeploymentCatalog` runtime contracts and a versioned copy-on-write `InMemoryRouteCatalog` containing routing snapshots for nodes, logical/provider models and deployments.

Startup now rebuilds the catalog from PostgreSQL. A dedicated EF SaveChanges interceptor captures `InferenceNode`, `ModelDefinition` and `ModelDeployment` additions/modifications/deletions and publishes them only after the durable save succeeds. This automatically covers admin edits plus node health/drain/disable changes that mutate tracked node entities.

Added:

- unit tests for candidate eligibility, effective weights/capacity, live upserts, disabled entities and public-model listing;
- `GET /api/admin/routing/catalog` with provider/version/node/model/deployment diagnostics;
- dedicated PostgreSQL-outage smoke test;
- removal of the obsolete `EfDeploymentCatalog` implementation.

The dedicated smoke verifies a live node configuration mutation increments the runtime catalog version, then deliberately stops PostgreSQL after startup and proves:

```text
GET /api/admin/routing/catalog -> still available
GET /v1/models                 -> 200 from runtime catalog
POST /v1/chat/completions      -> 200 and reaches configured vLLM mock
```

Therefore normal inference credential authentication + logical-model route resolution + routing/admission no longer requires a synchronous PostgreSQL query after startup/runtime publication.

Full quality gate passed on:

```text
commit 42c44753cd00d679a81bf065f410b7a497cdc000
CI     34871542047
```

Backend/unit/benchmark, React/Vitest/Playwright, production Docker, backend inference, DCGM, capacity, Governance and route-catalog PostgreSQL-outage smoke jobs all passed.

## 2026-09-14 - Redis-ready runtime-cache architecture

Added `docs/runtime-cache.md` and intentionally kept inference code dependent on cache/runtime abstractions rather than cache technology.

Recommended future multi-instance topology:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshot/version/event synchronization
local RAM  = per-replica request-path L1
```

A direct Redis-backed catalog can be implemented behind the existing contracts, but the preferred architecture keeps request-path lookups local and uses Redis to synchronize replicas. For stronger DB -> Redis delivery guarantees, plan a PostgreSQL transactional outbox rather than a distributed transaction.

A real multi-replica deployment must also distribute/coordinate rate-limit counters and node/deployment capacity leases before global limits can be claimed; route-catalog synchronization alone is insufficient.

## Next increment

Proceed with request-metric retention/background cleanup and define audit retention independently. Then define token/budget quota reservation/settlement semantics, add Prometheus/OpenTelemetry export and perform real DGX/Copilot/Entra/Cloudflare acceptance when the external environment is available.

Redis/distributed coordination is ready as an architectural evolution and should be implemented when multi-replica HA becomes a concrete requirement or the product owner explicitly reprioritizes it.
