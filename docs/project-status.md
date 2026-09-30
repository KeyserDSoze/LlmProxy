# Project status / handover snapshot

Last reviewed: **2026-09-30**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal source candidate:

```text
0.2.0-preview.8
```

The fully validated runtime/release checkpoint remains `0.2.0-preview.7` below until the candidate passes repository and target-host validation.


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

CI `35592623906` covers backend/unit/frontend/Playwright plus Docker/PostgreSQL regressives, including aggregate user-quota governance, restart republish, backup/restore and retention. Full Stack `35592624282` proves Redis/OpenTelemetry bootstrap, transactional outbox recovery, aggregate user request quota across two gateway replicas, distributed output-token budgets, cross-replica credential rotation and safe maintenance. Publish `35593081824` validates source CI before GHCR login and verifies the pushed OCI digest, SPDX SBOM and SLSA/BuildKit provenance.

The validated deployment image is `ghcr.io/keyserdsoze/llmproxy:sha-df3ecf7`. Later documentation-only commits may republish mutable `main` but do not replace this runtime checkpoint.

## Release-based distribution / 0.2.0-preview.8 — IMPLEMENTED IN SOURCE / VALIDATION PENDING

The current source candidate adds an immutable Linux distribution layer over the existing production installer/deployer:

```text
GitHub owner-triggered release tag
  -> exact-SHA CI + Full Stack evidence gate
  -> tagged multi-arch GHCR image (linux/amd64 + linux/arm64)
  -> SPDX/SLSA verification
  -> checksummed Linux operator bundle
  -> GitHub Release
  -> bootstrap.sh
  -> /opt/llmproxy/releases/<version>
  -> llmproxyctl install/update/status/logs/doctor/rollback
```

Host-owned `/opt/llmproxy/.env` and Docker volumes remain outside versioned bundles. The release installer reuses `docker/scripts/install-linux.sh` and `deploy.sh`; it does not fork a second deployment implementation.

The distribution still installs LlmProxy/control-plane dependencies rather than llama.cpp/vLLM/model weights. Protected inference runtimes are now supported: nodes can carry an AES-GCM-encrypted write-only upstream bearer, and same-host llama.cpp is validated through Docker's bridge gateway instead of relying on a skipped precheck.

Validation still required before promotion:

- green CI and Full Stack on the exact candidate SHA;
- successful Buildx publication for both amd64 and arm64 plus existing SBOM/provenance verification;
- owner-triggered immutable tag and GitHub Release asset creation;
- first real install/update/rollback exercise on the GB10 ARM64 host, including bridge-bound llama.cpp and its upstream bearer credential.

## Product/versioning — DONE / VALIDATED

Version authority and product surfaces:

```text
Directory.Build.props                  compiled version
src/LlmProxy.Admin/package.json        bundled Admin version
GET /healthz                           runtime version
GET /api/admin/product                 product/release/build object
/admin/releases                        operator-visible release notes
CHANGELOG.md                            human-readable product history
docs/versioning.md                     release/version/build rules
```

Formal release sequence:

```text
0.1.0-preview.1  initial versioned product baseline
0.2.0-preview.1  historical usage rollups
0.2.0-preview.2  GHCR SBOM/provenance verification
0.2.0-preview.3  source-validated main/tag publication
0.2.0-preview.4  consolidated Linux production deployment + host installer
0.2.0-preview.5  production environment acceptance evidence
0.2.0-preview.6  Entra-owned personal API keys + user self-service
0.2.0-preview.7  aggregate Entra user request quotas
```

No immutable `v0.2.0-preview.7` Git tag or GitHub Release has been created. That remains an explicit product-owner publication action.

## Linux production deployment — DONE / VALIDATED FOR REPOSITORY PATH

Canonical operator runbook:

```text
docs/linux-production-deployment.md
```

Supported production topology:

```text
LlmProxy + PostgreSQL + Redis
+ OpenTelemetry Collector
+ Prometheus + Tempo + Loki + Grafana
+ optional Cloudflare Tunnel profile
```

Canonical first-install script:

```text
docker/scripts/install-linux.sh
```

