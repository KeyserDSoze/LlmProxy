# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not old chat history, is the handover mechanism.

## Mandatory resume protocol

Before changing code, read these sources in this order:

1. `AGENTS.md` — engineering rules, architecture constraints and current resume point.
2. `docs/project-status.md` — canonical snapshot of validated work, unfinished work and external dependencies.
3. `docs/development-log.md` — chronological engineering trace and validation evidence.
4. `docs/roadmap.md` — DONE / ACTIVE / PLANNED / EXTERNAL milestones.
5. The focused document relevant to the task.
6. Inspect latest `main` and the GitHub Actions result before treating an increment as complete.

Source-of-truth precedence:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md
    > docs/development-log.md
    > docs/roadmap.md
    > focused technical documents
    > old chat context
```

For installation/testing start with `QUICKSTART.md` -> `docs/quickstart.md`.

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway for roughly 200 developers using GitHub Copilot or other OpenAI-compatible clients, with inference served by one to six NVIDIA DGX Spark nodes running vLLM.

```text
GitHub Copilot / OpenAI-compatible client
    -> public HTTPS endpoint
    -> LlmProxy (.NET 10)
       -> runtime credential authentication
       -> caller governance / rate limiting
       -> runtime logical-model/route resolution
       -> smart routing + physical capacity admission
       -> metadata-only usage accounting
    -> vLLM on DGX Spark 1..N
```

Clients see logical model aliases; physical DGX topology and provider model identifiers stay internal.

## Core product contract — do not drop from scope

LlmProxy is not only a router. The following are core product responsibilities:

1. **Inference authentication** — bearer/API credentials, raw secrets never persisted.
2. **Rate limiting / quotas** — caller governance before physical routing/admission.
3. **Usage consolidation** — request/token/latency/error metadata without prompts or generated code.
4. **Configurable usage groups** — one primary accounting group per inference credential in V1, with API/UI reporting.

Read `docs/usage-governance.md` before implementing auth/rate-limit/usage/reporting changes.

Important identity limitation: central GitHub Copilot BYOK may present one shared provider credential. The gateway must not infer individual people from IP. Per-team gateway accounting requires distinct credentials where client configuration permits it; per-user/adoption analytics may later ingest GitHub Copilot usage metrics.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code only under `src/`; tests/tooling only under `tests/`; Docker assets under `docker/`; docs under `docs/`.
- Work currently happens directly on `main` unless the project owner says otherwise.
- Do not log/persist prompts, source code, generated outputs, bearer tokens or raw API secrets.
- PostgreSQL is the durable source of truth; latency-sensitive inference decisions must use runtime-state abstractions rather than synchronous request-time SQL.
- Preserve SSE streaming and cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases.
- Capacity claims must come from benchmark evidence, not license count.
- GPU/DCGM telemetry is observational unless benchmark evidence justifies scheduling use.
- Every meaningful increment updates focused docs, `docs/project-status.md`, `docs/development-log.md`, roadmap when status changes, and this file when architecture/next-step changes.
- Never call work DONE merely because it was committed; require the relevant CI/integration evidence.

## Current validated baseline

Last reviewed: **2026-09-14**.

Latest fully validated product baseline:

```text
42c44753cd00d679a81bf065f410b7a497cdc000
```

Quality gate:

```text
GitHub Actions CI 34871542047
- backend unit + benchmark tests: success
- React/Vitest/Playwright: success
- production Docker + PostgreSQL integration: success
- backend inference smoke: success
- DGX hardware smoke: success
- capacity/backpressure smoke: success
- usage governance/rate-limit smoke: success
- route-catalog PostgreSQL-outage smoke: success
```

Earlier focused validation baselines:

```text
Caller Governance     798f0a460dcc4f89b17e2ce89df66f511d324241 / CI 34859931084
Credential auth cache 1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
```

## Inference hot-path boundary

Normal inference decisions are now DB-free after startup/runtime publication:

```text
API-key credential lookup
credential expiry/revocation decision
UsageGroup snapshot resolution
request-rate policy/admission
logical-model -> node/model/deployment route resolution
node health/drain/disable snapshot
request load/capacity counters
routing performance feedback
vLLM runtime pressure signals
```

PostgreSQL remains required for durable configuration, startup rebuild, control-plane mutations, audit/history/reporting and readiness. It is no longer synchronously queried to authenticate or resolve routes for ordinary `/v1` inference.

Runtime publication rules:

```text
PostgreSQL SaveChanges succeeds
    -> EF SaveChanges interceptor
    -> safe runtime snapshot update
```

At startup both credential state and route state are rebuilt from PostgreSQL.

`LastUsedAtUtc` is eventually consistent and written by a background/batched sink rather than synchronously in middleware.

Read `docs/runtime-cache.md` before changing cache/runtime synchronization architecture.

## Redis / multi-instance direction

The runtime contracts are deliberately provider-neutral so Redis can be added without rewriting inference code.

Recommended future topology when multiple gateway replicas are required:

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshot/version/event synchronization
local RAM  = L1 request-path runtime snapshot
```

Do **not** default to a Redis network read on every inference request. Keep local L1 for latency and resilience. For production-grade synchronization use versioned events and eventually a PostgreSQL transactional outbox so a committed DB mutation cannot be permanently lost if Redis publication temporarily fails.

Important: true multi-instance semantics require more than route-cache synchronization. Rate-limit counters, node/deployment capacity leases and other process-local coordination must also be made distributed/partition-aware before claiming globally enforced limits across replicas.

## Error/admission taxonomy

```text
caller policy exceeded
  -> 429 rate_limit_exceeded

healthy infrastructure exists but all eligible capacity is full
  -> 429 capacity_exhausted

no operational backend exists
  -> 503 no_healthy_deployment
```

Keep these states distinct in code, metrics and UI.

## Current development focus / resume point

Caller Governance, credential-auth caching and the runtime route catalog are validated.

Next engineering sequence:

1. **Retention / operational hygiene**
   - configurable request-metric retention, initial target 30–90 days;
   - separate audit retention policy;
   - background cleanup and optional long-term rollups.
2. **Token/budget quotas**
   - explicit reservation/settlement semantics for streaming, cancellation, failures and overage.
3. **Prometheus/OpenTelemetry gateway export** and remaining production hardening.
4. **Redis/distributed runtime coordination** only when multi-replica gateway/HA is actually required; preserve the abstractions documented in `docs/runtime-cache.md`.
5. **Physical acceptance** on real DGX/Copilot/Entra/Cloudflare environment.

Do not over-polish control-plane UI before the real Copilot -> gateway -> DGX acceptance path is proven.

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
QUICKSTART.md / docs/quickstart.md
    installation, GHCR, Linux/Windows, first smoke test

docs/project-status.md
    canonical state and exact resume point

docs/runtime-cache.md
    DB-free inference runtime state and Redis/multi-instance evolution

docs/usage-governance.md
    auth, rate limiting, configurable groups, consolidated usage/reporting

docs/development-log.md
    chronological engineering/validation trace

docs/roadmap.md
    milestone state/backlog

docs/capacity-control.md
    Capacity Profile + physical admission/backpressure

docs/benchmarking.md
    benchmark protocol

docs/routing.md
    routing and smart-routing tuning

docs/hardware-telemetry.md
    DCGM boundary

docs/github-copilot.md
    Copilot/BYOK and remaining external validation
```

## Handover checklist

Before ending a meaningful development session ensure the repository states what changed, exact green CI evidence, what remains unverified, architecture decisions, exact next step, external dependencies and focused docs changed. If `docs/project-status.md` points to an already-completed next step, fix it before considering the handover clean.
