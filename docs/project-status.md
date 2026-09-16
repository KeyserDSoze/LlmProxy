# Project status / handover snapshot

Last reviewed: **2026-09-16**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal product version:

```text
0.2.0-preview.1
```

Validated product checkpoint:

```text
implementation  5d66c7dcdae42955c6e26849aba84bed4787ff00
CI              35075387110 SUCCESS
Full Stack      35075387186 SUCCESS
Publish GHCR    35075788954 SUCCESS
```

Release/build identity hardening included in the same baseline was introduced at:

```text
c37479bb474d44f9e36726bebba74cdf38e5661e
CI           35064353402 SUCCESS
Publish GHCR 35064707488 SUCCESS
```

The standard CI proves backend build/unit/benchmark, React/Vitest/Playwright, version metadata validation, production image identity, Docker/PostgreSQL integration, caller governance, route-catalog outage behavior, retention/usage-rollup compaction, and Bash/PowerShell restore paths. Full Stack proves Redis/observability operation, transactional-outbox recovery, distributed output-token budgets, credential rotation and safe node maintenance.

## Product/versioning — DONE / VALIDATED

Version authority and product surfaces:

```text
Directory.Build.props                  compiled version
src/LlmProxy.Admin/package.json        bundled Admin version
GET /healthz                           runtime version
GET /api/admin/product                 product/release/build object
/admin/releases                        operator-visible patch notes
CHANGELOG.md                            human-readable product history
docs/versioning.md                     release/version/build rules
```

Current release history starts at `0.1.0-preview.1`; `0.2.0-preview.1` adds historical usage rollups. Older versions were intentionally not fabricated.

Production images include OCI version/revision/created labels plus runtime build SHA/date. CI validates current SemVer/changelog alignment and an intentionally mismatched candidate tag. Main publishing produces `main` + `sha-<7>`; exact version tags are produced only from a matching Git tag.

## Core product scope

LlmProxy is Agic's enterprise inference-governance boundary:

```text
1. inference authentication + credential lifecycle
2. request-rate + output-token governance
3. Usage Groups + recent/historical usage accounting
4. logical-model routing across DGX/vLLM
5. distributed multi-instance coordination with Redis
6. physical-capacity admission + safe runtime maintenance
7. backup/recovery of durable application state
8. metadata-only observability and audit
9. product version/build identity + operator release notes
```

## Current request path

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC-hashed bearer credential from local L1
  -> credential + UsageGroup + caller policy from local L1
  -> output-token budget reservation when configured
  -> request-rate admission
  -> logical model -> deployment/node catalog from local L1
  -> smart routing
  -> Redis/local physical-capacity admission
       Redis admission also enforces maintenance block
  -> vLLM
  -> output-token budget settlement
  -> metadata-only request metric + OTEL telemetry
```

Ordinary inference configuration lookup remains DB-free after startup/runtime publication.

## Distributed runtime state — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + runtime-state outbox
Redis      = distributed L2 + request/capacity/token-budget/maintenance coordination
local RAM  = per-gateway request-path L1
```

Node/Model/Deployment/Credential/RatePolicy mutations and outbox rows commit in the same PostgreSQL transaction. The globally ordered advisory-lock worker publishes/retries Redis state, updates its own L1 and marks rows processed only after acknowledged publication.

Pending outbox rows are never retention-deleted. `GET /api/admin/runtime-sync` exposes backlog/retry diagnostics.

## Safe model/runtime maintenance — DONE / VALIDATED

Supported API:

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Drain pre-blocks new admission before persisting `Draining`; Redis mode checks the marker inside atomic capacity admission. Existing streams finish. Resume is rejected until active global leases reach zero, then requires `/health`, `/v1/models` and one-token warm-up validation before returning to `Healthy`.

The legacy direct drain endpoint remains deprecated as an unsafe bypass.

## Credential lifecycle — DONE / VALIDATED

Credentials persist HMAC-SHA256 hashes and safe metadata only. Rotation performs an in-place hard cutover: same credential identity/group/policy/history linkage, new prefix/hash, one-time raw replacement secret, `Cache-Control: no-store`, safe audit, and cross-replica old-hash removal.

