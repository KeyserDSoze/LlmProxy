# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not old chat history, is the handover mechanism.

## Mandatory resume protocol

Read before changing code:

1. `AGENTS.md`.
2. `docs/project-status.md`.
3. latest entries in `docs/development-log.md`.
4. `docs/roadmap.md`.
5. `CHANGELOG.md` and `docs/versioning.md` for product-visible work.
6. focused docs for the feature being changed.
7. latest `main` and GitHub Actions state.

Source-of-truth precedence:

```text
running code + migrations + tests + successful CI/integration evidence
    > docs/project-status.md
    > docs/development-log.md
    > docs/roadmap.md
    > focused technical documents / CHANGELOG.md
    > old chat context
```

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway/governance boundary for GitHub Copilot and other OpenAI-compatible clients, with inference served by one to six NVIDIA DGX Spark nodes running vLLM.

Core responsibilities are authentication, credential lifecycle, request/token governance, consolidated usage accounting, Usage Groups, logical-model routing, distributed physical-capacity admission, safe runtime maintenance, backup/recovery, product version/release visibility and metadata-only enterprise observability.

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
- Every product/operator-visible change must also follow `docs/versioning.md`: update the appropriate release notes/version metadata and keep the Admin release view aligned.
- Never silently reuse a published product version for different bits.
- Never call work DONE merely because it was committed; require relevant green CI/integration evidence.

## Product version / release contract

Current formal product version:

```text
0.1.0-preview.1
```

Version authority is root `Directory.Build.props`. `src/LlmProxy.Admin/package.json` stays aligned for the bundled Admin application. Runtime identity is exposed by:

```http
GET /healthz
GET /api/admin/product
```

Operators can read the current version/build and patch notes at:

```text
/admin/releases
```

`CHANGELOG.md` is the human-readable product history. `src/LlmProxy.Api/Product/ProductReleaseCatalog.cs` is the release catalog rendered by the runtime Admin API/UI. Keep them semantically aligned when creating a new version. Engineering detail and CI history belong in `docs/development-log.md` rather than release notes.

Read `docs/versioning.md` before changing version/release behavior.

## Current validated baseline

Last reviewed: **2026-09-16**.

Current product/versioning implementation checkpoint:

```text
4b1f42daf8acb449526658b3a189535d7674c4b3
feat: add product version and release notes UI
Full Stack 35063309417 — SUCCESS
```

Final standard-CI checkpoint for the same product bits plus deterministic maintenance Playwright assertion:

```text
ee9ac0d17a95b68a79a464dc430e5c8427c9ded9
test: make maintenance status assertions exact
CI 35063494349 — SUCCESS
```

The Full Stack gate proves Redis synchronization, transactional-outbox recovery, distributed output-token budgets, cross-replica credential rotation and safe node maintenance. The standard CI gate proves backend/unit/benchmark, React/Vitest/Playwright, production image build, ordinary Docker/PostgreSQL integration, backup/restore and PowerShell recovery paths.

## Current runtime topology

```text
PostgreSQL = durable configuration/history + transactional runtime-state outbox
Redis      = distributed L2 snapshots/events + shared rate/capacity/token-budget/maintenance coordination
local RAM  = per-replica request-path configuration L1
```

Ordinary credential/route/policy configuration lookup is DB-free after startup/runtime publication.

### Transactional runtime publication

Redis-enabled runtime mutations for Node/Model/Deployment/Credential/RatePolicy write a `runtime_state_outbox` row in the same PostgreSQL transaction. The ordered advisory-lock worker retries until acknowledged Redis persistence/version/pubsub succeeds, applies the acknowledged event to the publishing replica's own L1 and only then marks the row processed.

Pending outbox rows are never retention-deleted. `GET /api/admin/runtime-sync` exposes backlog/retry diagnostics.

Read `docs/runtime-cache.md` before changing this path.

## Safe node/runtime maintenance: current contract

Model/runtime upgrade sequencing is **DONE and Full-Stack validated** for the repository-supported flow.

Supported operator path:

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Drain establishes an admission block before persisting `Draining`. In Redis mode the block is checked inside atomic distributed capacity admission, closing the cross-replica L1 propagation race without adding a separate Redis lookup. Existing in-flight work is allowed to finish; resume is refused until global active work reaches zero. Resume then requires health, `/v1/models` and one-token warm-up inference for enabled provider models before publishing `Healthy` and clearing the admission block.

The old `/api/admin/nodes/{id}/drain` is deprecated and must not be reintroduced as an unsafe bypass. Preserve the rule that in-flight streaming work is never failed over after downstream bytes have started.

Read `docs/operations.md` and `docs/capacity-control.md` before changing maintenance/capacity behavior.

## Credential lifecycle: current contract

