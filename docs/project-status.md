# Project status / handover snapshot

Last reviewed: **2026-09-16**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal product version:

```text
0.1.0-preview.1
```

Current complete repository checkpoint:

```text
product implementation 4b1f42daf8acb449526658b3a189535d7674c4b3
final test fix         ee9ac0d17a95b68a79a464dc430e5c8427c9ded9
CI                     35063494349 SUCCESS
Full Stack              35063309417 SUCCESS
```

CI proves backend build/unit/benchmark, React/Vitest/Playwright including release-note rendering and maintenance controls, production image build, ordinary Docker/PostgreSQL integration, backup/restore and PowerShell recovery paths. Full Stack proves base Redis/observability operation, transactional-outbox recovery, distributed output-token budgets, cross-replica credential rotation and safe node maintenance.

## Product versioning / patch notes — DONE / VALIDATED

LlmProxy now has an explicit SemVer identity. The first formal baseline is `0.1.0-preview.1`; older historical versions were intentionally not fabricated.

Version authority and product surfaces:

```text
Directory.Build.props                  compiled product version
GET /healthz                           includes current version
GET /api/admin/product                 product/release object
/admin/releases                        operator-visible release notes
CHANGELOG.md                            human-readable product history
docs/versioning.md                     release/versioning rules
```

The Admin UI displays a persistent version/release-notes launcher and reads runtime version information from the backend rather than hard-coding the displayed product version. The release object includes current version, channel, release date, optional build revision/date and versioned `Added / Changed / Fixed / Security` sections.

Future product/operator-visible increments must follow the versioning checklist: choose/bump SemVer as appropriate, update `CHANGELOG.md`, runtime release catalog and Admin release view, add tests and record exact green evidence. Engineering-only detail remains in `docs/development-log.md`.

## Core product scope

LlmProxy is Agic's enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication + credential lifecycle
2. request-rate + output-token governance
3. consolidated usage accounting + Usage Groups
4. logical-model routing across DGX/vLLM
5. distributed multi-instance coordination with Redis
6. physical-capacity admission and safe runtime maintenance
7. backup/recovery of durable application state
8. metadata-only observability and operational controls
9. product version/build identity + operator release notes
```

## Current request path

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC-hashed bearer credential from local L1
  -> credential + UsageGroup + caller policy from local L1
  -> output-token budget reservation when configured
       local atomic store in Redis-disabled single instance
       Redis atomic shared store in distributed mode
  -> request-rate admission
  -> logical model -> deployment/node catalog from local L1
  -> smart routing
  -> Redis/local physical-capacity admission
       Redis admission also enforces distributed maintenance block
  -> vLLM
  -> output-token budget settlement
  -> metadata-only request metric + OTEL telemetry
```

Ordinary inference configuration lookup remains DB-free after startup/runtime publication.

## Safe model/runtime upgrade + draining — DONE / VALIDATED

The supported maintenance protocol is:

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Semantics:

- drain first establishes an admission block, then persists `Draining`;
- in Redis mode the marker is enforced inside distributed capacity admission, preventing stale peer L1 state from admitting new work;
- existing in-flight requests/streams are allowed to finish normally;
- global active capacity leases expose remaining in-flight work;
- resume is rejected until active work reaches zero;
- resume probes `/health` and `/v1/models` and runs a one-token Chat Completions warm-up for each enabled provider model on that node;
- failed validation keeps the node `Draining` and audited;
- successful validation persists `Healthy` and clears the distributed admission marker;
- retry can repair a validated node whose Redis maintenance marker could not yet be cleared;
- the legacy direct drain endpoint is deprecated and must not be used as a maintenance bypass.

Full Stack `35063309417` proves cross-replica pre-blocking while a streaming request is active, refusal of premature resume, failed-runtime validation, recovery/warm-up and safe return to routing.

## Credential lifecycle — DONE / VALIDATED

Secrets are generated and shown once. PostgreSQL stores HMAC-SHA256 hashes and safe metadata; Redis/runtime payloads also contain only hashes and safe credential state.

`POST /api/admin/api-credentials/{id}/rotate` performs an in-place hard cutover: same credential identity/group/policy/history linkage, new prefix/HMAC, replacement secret shown once, and old secret invalid after runtime convergence. The response uses `Cache-Control: no-store`; audit contains no raw secret/HMAC.

## Usage Groups / request-rate governance — DONE / VALIDATED

- persisted Usage Groups and primary group per credential;
- request-time UsageGroup snapshot in metrics;
- persisted credential/model request-rate policies;
- fixed-window request admission with calculated `Retry-After`;
- Redis shared request counters in distributed mode;
- distinct `429 rate_limit_exceeded`;
- usage aggregation/UI by group, credential and logical model.

## Output-token budgets — V1 DONE / VALIDATED