## Caller governance — V1 DONE / VALIDATED

- persisted credential/model request-rate policies;
- fixed-window request admission + `Retry-After` + `429 rate_limit_exceeded`;
- shared Redis request counters;
- output-token budget using `OutputTokensPerWindow` + `MaxOutputTokensPerRequest`;
- pre-inference reservation and Chat/Responses cap injection;
- known-usage settlement/refund;
- conservative full charge for uncertain post-upstream usage;
- shared Redis token windows and fail-closed coordination;
- Admin API/UI Apply/Clear and audit.

Input/total-token admission remains requirements-driven because tokenizer/estimation semantics must be explicit. Monetary budgets remain requirements-driven because pricing/accounting semantics must be stable.

## Usage Groups + historical reporting — DONE / VALIDATED

`0.2.0-preview.1` adds PostgreSQL daily usage rollups.

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
cleanup interval              24 hours
```

Compaction contract:

- only complete expired UTC calendar days are compacted;
- rollup key: day + credential + Usage Group + logical model;
- credential/group attribution uses the request-time snapshot;
- aggregate + raw deletion commit atomically for each compaction transaction;
- PostgreSQL advisory transaction lock serializes retention compaction across gateway replicas;
- rerunning cleanup is idempotent;
- reporting combines historical rollups with newer raw metrics without double counting;
- API response exposes raw/rolled-up request counts and whether rollups contributed;
- Admin Usage & Governance supports up to 730 days and discloses historical-rollup usage.

CI `35075387110` proves the old raw record is rolled up, removed, still visible in usage reporting, and not duplicated by a second cleanup.

## Backup / restore — DONE / VALIDATED

PostgreSQL is durable recovery authority; Redis is rebuildable runtime state. Bash and PowerShell operators create/restore a custom-format PostgreSQL archive with SHA-256 and non-secret metadata. Restore is explicit/destructive and rebuilds runtime state from PostgreSQL.

Raw API secrets are not in PostgreSQL. `Authentication__ApiKeyPepper` and deployment secrets must be preserved separately.

## Routing / physical capacity — DONE FOR CURRENT MVP

- logical model aliases;
- weighted least loaded / round robin / weighted round robin;
- health hysteresis and path-prefixed service roots;
- pre-response-only failover;
- vLLM pressure + EWMA feedback;
- benchmark-derived Capacity Profiles;
- atomic deployment + physical-node admission;
- Redis capacity leases and fail-closed lease loss;
- distributed maintenance marker inside capacity admission.

## Observability / privacy

Full stack includes PostgreSQL, Redis, OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana. Telemetry is metadata-only. Prompts/source/generated output/API secrets are excluded by default.

## Current error taxonomy

```text
401 invalid_api_key
400 invalid_output_token_limit
409 revoked credential rotation
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

## Current development focus

Repository-supported hardening is complete through release identity and historical usage rollups. Default order from here:

1. supply-chain/release hardening where useful: immutable tagged release workflow, SBOM/provenance/attestation, operator-verifiable image identity;
2. customer-specific Redis/observability HA, production storage and scheduled backup guidance;
3. quota evolution only when requirements define tokenizer/pricing semantics;
4. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/Usage Group, not reliably to an individual GitHub user. Never infer identity from IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark sweeps;
- representative multi-DGX coding workload;
- real Entra app/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- customer production backup destination/encryption/retention and native Windows/Docker Desktop acceptance where applicable;
- Copilot usage metrics/custom-model reporting if per-user analytics are required.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat version `0.2.0-preview.1`, implementation `5d66c7dcdae42955c6e26849aba84bed4787ff00`, CI `35075387110`, Full Stack `35075387186` and Publish `35075788954` as the validated baseline;
4. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free configuration lookup, rollup/raw no-double-counting and pre-response-only failover;
5. for new product/operator-visible behavior, bump version/release notes according to `docs/versioning.md`;
6. update engineering docs/evidence after every meaningful increment.