Credential creation and rotation never persist raw secrets. PostgreSQL/Redis/runtime state contain HMAC hashes and safe metadata only.

Rotation is an **in-place hard cutover**: the credential keeps the same identity, group and policy/history linkage while `KeyPrefix` + `KeyHash` are replaced. The replacement raw secret is returned once, the old secret becomes invalid after runtime convergence, the response is `Cache-Control: no-store`, and audit contains safe prefix metadata only.

Read `docs/usage-governance.md` before changing credential lifecycle or caller governance.

## Caller governance: current contract

Request-rate and output-token governance share the same persisted credential/model `RateLimitPolicy` scope in V1.

```text
OutputTokensPerWindow : int?
MaxOutputTokensPerRequest : int?
```

The output-token budget uses the same `WindowSeconds` as request-rate policy. Reservation occurs before inference; successful known usage refunds unused reservation; no-upstream-attempt paths refund fully; uncertain usage after upstream work keeps the full reservation charged. Redis-enabled token-budget admission is shared across replicas and fails closed if Redis cannot coordinate.

## Backup / restore: current contract

Backup/restore is **DONE and CI-validated** for the repository-supported Compose path.

- PostgreSQL is the durable recovery authority.
- Backup artifact is `pg_dump` custom format plus SHA-256 sidecar and non-secret metadata.
- Raw API secrets are not in PostgreSQL and cannot be recovered from the dump.
- `Authentication__ApiKeyPepper` and other deployment secrets are external recovery dependencies and must be preserved separately.
- Restore is explicit/destructive: stop writers, recreate the target DB, restore with `pg_restore`, then rebuild runtime state.
- Redis is not restored as authoritative state. LlmProxy-prefixed runtime keys are cleared when the selected Compose stack owns Redis; startup republishes snapshots from restored PostgreSQL.
- Current request-rate/token windows and capacity leases may reset during DR.
- Linux Bash and PowerShell operator scripts are present under `docker/scripts/`.

Read `docs/backup-restore.md` before changing recovery behavior.

## Error/admission taxonomy

```text
401 invalid_api_key
400 invalid_output_token_limit
409 node_disabled / node_not_draining / node_still_draining
429 rate_limit_exceeded
429 token_budget_exceeded
429 capacity_exhausted
503 token_budget_coordination_unavailable
503 capacity_coordination_unavailable
503 maintenance_coordination_unavailable
503 node_validation_failed
503/abort capacity_lease_lost
503 no_healthy_deployment
```

Keep caller request rate, caller token budget, physical capacity and maintenance coordination distinct.

## Current development focus / resume point

Output-token budget V1, credential rotation, repository backup/restore, safe model/runtime maintenance, and product version/release visibility are DONE and validated. Default next order is requirements-driven rather than adding speculative governance semantics:

1. harden release/build automation so image build identity and version/tag consistency are mechanically checked;
2. optional quota evolution only if requirements call for input/total-token budgets, monetary budgets or independent token-budget periods;
3. long-term usage rollups if reporting must outlive raw retention;
4. customer-specific Redis/observability HA and production storage/backup scheduling guidance;
5. physical acceptance on real DGX/Copilot/Entra/Cloudflare environment.

Do not implement input/total-token admission without explicit tokenizer/estimation semantics. Do not implement monetary budgets without stable cost/pricing semantics.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute gateway traffic to the credential/Usage Group, not reliably to an individual GitHub user. Never infer users from IP.

## External validation still required

- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted deployment runner;
- customer production backup destination/retention/encryption and native Windows/Docker Desktop acceptance where used;
- Copilot usage-metrics behavior if used for per-user analytics.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. The project owner explicitly chose custom **LlmProxy + vLLM**. Do not redirect toward PAIR unless that decision is reopened.

## Documentation map

```text
CHANGELOG.md                              product-visible release history
QUICKSTART.md / docs/quickstart.md       installation and first smoke test
docs/versioning.md                       SemVer/release-note/build identity rules
docs/full-stack.md                       Redis + OTEL/Grafana + outbox operations
docs/project-status.md                   canonical state and exact resume point
docs/runtime-cache.md                    L1/L2 + transactional outbox + distributed coordination
docs/usage-governance.md                 auth, credential lifecycle, groups, rate/token governance, usage
docs/backup-restore.md                   PostgreSQL backup/restore, secrets boundary and restore proof
docs/operations.md                       health, safe maintenance and admin audit
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

Before ending a meaningful development session ensure the repository records: what changed, exact green CI evidence, what remains unverified, architecture decisions, exact next step, external dependencies, focused docs, and any product-visible release-note/version impact. If `docs/project-status.md` points to a completed next step or the Admin release notes omit a shipped product change, fix it before considering the handover clean.
