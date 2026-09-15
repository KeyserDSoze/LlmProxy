# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not old chat history, is the handover mechanism.

## Mandatory resume protocol

Before changing code read, in order:

1. `AGENTS.md`.
2. `docs/project-status.md`.
3. `docs/development-log.md`.
4. `docs/roadmap.md`.
5. The focused document for the feature being changed.
6. Latest `main` and GitHub Actions state.

Source-of-truth precedence:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md
    > docs/development-log.md
    > docs/roadmap.md
    > focused technical documents
    > old chat context
```

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway for GitHub Copilot and other OpenAI-compatible clients, with inference served by one to six NVIDIA DGX Spark nodes running vLLM.

```text
GitHub Copilot / OpenAI-compatible client
    -> public HTTPS endpoint
    -> LlmProxy (.NET 10)
       -> runtime credential authentication
       -> caller governance / distributed rate limiting
       -> logical-model/route resolution from local L1 runtime state
       -> smart routing + distributed physical capacity admission
       -> metadata-only usage accounting
    -> vLLM on DGX Spark 1..N
```

Clients see logical model aliases; physical DGX topology and provider model identifiers stay internal.

## Core product contract — do not drop from scope

LlmProxy is not only a router. Core responsibilities are inference authentication, rate limiting/quotas, consolidated usage accounting, configurable usage groups, multi-instance runtime coordination and metadata-only enterprise observability.

Raw prompts, source code, generated outputs, bearer tokens and API secrets must never be persisted or added to logs/spans by default.

A centrally configured GitHub Copilot BYOK credential may be shared. The gateway must not infer individual users from IP. Per-team gateway attribution requires distinct credentials where client configuration permits it; per-user/adoption analytics may later ingest GitHub Copilot usage metrics.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code under `src/`; tests/tooling under `tests/`; Docker assets under `docker/`; docs under `docs/`.
- Work happens directly on `main` unless the project owner says otherwise.
- PostgreSQL is the durable source of truth.
- Redis is shared coordination/runtime L2 when enabled; local RAM remains request-path L1.
- Preserve SSE streaming and cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases.
- Capacity claims require benchmark evidence.
- GPU/DCGM telemetry is observational unless measured evidence justifies scheduling use.
- Meaningful increments update focused docs, project status, development log, roadmap where status changes, and this file when architecture/next-step changes.
- Never call work DONE merely because it was committed; require relevant CI/integration evidence.

## Current validated baseline

Last reviewed: **2026-09-15**.

Latest fully validated product baseline:

```text
79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
chore: wire outbox operations into deployment
```

Validation evidence:

```text
GitHub Actions CI 34976465066 — SUCCESS
- backend build/unit/benchmark tests
- React build / Vitest / Playwright
- production Docker image build
- PostgreSQL integration smoke
- DGX hardware smoke
- capacity/backpressure smoke
- usage governance/rate-limit smoke
- route-catalog PostgreSQL-outage smoke
- data-retention smoke, including processed-outbox retention and pending preservation

Full Stack Smoke 34976465149 — SUCCESS
- PostgreSQL + Redis runtime synchronization
- explicit OTLP/Tempo application spans
- cross-gateway rate-limit counters
- distributed Redis node/deployment capacity leases
- pre-TTL active lease-loss cancellation + metric/trace evidence
- transactional runtime-state outbox Redis-outage/recovery replay
- replay by a non-originating gateway updates that gateway's own L1
- runtime-sync outbox backlog/retry/error diagnostics before, during and after recovery
```

## Current runtime topology

```text
PostgreSQL = durable configuration/history/outbox authority
Redis      = distributed L2 snapshots/events + shared rate/capacity coordination
local RAM  = per-replica request-path L1
```

Ordinary inference route/credential/rate-policy decisions are DB-free after startup/runtime publication.

### Transactional DB -> Redis publication — VALIDATED

When Redis is enabled, runtime configuration mutations use a PostgreSQL transactional outbox:

```text
same PostgreSQL transaction
  durable Node/Model/Deployment/Credential/RatePolicy mutation
  + durable runtime_state_outbox row