It supports Docker official repository installation on Debian, Ubuntu, Fedora, CentOS and RHEL, controlled package-manager fallbacks for common `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk` families, preservation of existing working Docker + Compose v2, protected host-owned config, generated initial secrets, optional GHCR login, DGX precheck and invocation of the canonical `docker/scripts/deploy.sh`.

Production host state:

```text
/opt/llmproxy/.env
/opt/llmproxy/runtime/
/opt/llmproxy/backups/
/opt/llmproxy/acceptance/
```

Actual target-host package/service behavior is still external evidence; repository validation does not claim universal Linux package compatibility.

## Production environment acceptance — REPOSITORY DONE / REAL ENVIRONMENT EXTERNAL

Canonical manual command:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

Focused runbook:

```text
docs/environment-acceptance.md
```

Self-hosted Actions path:

```text
.github/workflows/environment-acceptance.yml
```

The harness records Linux/Docker/Compose metadata and validates direct VM -> DGX/vLLM plus LlmProxy gateway surfaces:

```text
/health or /healthz + /readyz
/v1/models
/v1/chat/completions
/v1/chat/completions stream=true
/v1/responses
/v1/responses stream=true
```

Direct provider-model visibility and gateway logical-model visibility are required. Canonical vLLM `/health` is a status-only check because a healthy vLLM server may return HTTP 200 with an empty body.

Evidence is deliberately metadata-only:

```text
summary.md
checks.tsv
```

Request bodies/prompts, source code, generated output, response bodies and API/bearer secrets are temporary only and are not copied into evidence. A final guard rejects evidence containing the gateway or optional DGX bearer value.

The GitHub Actions workflow:

- runs on `self-hosted, linux, x64, llmproxy-prod`;
- uses the `production` GitHub environment;
- has no API-key workflow-dispatch inputs;
- reads `/opt/llmproxy/.env` through the root-owned acceptance process;
- requires non-interactive sudo;
- rejects unexpected evidence files;
- uploads only `summary.md` and `checks.tsv` with 14-day retention;
- preserves a failed acceptance result after uploading available metadata evidence;
- deletes runner-local evidence afterward.

Repository CI proves the script and workflow repository path without pretending to prove the physical environment. The first real run on the target VM/DGX remains **EXTERNAL**.

## Supply-chain release evidence — DONE / VALIDATED

Every publication resolves one exact source SHA, requires successful `CI` push evidence before GHCR login, enforces tag/version match for exact tags, builds with source/version/date identity, emits SPDX + SLSA/BuildKit attestations, verifies the pushed OCI index and uploads `release-manifest.json`.

Validated `preview.7` runtime registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version                 0.2.0-preview.7
source                df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
validating CI         35592623906 SUCCESS
Full Stack            35592624282 SUCCESS
Publish GHCR          35593081824 SUCCESS
image alias           sha-df3ecf7
image digest          sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
artifact              10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

## Core runtime scope — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + runtime-state outbox + usage rollups
Redis      = distributed L2 + request/capacity/token-budget/maintenance coordination
local RAM  = per-gateway request-path configuration L1
```

Ordinary inference configuration lookup is DB-free after startup/runtime publication.

Current request path:

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC bearer credential from local L1
  -> UsageGroup + Entra owner + user/credential caller policy from local L1
  -> output-token reservation when configured
  -> request-rate admission
  -> logical model -> route catalog from local L1
  -> smart routing
  -> Redis/local physical-capacity admission
  -> vLLM
  -> output-token settlement
  -> metadata-only metric + OTEL telemetry
```

### Runtime state

Node/Model/Deployment/Credential/RatePolicy changes and runtime outbox rows commit in the same PostgreSQL transaction. A globally serialized advisory-lock worker publishes/retries Redis state, updates its L1 and marks rows processed only after acknowledged publication. Pending outbox rows are never retention-deleted.

### Safe maintenance

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Drain pre-blocks admission. Existing streams finish. Resume requires zero global active leases and successful `/health`, `/v1/models` and one-token warm-up validation. The legacy direct drain endpoint remains deprecated.

### Credentials and caller governance

