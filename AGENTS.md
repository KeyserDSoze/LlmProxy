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

Core responsibilities: authentication, credential lifecycle, request/token governance, Usage Groups and historical usage accounting, logical-model routing, distributed physical-capacity admission, safe runtime maintenance, backup/recovery, version/release visibility, Linux production deployability, supply-chain identity, and metadata-only enterprise observability.

Raw prompts, source code, generated outputs, bearer tokens and API secrets must never be persisted or added to logs/spans by default.

## Engineering conventions

- Backend: .NET 10 / ASP.NET Core / C#.
- Frontend: React + TypeScript.
- Database: PostgreSQL via EF Core/Npgsql.
- Deployment: Docker + Docker Compose v2 + GitHub Actions + GHCR.
- Product code: `src/`; tests/tooling: `tests/`; Docker/deploy: `docker/`; docs: `docs/`.
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
0.2.0-preview.4
```

Validated product/release checkpoint:

```text
implementation   58a80a60c2f3a049b279be6bf9583ffa4c1cc088
CI               35095161900 SUCCESS
Full Stack       35088765577 SUCCESS
Publish GHCR     35095620725 SUCCESS
image digest     sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
release artifact 10445034650
```

`0.2.0-preview.4` changes operator/deployment behavior rather than inference semantics. Full Stack `35088765577` is the relevant Redis/OTEL/distributed-runtime proof for the full-stack Compose changes; final CI `35095161900` proves the complete final source including the cross-distribution installer and production deployment validation.

Runtime identity is exposed by:

```http
GET /healthz
GET /api/admin/product
```

Operators read version/build/patch notes at `/admin/releases`. Keep `CHANGELOG.md`, `ProductReleaseCatalog`, Admin package version and compiled version aligned.

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

Drain establishes an admission block before persisting `Draining`. Redis mode checks that block inside atomic distributed capacity admission. Existing work drains normally. Resume requires zero global active work plus `/health`, `/v1/models` and one-token warm-up validation before returning the node to routing.

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

## Linux production deployment contract

The canonical production topology is the Redis-enabled full stack:

```text
LlmProxy + PostgreSQL + Redis
+ OpenTelemetry Collector
+ Prometheus + Tempo + Loki + Grafana
+ optional Cloudflare Tunnel profile
```

Development/minimal Compose paths are not the production deployment contract.

Canonical first-install entry point:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --dgx-url http://<dgx>:8000 \
  --provider-model '<provider-model-id>' \
  --image-tag <validated-tag>
```

`docker/scripts/install-linux.sh`:

- detects `/etc/os-release` and common package managers;
- uses Docker's official repository path for Debian, Ubuntu, Fedora, CentOS and RHEL;
- supports common derivative/other families through `apt`, `dnf`/`yum`, `zypper`, `pacman` or `apk` distribution packages plus a Compose CLI-plugin fallback when needed;
- preserves a working existing Docker Engine + Compose v2 installation;
- prepares `/opt/llmproxy/{runtime,backups}` and a protected `/opt/llmproxy/.env`;
- generates initial PostgreSQL/Redis/API-key/pepper/Grafana secrets without printing them;
- optionally authenticates to GHCR from `GHCR_USER`/`GHCR_TOKEN` without storing the token in `.env`;
- checks DGX `/health` and `/v1/models` unless explicitly skipped for staged provisioning;
- invokes the same canonical `docker/scripts/deploy.sh` used by GitHub Actions.

Do not claim literal automatic package installation on every possible Linux distribution. For an unrecognized host/package manager, preinstall Docker Engine + Compose v2 and rerun with `--skip-docker-install`; the application deployment path remains the same.

Production host state contract:

```text
/opt/llmproxy/.env       operator-owned secrets/config
/opt/llmproxy/runtime/   staged Compose + observability assets
/opt/llmproxy/backups/   backup destination example
```

`deploy.sh` validates production configuration, stages runtime assets out of the transient checkout/runner workspace, validates Compose before changing containers, pulls/starts the full stack, and requires both `/healthz` and `/readyz`.

