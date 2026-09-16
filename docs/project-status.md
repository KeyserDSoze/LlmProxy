# Project status / handover snapshot

Last reviewed: **2026-09-16**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal product version:

```text
0.2.0-preview.5
```

Validated runtime/release checkpoint:

```text
implementation          723c47d919a59cf95e447c071ef377ab92a06498
CI                      35099356925 SUCCESS
Publish GHCR            35099987458 SUCCESS
immutable image alias   sha-723c47d
image digest            sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa
attestation manifest    sha256:b15e45a4024235fd2ba28c6a7711ab64922da4be4003d68b8f7ec0eb78db7712
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10448046779
artifact digest         sha256:c90c6ae1db7246afe34f3764543d0ec4a20eed7c6026cf8030e86cc55220562c
```

Validated operator-workflow checkpoint:

```text
commit                  cdd21d6155de08c6202754560f5b3c9f590071f9
CI                      35110131158 SUCCESS
```

The runtime checkpoint is the currently validated product image. The later operator-workflow checkpoint adds self-hosted environment-acceptance automation, README/runbook updates and no runtime implementation changes. Mutable `main` may republish after docs/operator commits; use `sha-723c47d` when the validated `preview.5` runtime image is required.

CI `35099356925` proves the complete `preview.5` implementation including the environment-acceptance harness and the canonical bodyless vLLM `/health` behavior. CI `35110131158` proves the repository with the new self-hosted acceptance workflow and re-runs backend, frontend and all Docker/PostgreSQL regressives, including `Environment acceptance harness smoke`.

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
```

No immutable `v0.2.0-preview.5` Git tag or GitHub Release has been created. That remains an explicit product-owner publication action.

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

Validated `preview.5` runtime registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version               0.2.0-preview.5
source                723c47d919a59cf95e447c071ef377ab92a06498
validating CI         35099356925
Publish GHCR          35099987458
image digest          sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa
attestation manifest  sha256:b15e45a4024235fd2ba28c6a7711ab64922da4be4003d68b8f7ec0eb78db7712
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
artifact              10448046779
artifact digest       sha256:c90c6ae1db7246afe34f3764543d0ec4a20eed7c6026cf8030e86cc55220562c
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
  -> UsageGroup + caller policy from local L1
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

Credentials persist HMAC-SHA256 hashes and safe metadata. In-place rotation preserves identity/group/policy/history linkage and returns the replacement secret once.

Caller governance includes shared request-rate counters and output-token budgets with pre-inference reservation, output-cap injection, known-usage refund and conservative uncertain-usage charging. Redis coordination fails closed.

Input/total-token quotas and monetary budgets remain requirements-driven.

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

The next step is **physical environment acceptance**, not another generic repository feature:

1. install immutable `sha-723c47d` on the actual target Linux distro/version;
2. install/validate the `llmproxy-prod` self-hosted GitHub Actions runner;
3. execute `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM and retain the metadata evidence artifact;
4. run real DGX benchmark sweeps + representative Copilot load and apply measured Capacity Profiles;
5. validate real Entra roles and Cloudflare/public hostname;
6. validate GitHub Copilot BYOK end-to-end;
7. choose customer backup destination/encryption/retention and PostgreSQL/Redis/observability HA/storage;
8. create an immutable Git tag/GitHub Release only when explicitly requested.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/Usage Group, not reliably to an individual GitHub user. Never infer identity from IP.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat `0.2.0-preview.5`, runtime source `723c47d919a59cf95e447c071ef377ab92a06498`, CI `35099356925`, Publish `35099987458`, image alias `sha-723c47d` and digest `sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa` as the validated runtime baseline;
4. treat operator workflow commit `cdd21d6155de08c6202754560f5b3c9f590071f9` / CI `35110131158` as the validated repository acceptance-automation checkpoint;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free config lookup, rollup/raw no-double-counting and pre-response-only failover;
6. preserve metadata-only acceptance evidence and the bodyless vLLM `/health` status-only rule;
7. preserve the pre-GHCR source-validation gate and post-push SPDX/SLSA registry verification;
8. for new product/operator-visible behavior, follow `docs/versioning.md`;
9. update engineering docs/evidence after every meaningful increment.
