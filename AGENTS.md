# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not old chat history, is the handover mechanism.

## Mandatory resume protocol

Before changing code, read these sources in this order:

1. `AGENTS.md` — engineering rules, architecture constraints and current resume point.
2. `docs/project-status.md` — canonical validated snapshot and unfinished work.
3. `docs/development-log.md` — chronological engineering trace and validation evidence.
4. `docs/roadmap.md` — DONE / ACTIVE / PLANNED / EXTERNAL milestones.
5. The focused document relevant to the task.
6. Inspect latest `main` and GitHub Actions before treating an increment as complete.

Source-of-truth precedence:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md
    > docs/development-log.md
    > docs/roadmap.md
    > focused technical documents
    > old chat context
```

For installation/testing start with `QUICKSTART.md` -> `docs/quickstart.md`. For the Redis/OTel/Grafana topology read `docs/full-stack.md`.

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

LlmProxy is not only a router. Core responsibilities are:

1. inference authentication;
2. rate limiting / quotas;
3. consolidated usage accounting;
4. configurable usage groups;
5. multi-instance runtime coordination when Redis is enabled;
6. metadata-only enterprise observability.

Raw prompts, source code, generated outputs, bearer tokens and API secrets must never be persisted or added to logs/spans by default.

Important identity limitation: central GitHub Copilot BYOK may present one shared provider credential. The gateway must not infer individual people from IP. Per-team gateway accounting requires distinct credentials where client configuration permits it; per-user/adoption analytics may later ingest GitHub Copilot usage metrics.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code only under `src/`; tests/tooling under `tests/`; Docker assets under `docker/`; docs under `docs/`.
- Work currently happens directly on `main` unless the project owner says otherwise.
- PostgreSQL is the durable source of truth.
- Redis is shared coordination/runtime L2 when enabled; local RAM remains request-path L1.
- Preserve SSE streaming and cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases.
- Capacity claims must come from benchmark evidence, not license count.
- GPU/DCGM telemetry is observational unless benchmark evidence justifies scheduling use.
- Every meaningful increment updates focused docs, project status, development log, roadmap when status changes, and this file when architecture/next-step changes.
- Never call work DONE merely because it was committed; require the relevant CI/integration evidence.

## Current validated baseline

Last reviewed: **2026-09-15**.

Latest fully validated runtime baseline:

```text
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
```

Validation evidence:

```text
GitHub Actions CI 34961566507 — SUCCESS
- backend unit + benchmark tests
- React build / Vitest / Playwright
- production Docker build
- PostgreSQL backend integration smoke
- DGX hardware smoke
- capacity/backpressure smoke
- usage governance/rate-limit smoke
- route-catalog PostgreSQL-outage smoke
- data-retention smoke

Full Stack Smoke 34961566463 — SUCCESS
- PostgreSQL + Redis runtime synchronization
- explicit LlmProxy OpenTelemetry spans through OTLP/Tempo
- Grafana/Tempo/Loki/Prometheus wiring
- cross-gateway Redis rate-limit counters
- distributed Redis node/deployment capacity leases
- proactive cancellation when Redis lease coordination is lost
- `capacity_lease_lost` request metric + Tempo trace evidence
```

The earlier known-good pre-hardening checkpoint `3740692ffa3afa140e1a8f0ade5440e430838599` also had both standard CI and Full Stack Smoke green, but `6ec3c291...` is the canonical baseline.

## Current runtime topology

Normal inference decisions are DB-free after startup/runtime publication:

```text
PostgreSQL = durable configuration/history authority
Redis      = distributed L2 snapshots/events + shared rate/capacity coordination
local RAM  = per-replica request-path L1
```

Runtime hot-path state includes credential snapshots, UsageGroup attribution, rate policies/counters, logical-model routes, node health/drain/disable state, routing feedback, vLLM pressure and capacity admission.

Redis-enabled multi-instance behavior is now real, not merely planned:

- route, credential and rate-policy runtime state is synchronized between replicas;
- request-rate counters are shared in Redis;
- physical node/deployment capacity uses Redis leases;
- a Redis coordination failure during acquisition fails closed;
- if an active Redis capacity lease cannot be renewed safely, the request is cancelled before lease expiry;
- if headers have not started, the caller receives `503` + `Retry-After: 1` + `capacity_lease_lost`;
- if streaming already started, the connection is aborted rather than allowing unsafe inference to continue;
- request metrics and tracing record `capacity_lease_lost`.

Read `docs/runtime-cache.md` and `docs/capacity-control.md` before changing these semantics.

## Remaining correctness gap: transactional outbox

Runtime configuration mutations currently have a narrow crash window:

```text
PostgreSQL commit succeeds
    -> EF SavedChanges interceptor updates local L1
    -> runtime event is queued for Redis publication
```

A process crash after the DB commit but before durable Redis publication can lose the outbound change until reconciliation/restart repairs it. The next engineering increment is a PostgreSQL transactional outbox.

Correctness requirement for that work:

```text
same PostgreSQL transaction
  - durable configuration mutation
  - durable RuntimeStateOutbox row
commit

outbox worker
  -> perform acknowledged Redis snapshot/state update + pub/sub publication
  -> only then mark outbox row delivered
```

Do **not** mark an outbox event delivered merely because it was placed on the existing in-memory outbound channel. The acknowledgement boundary must be the durable Redis operation itself. Add retry/backoff, idempotency, observability and integration coverage.

## Error/admission taxonomy

```text
caller policy exceeded
  -> 429 rate_limit_exceeded

healthy infrastructure exists but all eligible capacity is full
  -> 429 capacity_exhausted

Redis capacity coordination unavailable before admission
  -> 503 capacity_coordination_unavailable

active distributed capacity lease becomes unsafe
  -> 503/connection abort + capacity_lease_lost

no operational backend exists
  -> 503 no_healthy_deployment
```

Keep these states distinct in code, metrics, traces and UI.

## Current development focus / resume point

1. **Transactional outbox for DB -> Redis runtime publication** — ACTIVE next increment.
2. Token/budget quotas with explicit reservation/settlement semantics for streaming/cancellation/failures.
3. Remaining production hardening such as credential rotation and backup/restore verification.
4. Physical acceptance on real DGX/Copilot/Entra/Cloudflare environment.

Retention is already implemented and validated. OpenTelemetry/Tempo/Grafana full-stack export is already implemented and validated. Do not resume from the older roadmap assumption that these are still the immediate next items.

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
docs/full-stack.md                       Redis + OpenTelemetry + Grafana stack
docs/project-status.md                   canonical state and exact resume point
docs/runtime-cache.md                    L1/L2 runtime state, Redis and outbox boundary
docs/usage-governance.md                 auth, rate limits, groups and usage reporting
docs/data-retention.md                   request/audit retention
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