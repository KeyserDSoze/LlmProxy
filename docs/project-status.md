# Project status / handover snapshot

Last reviewed: **2026-09-16**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Current formal product version:

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

The final CI proves backend build/unit/benchmark, React/Vitest/Playwright, version/source-release guards, Linux installer validation, private/public production deployment rendering, image identity, Docker/PostgreSQL integration, caller governance, route-catalog PostgreSQL-outage behavior, retention/rollup compaction, and Bash/PowerShell restore paths.

Full Stack `35088765577` validates the production full-stack Compose changes through Redis + OpenTelemetry + Grafana, transactional outbox recovery, distributed output-token budgets, cross-replica credential rotation and safe node maintenance.

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
```

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

It supports Docker official repository installation on Debian, Ubuntu, Fedora, CentOS and RHEL, and controlled distribution-package fallbacks for common `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk` families. Existing Docker + Compose v2 is preserved. Unknown hosts can preinstall Docker/Compose and rerun with `--skip-docker-install`.

The installer prepares:

```text
/opt/llmproxy/.env
/opt/llmproxy/runtime/
/opt/llmproxy/backups/
```

It generates initial PostgreSQL/Redis/API-key/pepper/Grafana secrets without printing them, optionally authenticates to GHCR, checks DGX `/health` and `/v1/models`, then invokes the same `docker/scripts/deploy.sh` used by `.github/workflows/deploy.yml`.

Production deployment:

1. validates required settings and rejects unresolved `CHANGE_ME` values;
2. requires `ASPNETCORE_ENVIRONMENT=Production`;
3. requires Entra before enabling the public Cloudflare profile;
4. stages Compose + observability assets under `/opt/llmproxy/runtime` so running containers do not depend on an ephemeral runner checkout;
5. runs `docker compose config` before changing containers;
6. pulls/starts the Redis-enabled full stack;
7. requires both `/healthz` and `/readyz`.

The production DGX address is an explicit placeholder, preventing accidental deployment to a plausible sample IP. Grafana binds loopback by default in the production template.

Repository validation does **not** claim that every possible Linux distribution/package repository has been exercised. Actual target-host package installation remains acceptance evidence to capture on the chosen distro/version.

## Supply-chain release evidence — DONE / VALIDATED

Every publication:

1. resolves one exact source SHA;
2. queries GitHub Actions before GHCR login;
3. requires successful `CI` from a push to `main` on that exact SHA;
4. for `workflow_run` publication, requires the API-selected CI ID to equal the triggering CI run;
5. for exact tags, additionally requires tag version == compiled version;
6. builds with source/version/date identity;
7. emits SPDX SBOM + SLSA/BuildKit provenance;
8. records the immutable image digest;
9. reads the pushed OCI index/attestation manifests back from GHCR;
10. requires both SPDX and SLSA predicates;
11. uploads `release-manifest.json`.

Validated `preview.4` registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version               0.2.0-preview.4
source                58a80a60c2f3a049b279be6bf9583ffa4c1cc088
validating CI         35095161900
Publish GHCR          35095620725
image digest          sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
attestation manifest  sha256:cd92f248e73e58fca570a687ca0002d10cfc8e5b308e60ce31351454b4933b0b
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
artifact              10445034650
artifact digest       sha256:cb2bf6b6d8d34a545c080b866866d7098cedbab66f66f475aa168caf6a93c977
```

No immutable Git tag or GitHub Release has been created. That is an explicit product-owner publication action.

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

The next step is environment acceptance, not another generic repository feature:

1. install `preview.4` on the actual target Linux distro/version using `docs/linux-production-deployment.md`;
2. validate real DGX Spark/vLLM/model connectivity and benchmark capacity;
3. validate real Entra roles and Cloudflare/public hostname;
4. validate GitHub Copilot BYOK end-to-end;
5. install/validate the self-hosted deployment runner;
6. choose customer backup destination/encryption/retention and PostgreSQL/Redis/observability HA/storage;
7. create an immutable Git tag/GitHub Release only when explicitly requested.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/Usage Group, not reliably to an individual GitHub user. Never infer identity from IP.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat version `0.2.0-preview.4`, implementation `58a80a60c2f3a049b279be6bf9583ffa4c1cc088`, CI `35095161900`, Full Stack `35088765577`, Publish `35095620725` and image digest `sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a` as the validated baseline;
4. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free configuration lookup, rollup/raw no-double-counting and pre-response-only failover;
5. preserve the Linux production full-stack contract and one manual/Actions `deploy.sh` implementation;
6. preserve the pre-GHCR source-validation gate and post-push SPDX/SLSA registry verification;
7. for new product/operator-visible behavior, bump version/release notes according to `docs/versioning.md`;
8. update engineering docs/evidence after every meaningful increment.