Cloudflare is optional. A non-empty tunnel token enables the `cloudflare` profile; public-tunnel deployment requires Entra enabled/configured first. Grafana binds loopback by default in the production template.

Read `docs/linux-production-deployment.md` and `docs/deployment.md` before changing production installation/deployment behavior.

## Release/build/supply-chain contract

Version authority is `Directory.Build.props`; Admin `package.json` stays aligned. Every container publication queries GitHub Actions before GHCR login and proves that the selected source SHA already has successful `CI` from a push to `main`. Exact tags additionally require tag version == compiled version.

Registry-native supply-chain evidence remains mandatory:

- Buildx SPDX SBOM OCI attestation;
- SLSA/BuildKit provenance (`mode=max`);
- immutable image digest;
- post-push GHCR OCI index/attestation verification;
- `release-manifest.json` recording image, digest, version, source SHA, build timestamp, validating CI run ID and attestation descriptors.

Validated `0.2.0-preview.4` evidence:

```text
source                 58a80a60c2f3a049b279be6bf9583ffa4c1cc088
validating CI          35095161900
Publish GHCR           35095620725
image digest           sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
attestation manifest   sha256:cd92f248e73e58fca570a687ca0002d10cfc8e5b308e60ce31351454b4933b0b
SBOM predicate         https://spdx.dev/Document
provenance predicate   https://slsa.dev/provenance/v1
release artifact       10445034650
artifact digest        sha256:cb2bf6b6d8d34a545c080b866866d7098cedbab66f66f475aa168caf6a93c977
```

No immutable Git tag or GitHub Release has been created. Creating one is an explicit product-owner release action.

Read `docs/versioning.md` before release changes.

## Current development focus / resume point

Repository-supported MVP hardening and Linux production bootstrap are complete for the current preview. Default next order:

1. run an actual target-host installation using `docs/linux-production-deployment.md` and capture distro/Docker/DGX/Entra/Cloudflare acceptance evidence;
2. calibrate real DGX capacity with intended vLLM models and representative Copilot load;
3. add customer-specific PostgreSQL/Redis/observability HA/storage and backup destination/retention/encryption choices when deployment topology is known;
4. evolve quota semantics only with explicit tokenizer/pricing requirements;
5. create an immutable Git tag/GitHub Release only when the project owner explicitly wants a distributable release.

Do not invent per-user identity from a shared GitHub Copilot BYOK credential or from IP addresses.

## External validation still required

- installer/package behavior on the actual target Linux distribution/version;
- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- real Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted deployment runner permissions/reboot behavior;
- customer backup destination/retention/encryption;
- customer-specific PostgreSQL/Redis/observability HA/storage;
- Copilot usage-metrics behavior if per-user analytics are required.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router was evaluated and rejected for the current direction. Continue custom **LlmProxy + vLLM** unless that decision is explicitly reopened.

## Documentation map

```text
README.md                           product/developer entry point
CHANGELOG.md                        product-visible release history
docs/project-status.md             canonical state and exact resume point
docs/development-log.md            chronological engineering + validation trace
docs/roadmap.md                    milestone state/backlog
docs/linux-production-deployment.md zero-to-running Linux production runbook
docs/deployment.md                 deployment contract/automation summary
docs/versioning.md                 SemVer/release/build/supply-chain rules
docs/operations.md                 health, maintenance, deployment/release operations
docs/data-retention.md             raw metrics + daily rollups + audit/outbox retention
docs/usage-governance.md           auth, credential lifecycle, groups, quotas, usage
docs/runtime-cache.md              L1/L2 + transactional outbox
docs/backup-restore.md             PostgreSQL recovery contract
docs/full-stack.md                 Redis + observability bundle
docs/capacity-control.md           physical admission + capacity leases
docs/benchmarking.md               benchmark protocol
docs/routing.md                    routing and tuning
docs/hardware-telemetry.md         DGX/DCGM boundary
docs/github-copilot.md             Copilot/BYOK limitations and external validation
```

## Handover checklist

Before ending a meaningful development session, record what changed, exact green CI/integration evidence, what remains unverified, architecture decisions, exact next step, external dependencies, focused docs, and any version/release-note impact.
