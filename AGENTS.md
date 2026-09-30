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

Core responsibilities: authentication, credential lifecycle, request/token governance, Usage Groups and historical usage accounting, logical-model routing, distributed physical-capacity admission, safe runtime maintenance, backup/recovery, version/release visibility, Linux production deployability, supply-chain identity, target-environment acceptance and metadata-only enterprise observability.

Raw prompts, source code, generated outputs, response bodies, bearer tokens and API secrets must never be persisted or added to logs/spans/evidence by default.

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

## Current product version and validated baselines

Current formal source candidate:

```text
0.2.0-preview.8
```

The last fully validated runtime/release checkpoint remains `0.2.0-preview.7` until the candidate passes CI, Full Stack, multi-architecture publication and target-host acceptance.


Validated runtime/release checkpoint:

```text
version                 0.2.0-preview.7
implementation          df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                      35592623906 SUCCESS
Full stack              35592624282 SUCCESS
Publish GHCR            35593081824 SUCCESS
immutable image alias   sha-df3ecf7
image digest            sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

This is the validated runtime baseline for deployment and environment acceptance. Later docs-only commits may move mutable `main`; use `sha-df3ecf7` when the validated `preview.7` product image is required.

No immutable `v0.2.0-preview.7` Git tag or GitHub Release has been created. Creating one remains an explicit product-owner action.

Runtime identity is exposed by:

```http
GET /healthz
GET /api/admin/product
```

Operators read version/build/patch notes at `/admin/releases`. Keep `CHANGELOG.md`, `ProductReleaseCatalog`, Admin package version and compiled version aligned.

## Entra identity and personal API keys

Production Entra application roles are `LlmProxy.Admin`, `LlmProxy.User` and `LlmProxy.Reader`. Personal API-key ownership uses stable Entra `tid + oid`; usernames/email are metadata only. `LlmProxy.User` and `LlmProxy.Admin` can manage only their own personal keys through `/api/me/*` and `/admin/me`. Administrator-created unowned service credentials remain supported for shared/unattended integrations.

`0.2.0-preview.7` adds aggregate **request-count** quotas at Entra user/model scope across all personal keys. User and credential request-rate policies compose with AND semantics and counters must be acquired atomically. Output-token budgets remain credential/model scoped. Monetary/spend budgets are not implemented without explicit pricing/chargeback semantics.

Read `docs/identity-api-keys.md` and `docs/security.md` before changing identity/credential behavior.

## Runtime topology

```text
PostgreSQL = durable configuration/history + transactional runtime-state outbox + usage rollups
Redis      = distributed L2 snapshots/events + shared request/capacity/token-budget/maintenance coordination
local RAM  = per-replica request-path configuration L1
```

Ordinary credential/route/policy configuration lookup is DB-free after startup/runtime publication.

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

The legacy `/api/admin/nodes/{id}/drain` remains deprecated as an unsafe bypass.

Read `docs/operations.md` and `docs/capacity-control.md` before changing maintenance/capacity behavior.

## Credential and caller-governance contract

Credentials persist only HMAC hashes and safe metadata. Rotation is an in-place hard cutover: same credential identity/group/policy/history linkage, new prefix/hash, one-time replacement secret, `Cache-Control: no-store`, safe audit only.

Inference-node provider credentials are a separate trust boundary from client API keys. A node bearer is write-only and persisted/replicated only as AES-GCM ciphertext; `LLMPROXY_UPSTREAM_CREDENTIAL_KEY` is the external recovery key shared by gateway replicas. The client Authorization header must never be forwarded to an inference provider.

Credential request-rate and output-token governance use persisted `RateLimitPolicy`; aggregate Entra-user request quotas use `UserRateLimitPolicy`. Both request scopes publish through the RatePolicy runtime-state channel and must remain DB-free on the ordinary inference path. Redis-enabled distributed token/capacity admission fails closed where the existing contract requires it.

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

Canonical topology:

```text
LlmProxy + PostgreSQL + Redis
+ OpenTelemetry Collector
+ Prometheus + Tempo + Loki + Grafana
+ optional Cloudflare Tunnel profile
```

Development/minimal Compose paths are not the production deployment contract.

Candidate `0.2.0-preview.8` adds an immutable GitHub Release bundle and stable operator command. The release-oriented entry point is documented in `docs/release-installation.md`; it wraps the same validated installer/deployer rather than replacing their host contract.

Canonical repository-checkout first-install entry point:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --dgx-url http://<dgx>:8000 \
  --provider-model '<provider-model-id>' \
  --image-tag sha-df3ecf7
```

The installer supports Docker official repository paths for Debian, Ubuntu, Fedora, CentOS and RHEL plus controlled package-manager fallbacks; preserves working existing Docker/Compose and an existing protected `/opt/llmproxy/.env`; generates initial secrets without printing them; optionally logs into GHCR; checks DGX `/health` + `/v1/models`; and invokes the canonical `docker/scripts/deploy.sh`.

Production host contract:

```text
/opt/llmproxy/.env       operator-owned secrets/config
/opt/llmproxy/runtime/   staged Compose + observability assets
/opt/llmproxy/backups/   backup destination example
/opt/llmproxy/acceptance environment acceptance evidence
```

Cloudflare is optional. Public-tunnel deployment requires Entra enabled/configured first. Grafana binds loopback by default.

Read `docs/linux-production-deployment.md` and `docs/deployment.md` before changing production deployment behavior.

## Production environment acceptance contract

Manual entry point:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

Focused runbook:

```text
docs/environment-acceptance.md
```

Repository-supported Actions path:

```text
.github/workflows/environment-acceptance.yml
```

The workflow runs on the production self-hosted labels `self-hosted, linux, x64, llmproxy-prod`, accepts no API-key dispatch inputs, reads the host-owned `/opt/llmproxy/.env`, requires non-interactive sudo, uploads only `summary.md` and `checks.tsv` with 14-day retention, rejects unexpected files and removes runner-local evidence after the run.

The harness validates host Docker/Compose, direct VM->DGX/vLLM and gateway `/v1/models`, Chat, Responses and SSE surfaces. Canonical vLLM `/health` is status-only: HTTP 200 with an empty body is valid and must not be rejected for lacking JSON.

Evidence is metadata-only. Never add prompts, source, generated output, response bodies or credentials to acceptance evidence.

Repository CI validates the harness against mocks and the workflow repository path. The first real self-hosted-runner run on the target VM/DGX remains **EXTERNAL** and is the next acceptance milestone.

## Release/build/supply-chain contract

Version authority is `Directory.Build.props`; Admin `package.json` stays aligned. Every container publication queries GitHub Actions before GHCR login and proves that the selected source SHA already has successful `CI` from a push to `main`. Exact tags additionally require tag version == compiled version.

Registry-native evidence remains mandatory: Buildx SPDX SBOM, SLSA/BuildKit provenance, immutable digest, post-push OCI attestation verification and `release-manifest.json`.

Read `docs/versioning.md` before release changes.

## Current development focus / resume point

Release-distribution candidate `0.2.0-preview.8` is implemented in source and requires validation before it replaces the `0.2.0-preview.7` baseline. Default next order:

1. obtain green CI + Full Stack for the `0.2.0-preview.8` source, validate the multi-architecture/tagged GitHub Release path, then install it on the actual ARM64 GB10 target;
2. if candidate validation fails, continue using immutable `sha-df3ecf7` as the runtime baseline;
3. install/validate the dedicated `llmproxy-prod` self-hosted runner and execute `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM;
4. calibrate real DGX capacity with intended models and representative Copilot load;
5. validate real Entra roles, personal-key self-service + aggregate user request quotas, Cloudflare/public hostname and GitHub Copilot BYOK end-to-end;
6. define customer-specific PostgreSQL/Redis/observability HA/storage and backup destination/retention/encryption;
7. evolve quota semantics only with explicit tokenizer/pricing requirements;
8. create immutable tags/releases only through the owner-triggered release workflow after validation.

Do not invent per-user identity from a shared GitHub Copilot BYOK credential or from IP addresses.

## External validation still required

- installer/package behavior on the actual target Linux distribution/version;
- first real environment-acceptance run on the production VM/DGX;
- real DGX Spark + intended vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/roles;
- real Cloudflare Tunnel/public domain;
- real GitHub Copilot BYOK end-to-end;
- on-prem self-hosted runner permissions/reboot behavior;
- customer backup destination/retention/encryption;
- customer-specific PostgreSQL/Redis/observability HA/storage;
- Copilot usage-metrics behavior if per-user analytics are required.

## Architecture decision: NVIDIA PAIR

NVIDIA Personal AI Router was evaluated and rejected for the current direction. Continue custom **LlmProxy + vLLM** unless that decision is explicitly reopened.

## Documentation map

```text
README.md                            product/developer entry point
CHANGELOG.md                         product-visible release history
docs/project-status.md              canonical state and exact resume point
docs/development-log.md             chronological engineering + validation trace
docs/roadmap.md                     milestone state/backlog
docs/linux-production-deployment.md zero-to-running Linux production runbook
docs/environment-acceptance.md      target-host/DGX/gateway acceptance contract
docs/deployment.md                  deployment contract/automation summary
docs/versioning.md                  SemVer/release/build/supply-chain rules
docs/operations.md                  health, acceptance, maintenance and release operations
docs/data-retention.md              raw metrics + daily rollups + audit/outbox retention
docs/usage-governance.md            auth, credential lifecycle, groups, quotas, usage
docs/runtime-cache.md               L1/L2 + transactional outbox
docs/backup-restore.md              PostgreSQL recovery contract
docs/full-stack.md                  Redis + observability bundle
docs/capacity-control.md            physical admission + capacity leases
docs/benchmarking.md                benchmark protocol
docs/routing.md                     routing and tuning
docs/hardware-telemetry.md          DGX/DCGM boundary
docs/github-copilot.md              Copilot/BYOK limitations and external validation
```

## Handover checklist

Before ending a meaningful development session, record what changed, exact green CI/integration evidence, what remains unverified, architecture decisions, exact next step, external dependencies, focused docs, and any version/release-note impact.
