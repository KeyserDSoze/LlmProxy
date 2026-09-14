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
       -> in-memory credential authentication
       -> caller governance / rate limiting
       -> logical model resolution
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
- Durable configuration/history belongs in PostgreSQL; latency-sensitive inference decisions should be published into in-memory runtime state.
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
1f607c8433fe2ca08a1c243b68d87587204f35ee
```

Quality gate:

```text
GitHub Actions CI 34860662747
- backend unit + benchmark tests: success
- React/Vitest/Playwright: success
- production Docker + PostgreSQL integration: success
- backend inference smoke: success
- DGX hardware smoke: success
- capacity/backpressure smoke: success
- usage governance/rate-limit smoke: success
```

Caller Governance itself was first fully validated on `798f0a460dcc4f89b17e2ce89df66f511d324241` / CI `34859931084`.

Validated capabilities include Chat Completions/Responses/SSE, Entra administration, HMAC-hashed API credentials, **in-memory inference credential authentication**, asynchronous/batched last-used persistence, Usage Groups, credential/model request rate limiting, `429 rate_limit_exceeded`, historically stable group snapshots in request metrics, grouped usage APIs/React UI, multi-DGX routing/failover, health, audit, inference/vLLM/DCGM telemetry, benchmark tooling, Capacity Profiles and `429 capacity_exhausted` physical backpressure.

## Inference hot-path boundary

The following runtime decisions are DB-free after startup/publication:

```text
API-key credential lookup
credential expiry/revocation decision
UsageGroup snapshot resolution
request-rate policy/admission
request load/capacity counters
routing performance feedback
vLLM runtime pressure signals
```

`ApiCredential` changes are persisted first, then published to the in-memory credential cache by an EF SaveChanges interceptor. Startup rebuilds the cache from PostgreSQL. `LastUsedAtUtc` is eventually consistent and written by a background/batched sink rather than synchronously in middleware.

**Known remaining hot-path DB access:** `EfDeploymentCatalog` still queries PostgreSQL per inference request to resolve logical model -> eligible deployment/node/model configuration. Do not claim the entire inference path is database-free until this catalog is cached.

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

Caller Governance and credential-auth caching are validated.

Next engineering sequence:

1. **In-memory route/model/deployment catalog**
   - eliminate the remaining per-inference PostgreSQL query in `EfDeploymentCatalog`;
   - keep persisted node/model/deployment configuration as source of truth;
   - publish configuration changes atomically after successful DB commits;
   - model node health as runtime state rather than requiring request-time DB reads;
   - prove live admin mutations and restart rebuild behavior with integration tests.
2. **Retention / operational hygiene**
   - configurable request-metric retention (initial target 30–90 days);
   - separate audit retention policy if required;
   - background cleanup/optional long-term rollups.
3. **Token/budget quotas** with explicit post-inference accounting semantics.
4. Prometheus/OpenTelemetry gateway export and remaining production hardening.
5. Physical acceptance on real DGX/Copilot/Entra/Cloudflare environment.

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
