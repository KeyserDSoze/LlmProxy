# AGENTS.md

This file is the mandatory entry point for humans and AI coding agents working on **LlmProxy**. The repository, not chat history, is the handover mechanism.

## Mandatory resume protocol

Read, in order:

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
    > focused technical docs / CHANGELOG.md
    > old chat context
```

## Product goal

LlmProxy is Agic's productizable on-premises AI gateway/governance boundary for GitHub Copilot and other OpenAI-compatible clients, targeting one to six NVIDIA DGX Spark nodes running vLLM.

Core responsibilities: authentication, credential lifecycle, request/token governance, Usage Groups and historical usage accounting, logical-model routing, distributed physical-capacity admission, safe runtime maintenance, backup/recovery, version/release visibility, supply-chain identity, and metadata-only enterprise observability.

Raw prompts, source code, generated outputs, bearer tokens and API secrets must never be persisted or added to logs/spans by default.

## Engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + GitHub Actions + GHCR.
- Product code: `src/`; tests/tooling: `tests/`; Docker: `docker/`; docs: `docs/`.
- Work directly on `main` unless the project owner says otherwise.
- PostgreSQL is durable truth.
- Redis is shared runtime L2/coordination when enabled; local RAM remains request-path configuration L1.
- Preserve SSE streaming/cancellation end-to-end.
- Never fail over after downstream bytes/tokens have started.
- Public model names are logical aliases; provider/DGX identifiers stay internal.
- Capacity claims require benchmark evidence.
- GPU/DCGM telemetry is observational unless benchmarks justify scheduling use.
- Every meaningful increment updates focused docs, project status, development log and roadmap where status changes.
- Every product/operator-visible change follows `docs/versioning.md` and updates version/release notes when required.
- Never silently reuse a tagged/published exact version for different product bits.
- Never call work DONE because it was committed; require relevant green CI/integration evidence.

## Current product version and validated baseline

Current formal version:

```text
0.2.0-preview.3
```

Current validated product/release checkpoint:

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

No inference/runtime path changed in `0.2.0-preview.3`; that Full Stack evidence remains the relevant distributed-runtime proof.

Runtime identity is exposed by:

```http
GET /healthz
GET /api/admin/product
```

Operators read version/build/patch notes at `/admin/releases`. `CHANGELOG.md` is human-readable product history; `ProductReleaseCatalog` is the runtime release catalog. Keep them aligned.

## Runtime topology

```text
PostgreSQL = durable configuration/history + transactional runtime-state outbox + usage rollups
Redis      = distributed L2 snapshots/events + shared request/capacity/token-budget/maintenance coordination
local RAM  = per-replica request-path configuration L1
```

Ordinary credential/route/policy configuration lookup is DB-free after startup/runtime publication.

### Transactional runtime publication

Runtime Node/Model/Deployment/Credential/RatePolicy mutations and `runtime_state_outbox` rows commit in the same PostgreSQL transaction. The ordered advisory-lock worker retries Redis publication, applies acknowledged state to the publisher L1, then marks the row processed. Pending outbox rows are never retention-deleted.

Read `docs/runtime-cache.md` before changing this path.

## Safe node/runtime maintenance contract

Supported operator path:

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Drain establishes an admission block before persisting `Draining`. Redis mode checks that block inside atomic distributed capacity admission, closing stale-peer admission races without another request-path lookup. Existing work drains normally. Resume requires zero global active work plus `/health`, `/v1/models` and one-token warm-up validation before returning the node to routing.

The legacy `/api/admin/nodes/{id}/drain` must remain deprecated as an unsafe bypass.

Read `docs/operations.md` and `docs/capacity-control.md` before changing maintenance/capacity behavior.

## Credential and caller-governance contract

Credentials persist only HMAC hashes and safe metadata. Rotation is an in-place hard cutover: same credential identity/group/policy/history linkage, new prefix/hash, one-time replacement secret, `Cache-Control: no-store`, safe audit only.

Request-rate and output-token governance share the persisted credential/model `RateLimitPolicy` scope in V1. Redis-enabled distributed token/capacity admission fails closed when coordination is unavailable.

Do not add input/total-token admission without explicit tokenizer/estimation semantics. Do not add monetary budgets without stable pricing/accounting semantics.

Read `docs/usage-governance.md` before changing caller governance.

## Historical usage / retention contract

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
```

Before expired raw request metrics are deleted, complete UTC days are aggregated into PostgreSQL rollups keyed by day + credential + Usage Group + logical model. Compaction is transactional and serialized across replicas with a PostgreSQL advisory transaction lock. Reporting combines rollups with newer raw metrics without double counting.

Read `docs/data-retention.md` before changing retention/reporting semantics.

