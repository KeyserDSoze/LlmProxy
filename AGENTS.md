# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not old chat history, is the handover mechanism.

## Mandatory resume protocol

Read before changing code:

1. `AGENTS.md`.
2. `docs/project-status.md`.
3. latest entries in `docs/development-log.md`.
4. `docs/roadmap.md`.
5. focused docs for the feature being changed.
6. latest `main` and GitHub Actions state.

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

LlmProxy is Agic's productizable on-premises AI gateway/governance boundary for GitHub Copilot and other OpenAI-compatible clients, with inference served by one to six NVIDIA DGX Spark nodes running vLLM.

Core responsibilities are authentication, request/token governance, consolidated usage accounting, Usage Groups, logical-model routing, distributed physical-capacity admission and metadata-only enterprise observability.

Raw prompts, source code, generated outputs, bearer tokens and API secrets must never be persisted or added to logs/spans by default.

## Non-negotiable engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code under `src/`; tests/tooling under `tests/`; Docker under `docker/`; docs under `docs/`.
- Work directly on `main` unless the project owner says otherwise.
- PostgreSQL is durable truth.
- Redis is shared runtime L2 / coordination when enabled; local RAM remains configuration L1.
- Preserve SSE streaming and cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases; DGX/provider identifiers stay internal.
- Capacity claims require benchmark evidence.
- GPU/DCGM telemetry is observational unless benchmarks justify scheduling use.
- Every meaningful increment updates focused docs, project status, development log and roadmap where status changes.
- Never call work DONE merely because it was committed; require relevant green CI/integration evidence.

## Current validated baseline

Last reviewed: **2026-09-15**.

Current complete product/UI checkpoint:

```text
426c545e841865406615998ca50b28a45c40e6f4
feat: manage output token budgets in admin
CI 34988084106 — SUCCESS
```

Runtime/distributed quota checkpoint immediately below it:

```text
887ebfac98389c0115eaf9c102a60133ede745ff
test: validate output token budgets end to end
CI         34987407172 — SUCCESS
Full Stack 34987407169 — SUCCESS
```

`426c545e...` changes only React/Admin client code and Playwright coverage relative to `887ebfac...`, so Full Stack `34987407169` is the canonical runtime evidence for the current product checkpoint.

The Full Stack run proves Redis runtime sync, OTLP/Tempo, shared request-rate counters, distributed DGX capacity leases, transactional-outbox outage replay and the new distributed output-token budget behavior.

## Current runtime topology

```text
PostgreSQL = durable configuration/history + transactional runtime-state outbox
Redis      = distributed L2 snapshots/events + shared rate/capacity/token-budget coordination
local RAM  = per-replica request-path configuration L1
```

Ordinary credential/route/policy configuration lookup is DB-free after startup/runtime publication.

### Transactional runtime publication

Redis-enabled runtime mutations for Node/Model/Deployment/Credential/RatePolicy write a `runtime_state_outbox` row in the same PostgreSQL transaction. The ordered advisory-lock worker retries until acknowledged Redis persistence/version/pubsub succeeds, applies the acknowledged event to the publishing replica's own L1 and only then marks the row processed.

Pending outbox rows are never retention-deleted. `GET /api/admin/runtime-sync` exposes backlog/retry diagnostics.

Read `docs/runtime-cache.md` before changing this path.

## Caller governance: current contract

Request-rate and output-token governance share the same persisted credential/model `RateLimitPolicy` scope in V1.

Output-token budget fields:

```text
OutputTokensPerWindow : int?
MaxOutputTokensPerRequest : int?
```

The output-token budget uses the same `WindowSeconds` as request-rate policy in V1.

### Output-token reservation/settlement

When a budget is configured:

```text
before inference
  -> cap/inject maximum output tokens in the OpenAI-compatible payload
  -> atomically reserve that amount

successful response with observed output usage
  -> refund Reserved - Actual

no upstream attempt
  -> refund full reservation

upstream work with uncertain/missing usage, cancellation or interrupted stream
  -> keep full reservation charged
```

Supported request caps:

```text
Chat Completions: max_completion_tokens / max_tokens
Responses:        max_output_tokens
```

Redis-enabled budget reservation is shared across replicas and **fails closed** if Redis cannot coordinate. Do not add a distributed local fallback for token budgets.

The dedicated Full Stack smoke proves a peer started before policy creation receives the policy in local L1, shared usage settles `7 -> 14`, the next request is rejected, Redis outage returns the dedicated 503 and recovery preserves the shared window.

Read `docs/usage-governance.md` before changing caller governance.

## Error/admission taxonomy

```text
401 invalid_api_key
400 invalid_output_token_limit
429 rate_limit_exceeded
429 token_budget_exceeded
429 capacity_exhausted
503 token_budget_coordination_unavailable
503 capacity_coordination_unavailable
503/abort capacity_lease_lost
503 no_healthy_deployment
```

Keep caller request rate, caller token budget and physical capacity distinct.

## Current development focus / resume point

Output-token budget V1 is DONE and validated. Default next order:

1. **Credential rotation workflow** — next product-hardening increment.
2. Backup/restore + actual restore verification.
3. Optional quota evolution only if required: input/total-token budgets, monetary budgets or independent token-budget periods.
4. Long-term usage rollups / production HA-storage guidance as required.
5. Physical acceptance on real DGX/Copilot/Entra/Cloudflare environment.

Do not implement input/total-token admission without explicit tokenizer/estimation semantics. Do not implement monetary budgets without stable cost/pricing semantics.

A small known quota follow-up is rejection precedence/efficiency: token reservation currently happens before the endpoint request-rate check, but a request rejected before any upstream attempt receives a full token reservation refund. This is safe; change it only deliberately with regression coverage.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute gateway traffic to the credential/Usage Group, not reliably to an individual GitHub user. Never infer users from IP.

## External validation still required

- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted deployment runner;
- Copilot usage-metrics behavior if used for per-user analytics.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. The project owner explicitly chose custom **LlmProxy + vLLM**. Do not redirect toward PAIR unless that decision is reopened.

## Documentation map

```text
QUICKSTART.md / docs/quickstart.md       installation and first smoke test
docs/full-stack.md                       Redis + OTEL/Grafana + outbox operations
docs/project-status.md                   canonical state and exact resume point
docs/runtime-cache.md                    L1/L2 + transactional outbox + distributed coordination
docs/usage-governance.md                 auth, groups, request rate, output-token budgets, usage
docs/data-retention.md                   request/audit/processed-outbox retention
docs/development-log.md                  chronological engineering + validation trace
docs/roadmap.md                          milestone state/backlog
docs/capacity-control.md                 physical admission and Redis capacity leases
docs/benchmarking.md                     benchmark protocol
docs/routing.md                          routing and smart-routing tuning
docs/hardware-telemetry.md               DCGM boundary
docs/github-copilot.md                   Copilot/BYOK and external validation
```

## Handover checklist

Before ending a meaningful development session ensure the repository records: what changed, exact green CI evidence, what remains unverified, architecture decisions, exact next step, external dependencies and focused docs. If `docs/project-status.md` points to a completed next step, fix it before considering the handover clean.
