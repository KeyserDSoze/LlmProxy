# Project status / handover snapshot

Last reviewed: **2026-09-16**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal product version:

```text
0.2.0-preview.3
```

Validated product/release checkpoint:

```text
implementation   e9c8805e8473d3ad4df118d6a623ccef08723761
CI               35083646699 SUCCESS
Publish GHCR     35084132389 SUCCESS
image digest     sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
release artifact 10441770750
```

The latest distributed-runtime Full Stack checkpoint remains:

```text
Full Stack 35075387186 SUCCESS
```

`0.2.0-preview.3` changes release validation/supply-chain behavior, not inference/runtime semantics, so the existing Full Stack remains the relevant Redis/runtime proof.

The standard CI proves backend build/unit/benchmark, React/Vitest/Playwright, release-source guard fixtures, version metadata validation, production image identity, Docker/PostgreSQL integration, caller governance, route-catalog outage behavior, retention/usage-rollup compaction, and Bash/PowerShell restore paths.

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

Current release history starts at `0.1.0-preview.1`; `0.2.0-preview.1` adds historical usage rollups; `0.2.0-preview.2` adds GHCR SBOM/provenance verification; `0.2.0-preview.3` ties all container publication, including exact SemVer tags, to previously successful `main` CI for the same source SHA. Older versions were intentionally not fabricated.

## Supply-chain release evidence — DONE / VALIDATED FOR REPOSITORY PATH

Production publication now:

1. resolves one exact source SHA;
2. queries the GitHub Actions API before GHCR login;
3. requires a `CI` workflow run with event `push`, branch `main`, exact source SHA and conclusion `success`;
4. for `workflow_run` publication, requires the API-selected CI run ID to equal the triggering CI run ID;
5. for exact `vX.Y.Z` tag publication, also requires tag version == compiled version;
6. builds with explicit product version, source SHA and UTC build time;
7. publishes `main` + `sha-<7>` for main builds, or exact SemVer + `sha-<7>` for a matching validated tag;
8. emits an SPDX SBOM OCI attestation;
9. emits SLSA/BuildKit provenance with `mode=max`;
10. records the immutable registry digest returned by Buildx;
11. reads the pushed OCI index back from GHCR and verifies both SPDX and SLSA in-toto predicates;
12. uploads `release-manifest.json` with image/digest/version/source/build time, validating CI run ID and attestation descriptors.

Validated repository-path evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version               0.2.0-preview.3
source                e9c8805e8473d3ad4df118d6a623ccef08723761
validating CI         35083646699
Publish GHCR          35084132389
image digest          sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
attestation manifest  sha256:6d60bd6cb26cce447e403081ae1aa6129920f2716a0a1ccfb579b196054997a9
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
artifact              10441770750
artifact digest       sha256:5315122e2df9238702655332273e48744a08b895c2fcca25cbe7dcd6ff42d13d
```

CI exercises the reusable source guard with positive and negative fixtures without creating a real immutable tag. Publish `35084132389` proves the same Actions-API guard end-to-end on a real `main` publication before GHCR login, followed by digest/SBOM/provenance verification.

No immutable Git tag or GitHub Release has been created. That remains an explicit product-owner publication action rather than missing repository implementation.

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
10. validated container publication + registry-native SBOM/provenance evidence
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

Node/Model/Deployment/Credential/RatePolicy mutations and outbox rows commit in the same PostgreSQL transaction. The globally ordered advisory-lock worker publishes/retries Redis state, updates its own L1 and marks rows processed only after acknowledged publication. Pending outbox rows are never retention-deleted. `GET /api/admin/runtime-sync` exposes backlog/retry diagnostics.

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

`0.2.0-preview.1` added PostgreSQL daily usage rollups.

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
cleanup interval              24 hours
```

Compaction covers only complete expired UTC days; rollup key is day + credential + Usage Group + logical model; request-time attribution is preserved; aggregate + raw deletion are atomic; a PostgreSQL advisory transaction lock serializes compaction across gateway replicas; reruns are idempotent; reporting merges rollups with newer raw metrics without double counting.

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

Repository-supported MVP hardening is complete through release identity, historical usage rollups, OCI SBOM/provenance verification and source-validated main/tag publication.

Default order from here:

1. customer-specific Redis/observability HA, production storage and scheduled backup guidance when deployment topology is known;
2. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available;
3. quota evolution only when requirements define tokenizer/pricing semantics;
4. create an immutable Git tag/GitHub Release only when the project owner explicitly wants a distributable release.

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
3. treat version `0.2.0-preview.3`, implementation `e9c8805e8473d3ad4df118d6a623ccef08723761`, CI `35083646699`, Publish `35084132389`, image digest `sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec` and latest runtime Full Stack `35075387186` as the validated baseline;
4. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free configuration lookup, rollup/raw no-double-counting and pre-response-only failover;
5. preserve the pre-GHCR Actions-API source-validation gate plus post-push registry verification of both SPDX and SLSA attestations;
6. for new product/operator-visible behavior, bump version/release notes according to `docs/versioning.md`;
7. update engineering docs/evidence after every meaningful increment.