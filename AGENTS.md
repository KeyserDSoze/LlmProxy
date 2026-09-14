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
       -> authentication
       -> caller governance / rate limiting
       -> logical model resolution
       -> smart routing + capacity admission
       -> metadata-only usage accounting
    -> vLLM on DGX Spark 1..N
```

Clients see logical model aliases; physical DGX topology and provider model identifiers stay internal.

## Core product contract — do not drop from scope

LlmProxy is not only a router. The following are core product responsibilities:

1. **Inference authentication** — LlmProxy validates bearer/API credentials; raw secrets are never persisted.
2. **Rate limiting / quotas** — LlmProxy enforces caller governance before physical routing/admission.
3. **Usage consolidation** — request/token/latency/error metadata is consolidated centrally without persisting prompts or generated code.
4. **Configurable usage groups** — administrators can create groups, assign inference credentials to them, query usage by group and drill down through API and React UI.

Read `docs/usage-governance.md` before implementing auth/rate-limit/usage/reporting changes.

V1 accounting semantics:

```text
UsageGroup 1 --- N ApiCredential
ApiCredential -> at most one primary UsageGroup
```

A single primary group keeps accounting unambiguous. If overlapping classifications are needed later, add reporting tags separately.

Important identity limitation: central GitHub Copilot BYOK may present one shared provider credential. The gateway must not infer individual people from IP. Per-team gateway accounting requires distinct credentials where the client setup allows it; per-user/adoption analytics can later ingest GitHub Copilot usage metrics.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code only under `src/`; tests/tooling only under `tests/`; Docker assets under `docker/`; docs under `docs/`.
- Work currently happens directly on `main` unless the project owner says otherwise.
- Do not log/persist prompts, source code, generated outputs, bearer tokens or raw API secrets.
- PostgreSQL must stay out of the inference hot path. Persist policy/history, publish active policy to in-memory runtime state.
- Preserve SSE streaming and cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases.
- Capacity claims must come from benchmark evidence, not license count.
- GPU/DCGM telemetry is observational unless benchmark evidence justifies scheduling use.
- Every meaningful increment updates focused docs, `docs/project-status.md`, `docs/development-log.md`, roadmap when status changes, and this file when architecture/next-step changes.
- Never call work DONE merely because it was committed; require the relevant CI/integration evidence.

## Current validated baseline

Last reviewed: **2026-09-14**.

Latest fully validated capacity-control runtime baseline:

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

Validated capabilities include OpenAI-compatible Chat Completions/Responses, SSE, API-key auth, Entra admin plumbing, multi-DGX routing/failover, health hysteresis, audit, request/token telemetry, vLLM runtime metrics, DCGM hardware telemetry, benchmark tooling, Capacity Profiles, atomic deployment/node-wide admission and explicit `429 capacity_exhausted` backpressure.

Operational quickstart/documentation changes live on later commits and must themselves remain CI-valid.

## Error/admission taxonomy

Keep these states operationally distinct:

```text
caller policy exceeded
  -> 429 rate_limit_exceeded

healthy infrastructure exists but all eligible capacity is full
  -> 429 capacity_exhausted

no operational backend exists
  -> 503 no_healthy_deployment
```

## Current development focus / resume point

Capacity Profile + node-wide capacity/backpressure is validated.

Next product sequence:

1. **Credential/model request rate limiting**
   - persisted policy per inference credential with optional logical-model override;
   - in-memory limiter, no DB read per request;
   - computed `Retry-After`;
   - distinct `429 rate_limit_exceeded`;
   - audit + Admin API/UI + concurrent integration tests.
2. **Usage groups and group reporting**
   - persisted `UsageGroup` CRUD;
   - assign one primary group to each API credential;
   - snapshot `UsageGroupId` into request metrics for historically stable accounting;
   - aggregate requests/tokens/errors/TTFT/duration by group, credential and logical model;
   - React **Usage & Governance** view and APIs.
3. **Token/budget quotas** after request-rate limiting, because final token usage is generally known only after inference completes.
4. Gateway Prometheus/OpenTelemetry export and remaining production hardening.

Read `docs/usage-governance.md` for the detailed target API/reporting model.

## External validation still required

- real DGX Spark + intended vLLM/model profile and benchmark sweeps;
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