Credentials persist HMAC-SHA256 hashes and safe metadata. Administrator-created service credentials remain unowned. Personal credentials store stable Entra `OwnerTenantId + OwnerObjectId` and optional principal-name display metadata; raw secrets are returned once. In-place rotation preserves credential identity, Entra ownership, group/policy/history linkage and returns the replacement secret once.

Entra roles are `LlmProxy.Admin`, `LlmProxy.User` and `LlmProxy.Reader`. Normal users use `/api/me/*` or `/admin/me` to create/list/rotate/revoke only their own keys and inspect own credential-attributed usage. Admin/Reader identity inventory is available under `/api/admin/identity`.

Caller governance includes credential/model request-rate counters and output-token budgets with pre-inference reservation, output-cap injection, known-usage refund and conservative uncertain-usage charging. Preview.7 adds aggregate Entra-user request-rate policies across all personal keys, keyed by stable `tid+oid` and optionally logical model. Applicable user and credential request counters are acquired atomically with AND semantics. Redis coordinates the aggregate counters across replicas; the request path remains DB-free.

Aggregate user output-token quotas, input/total-token quotas and monetary budgets remain requirements-driven; spend enforcement additionally requires an explicit pricing/chargeback model.

### Historical reporting / retention

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
```

Complete expired UTC days roll up transactionally before raw deletion. A PostgreSQL advisory transaction lock serializes compaction across replicas. Reporting merges historical rollups with newer raw metrics without double counting.

### Backup / restore

PostgreSQL is durable recovery authority; Redis is rebuildable runtime state. Bash and PowerShell backup/restore operators are validated with destructive clean-target restore smokes. Raw API secrets are not in PostgreSQL. `Authentication__ApiKeyPepper` and deployment secrets must be preserved separately.

### Routing / capacity / observability

Current MVP includes logical aliases, weighted least loaded / round robin / weighted round robin, health hysteresis, path-prefixed service roots, pre-response-only failover, vLLM pressure/EWMA feedback, benchmark-derived Capacity Profiles, distributed capacity leases with lease-loss cancellation, and metadata-only OTEL/DCGM observability.

Prompts/source/generated output/API secrets remain excluded from persistent telemetry by default.

## Current development focus

The immediate step is validating the new release-distribution candidate before physical product acceptance continues:

1. get green CI + Full Stack on the exact `0.2.0-preview.8` source and validate tagged multi-architecture publication/release assets;
2. install/update/rollback the candidate on the actual ARM64 GB10 host;
2. install/validate the `llmproxy-prod` self-hosted GitHub Actions runner;
3. execute `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM and retain the metadata evidence artifact;
4. run real DGX benchmark sweeps + representative Copilot load and apply measured Capacity Profiles;
5. validate real Entra Admin/User/Reader roles, personal-key self-service, aggregate user request quotas and Cloudflare/public hostname;
6. validate GitHub Copilot BYOK end-to-end;
7. choose customer backup destination/encryption/retention and PostgreSQL/Redis/observability HA/storage;
8. create an immutable Git tag/GitHub Release only when explicitly requested.

## Identity limitation

A personal API key is attributable to its Entra owner through stable `tid + oid` plus the credential ID retained in usage telemetry/rollups. A centrally configured GitHub Copilot BYOK provider may still use one shared **service credential**; traffic through that shared key cannot reliably identify an individual GitHub user. Never infer identity from IP.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat `0.2.0-preview.7`, runtime source `df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38`, CI `35592623906`, Full Stack `35592624282`, Publish `35593081824`, image alias `sha-df3ecf7` and digest `sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99` as the validated runtime baseline;
4. preserve the Entra `tid+oid` personal-key ownership contract, service-credential compatibility and atomic user+credential request-rate admission;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free config lookup, rollup/raw no-double-counting and pre-response-only failover;
6. preserve metadata-only acceptance evidence and the bodyless vLLM `/health` status-only rule;
7. preserve the pre-GHCR source-validation gate and post-push SPDX/SLSA registry verification;
8. for new product/operator-visible behavior, follow `docs/versioning.md`;
9. update engineering docs/evidence after every meaningful increment.