commit

post-commit
  -> originating replica updates local L1

outbox worker
  -> acquire global PostgreSQL advisory publisher lock
  -> process pending rows strictly by outbox Id
  -> acknowledged Redis state write
  -> Redis global version increment + pub/sub event
  -> publishing replica applies the acknowledged change to its own L1
  -> mark row ProcessedAtUtc only after Redis acknowledgement
```

Failed publication stays pending with retry/backoff and blocks later rows, preserving mutation order across replicas. Replay is intentionally idempotent. If Redis is disabled, the outbox interceptor/worker is not registered.

`GET /api/admin/runtime-sync` exposes Redis sync state plus outbox `pendingCount`, `failedPendingCount`, oldest pending timestamp/age, max attempt count, last processed timestamp and last error.

Processed outbox rows have configurable retention (default 30 days). **Pending rows are never deleted by retention.**

Read `docs/runtime-cache.md`, `docs/full-stack.md` and `docs/data-retention.md` before changing these semantics.

## Error/admission taxonomy

```text
caller request policy exceeded      -> 429 rate_limit_exceeded
physical DGX saturated              -> 429 capacity_exhausted
capacity coordinator unavailable    -> 503 capacity_coordination_unavailable
active capacity lease unsafe        -> 503/abort capacity_lease_lost
no operational backend              -> 503 no_healthy_deployment
```

Keep these states distinct in code, metrics, traces and UI.

## Current development focus / resume point

1. **Token/budget quotas** — ACTIVE NEXT. Define and implement reservation/settlement semantics that remain correct for streaming, cancellation, failures and multi-replica Redis mode.
2. Credential rotation workflow.
3. Backup/restore + restore verification and remaining production hardening.
4. Physical acceptance on real DGX/Copilot/Entra/Cloudflare environment.

Do not resume from older assumptions that transactional outbox, retention or full-stack OTEL are pending; all are implemented and validated in the baseline above.

## Token/budget quota guardrails for the next increment

Request-rate limiting is already implemented. Token/budget quotas are different because final token usage may be unknown until inference completes. The next design must explicitly define:

- what is reserved before admission;
- how reservations are shared across gateway replicas;
- how actual usage settles a reservation;
- behavior when usage is missing/incomplete;
- cancellation/failure/stream-abort semantics;
- overage policy if actual usage exceeds reservation;
- expiry/recovery for abandoned reservations;
- distinct caller-facing taxonomy without conflating request rate, token budget and physical capacity.

Do not implement a naive post-response counter that allows unlimited concurrent oversubscription.

## External validation still required

- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted deployment runner;
- GitHub Copilot usage-metrics behavior if used for per-user analytics.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router (PAIR) was evaluated. The project owner explicitly chose to continue with custom **LlmProxy + vLLM**. Do not redirect toward PAIR unless that decision is explicitly reopened.

## Documentation map

```text
QUICKSTART.md / docs/quickstart.md       installation and first smoke test
docs/full-stack.md                       Redis + OTEL/Grafana + outbox operations
docs/project-status.md                   canonical state and exact resume point
docs/runtime-cache.md                    L1/L2 runtime state + transactional outbox
docs/usage-governance.md                 auth, rate limits, groups and usage reporting
docs/data-retention.md                   request/audit/processed-outbox retention
docs/development-log.md                  chronological engineering/validation trace
docs/roadmap.md                          milestone state/backlog
docs/capacity-control.md                 physical admission and Redis capacity leases
docs/benchmarking.md                     benchmark protocol
docs/routing.md                          routing and smart-routing tuning
docs/hardware-telemetry.md               DCGM boundary
docs/github-copilot.md                   Copilot/BYOK and external validation
```

## Handover checklist

Before ending a meaningful development session ensure the repository states what changed, exact green CI evidence, what remains unverified, architecture decisions, exact next step, external dependencies and focused docs changed. If `docs/project-status.md` points to an already-completed next step, fix it before considering the handover clean.