Budget configuration lives on the existing credential/model `RateLimitPolicy`:

```text
OutputTokensPerWindow : int?
MaxOutputTokensPerRequest : int?
```

V1 shares policy scope and `WindowSeconds` with request-rate admission.

- reserve output capacity before inference, preventing concurrent oversubscription;
- cap/inject Chat `max_completion_tokens` / `max_tokens` and Responses `max_output_tokens`;
- successful known usage refunds unused reservation;
- no upstream attempt refunds fully;
- uncertain usage after upstream work keeps full reservation charged;
- `429 token_budget_exceeded` when budget cannot admit the reservation;
- Redis-enabled coordination fails closed as `503 token_budget_coordination_unavailable`;
- quota definitions propagate through transactional runtime-state outbox + peer L1;
- Admin API/UI supports visibility, Apply and Clear.

## Backup / restore — DONE / VALIDATED

PostgreSQL is durable recovery authority. Redis is runtime/coordination state and is rebuilt after restore.

Supported operators:

```text
docker/scripts/postgres-backup.sh
docker/scripts/postgres-restore.sh
docker/scripts/postgres-backup.ps1
docker/scripts/postgres-restore.ps1
```

Backup creates a PostgreSQL custom-format archive, SHA-256 checksum and non-secret metadata. Restore validates the archive/checksum, stops the Compose-managed gateway, recreates the target database, restores with `pg_restore`, clears only LlmProxy-prefixed Redis runtime state when appropriate and restarts the gateway.

Raw API secrets are never in PostgreSQL. `Authentication__ApiKeyPepper` and external deployment secrets must be preserved separately in the approved secret manager.

## Routing / physical capacity — DONE FOR CURRENT MVP

- logical models hide provider/DGX topology;
- weighted least loaded / round robin / weighted round robin;
- health hysteresis and path-prefixed service roots;
- pre-response-only failover;
- vLLM pressure + EWMA feedback;
- benchmark-derived Capacity Profiles;
- atomic deployment + physical-node admission;
- Redis capacity leases across replicas;
- fail-closed capacity acquisition;
- active-inference cancellation before unsafe distributed lease expiry;
- distributed maintenance marker enforced inside capacity admission.

## Distributed runtime state / transactional outbox — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + transactional runtime-state outbox
Redis      = distributed L2 snapshots/events + shared counters/leases/budget/maintenance state
local RAM  = per-gateway request-path L1
```

Runtime Node/Model/Deployment/Credential/RatePolicy mutations and outbox records commit in the same PostgreSQL transaction. One advisory-lock worker publishes globally ordered events to Redis, retries failures, applies acknowledged events to its own L1 and marks rows processed only after durable Redis acknowledgement.

`GET /api/admin/runtime-sync` exposes Redis status and outbox backlog/retry diagnostics. Processed outbox rows default to 30-day retention; pending rows are never retention-deleted.

## Retention / observability

Defaults:

```text
request_metrics              90 days
audit_events                 365 days
processed runtime outbox      30 days
cleanup interval              24 hours
batch size                    5000
```

Full stack includes PostgreSQL, Redis, OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana. Telemetry is metadata-only; prompts/source/generated output/secrets are excluded.

## Current error taxonomy

```text
401 invalid_api_key
400 invalid_output_token_limit
409 revoked credential rotation
409 node_disabled
409 node_not_draining
409 node_still_draining
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

## Current development focus

The originally planned production-hardening chain through safe runtime maintenance is complete. The next order is:

1. **release/build automation hardening**: mechanically tie image build identity to source SHA and validate version/tag consistency before tagged publication;
2. quota evolution only if product requirements need input/total-token budgets, monetary budgets or a token-budget period independent from request-rate `WindowSeconds`;
3. optional long-term usage rollups;
4. customer-specific Redis/observability HA/storage and scheduled backup guidance;
5. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available.

Quota expansion remains requirements-driven: input/total-token admission needs tokenizer/estimation semantics; monetary budgets need stable pricing/accounting rules.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/group but cannot infer the individual GitHub user. Never infer identity from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark runs;
- representative multi-DGX coding workload;
- real Entra app/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- customer production backup destination/encryption/retention and native Windows/Docker Desktop acceptance where applicable;
- Copilot usage-metrics/custom-model reporting if per-user analytics are required.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat product version `0.1.0-preview.1`, implementation `4b1f42daf8acb449526658b3a189535d7674c4b3`, standard CI checkpoint `ee9ac0d17a95b68a79a464dc430e5c8427c9ded9` / `35063494349`, and Full Stack `35063309417` as the validated baseline;
4. if continuing default hardening, start release/build identity + tag consistency automation;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free configuration lookup and pre-response-only failover;
6. update release notes/version metadata for product-visible changes and update engineering docs/evidence after every meaningful increment.