## Backup / restore contract

PostgreSQL is the recovery authority; Redis is rebuildable runtime state. Supported Bash and PowerShell backup/restore scripts live under `docker/scripts/`. Backup uses custom-format `pg_dump` + SHA-256 + non-secret metadata. Restore is explicit/destructive and rebuilds runtime state from PostgreSQL.

`Authentication__ApiKeyPepper` and deployment secrets are external recovery dependencies and must be preserved separately.

Read `docs/backup-restore.md` before changing recovery behavior.

## Release/build/supply-chain contract

Version authority is `Directory.Build.props`; Admin `package.json` stays aligned. CI validates SemVer, changelog presence and deliberately invalid release candidates. Production images carry OCI version/revision/created labels plus `LLMPROXY_BUILD_SHA` and `LLMPROXY_BUILD_DATE`.

Publishing behavior:

```text
validated main SHA -> main + sha-<7>
validated Git tag vX.Y.Z -> exact X.Y.Z + sha-<7>
stable tag only -> optional major.minor alias
prerelease tag -> never updates a stable-looking alias
```

Every container publication, including ordinary `main` publication and an exact Git-tag publication, must query GitHub Actions and prove that the selected source SHA already has a successful `CI` run produced by a push to `main`. The reusable `docker/scripts/validate-release-main-ci.sh` guard enforces workflow name, push event, `main` branch, exact SHA and `success` conclusion. A `workflow_run` publication additionally requires the API-selected run ID to equal the run that triggered publication. This gate runs before GHCR login.

Registry-native supply-chain evidence remains mandatory:

- Buildx publishes an SPDX SBOM OCI attestation;
- Buildx publishes SLSA/BuildKit provenance (`mode=max`);
- publish records the immutable image digest;
- post-push verification resolves the pushed digest from GHCR, reads the OCI image index, follows `attestation-manifest` descriptors and validates `application/vnd.in-toto+json` layers;
- verification requires both predicate types `https://spdx.dev/Document` and `https://slsa.dev/provenance/...`;
- an Actions `release-manifest.json` artifact records image, digest, version, source SHA, build timestamp, validating CI run ID and verified attestation descriptors.

Validated `0.2.0-preview.3` release evidence:

```text
validating CI         35083646699
Publish GHCR          35084132389
image digest          sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
attestation manifest  sha256:6d60bd6cb26cce447e403081ae1aa6129920f2716a0a1ccfb579b196054997a9
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
release artifact      10441770750
```

No immutable Git tag or GitHub Release has been created. Creating one is an explicit product-owner release action, not a prerequisite for considering the repository release gate implemented and validated.

Read `docs/versioning.md` before release changes.

## Current development focus / resume point

Repository-supported MVP hardening is complete through historical usage rollups, registry-verified SPDX/SLSA attestations and the shared main/tag publication-source gate. Default next order:

1. add customer-specific Redis/observability HA, production storage and scheduled backup guidance when deployment topology is known;
2. run physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available;
3. evolve quota semantics only when explicit requirements define tokenizer/pricing behavior;
4. create an immutable Git tag/GitHub Release only when the project owner explicitly wants to publish a distributable release.

Do not invent per-user identity from a shared GitHub Copilot BYOK credential or from IP addresses.

## External validation still required

- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted deployment runner;
- customer production backup destination/retention/encryption and native Windows/Docker Desktop acceptance where used;
- Copilot usage-metrics behavior if per-user analytics are required.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router was evaluated and rejected for the current direction. Continue custom **LlmProxy + vLLM** unless that decision is explicitly reopened.

## Documentation map

```text
CHANGELOG.md                        product-visible release history
docs/project-status.md             canonical state and exact resume point
docs/development-log.md            chronological engineering + validation trace
docs/roadmap.md                    milestone state/backlog
docs/versioning.md                 SemVer/release/build/supply-chain rules
docs/data-retention.md             raw metrics + daily rollups + audit/outbox retention
docs/usage-governance.md           auth, credential lifecycle, groups, quotas, usage
docs/runtime-cache.md              L1/L2 + transactional outbox
docs/operations.md                 health, safe maintenance, build/release identity, audit
docs/backup-restore.md             PostgreSQL recovery contract
docs/full-stack.md                 Redis + observability bundle
docs/capacity-control.md           physical admission + capacity leases
docs/benchmarking.md               benchmark protocol
docs/routing.md                    routing and tuning
docs/hardware-telemetry.md         DGX/DCGM boundary
docs/github-copilot.md             Copilot/BYOK limitations and external validation
```

## Handover checklist

Before ending a meaningful development session, record what changed, exact green CI evidence, what remains unverified, architecture decisions, exact next step, external dependencies, focused docs, and any version/release-note impact.