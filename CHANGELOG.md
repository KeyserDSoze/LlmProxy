# Changelog

All notable product changes to **LlmProxy** are recorded here.

The project follows Semantic Versioning from the first formal preview release onward. Because the product is still pre-1.0, incompatible changes may occur between preview/minor releases and must be called out explicitly in release notes.

## [Unreleased]

No unreleased product changes are recorded after the current preview baseline yet.

## [0.2.0-preview.4] - 2026-09-16

### Added

- A single Linux production deployment path now installs the validated full stack: LlmProxy, PostgreSQL, Redis and bundled observability.
- `docker/.env.production.example` provides an operator-oriented production template separate from development/full-stack examples.
- The production deploy script stages Compose and observability assets under `/opt/llmproxy/runtime`, so running containers do not depend on a transient GitHub Actions workspace.
- Cloudflare Tunnel can be enabled as an optional Compose profile when a tunnel token is present.
- A dedicated Linux production guide covers private-LAN bootstrap, DGX connectivity, Entra/public exposure, self-hosted runner setup, backup, update and rollback.

### Changed

- `docker/scripts/deploy.sh` and `.github/workflows/deploy.yml` now deploy the same Redis-enabled full-stack topology used by the supported production documentation instead of the legacy minimal Compose overlay.
- Production deployment performs Compose validation before pull/up and verifies both `/healthz` and `/readyz` before succeeding.

### Security

- Grafana can bind to loopback independently from the gateway, and the production guide requires Entra configuration before exposing administrative surfaces through a public tunnel.

## [0.2.0-preview.3] - 2026-09-16

### Added

- Exact SemVer tag publication now requires evidence that the same source SHA already completed the repository `CI` workflow successfully from a push to `main`.
- Release manifests record the validating CI run ID alongside image digest, source SHA, build timestamp, SBOM and provenance evidence.
- The tagged-release validation predicate is a reusable shell guard with positive and negative CI fixtures, so the gate is tested without creating a real immutable tag.

### Changed

- A direct `vX.Y.Z` tag push can no longer publish an exact-version GHCR image solely because the tag matches compiled version metadata; unvalidated source SHAs fail before GHCR publication.

### Security

- Exact versioned container releases are now tied to previously validated `main` source rather than trusting tag creation alone.

## [0.2.0-preview.2] - 2026-09-16

### Added

- GHCR container publication now emits an SPDX software bill of materials (SBOM) as an OCI attestation.
- Published images now include explicit SLSA/BuildKit provenance metadata alongside the existing source SHA and build timestamp identity.
- The publish workflow records the immutable registry digest for every pushed image.

### Changed

- Container publication verifies the pushed digest directly in GHCR and reads both SBOM and provenance back from the registry before the workflow can succeed.

### Security

- Release consumers can inspect the image dependency inventory and build provenance without relying only on mutable tags.

## [0.2.0-preview.1] - 2026-09-16

### Added

- Daily PostgreSQL usage rollups preserve request/token/error accounting after granular request metrics age out.
- Raw request-metric retention and historical rollup retention are independently configurable; defaults are 90 and 730 days.
- Usage reporting transparently combines recent raw metrics with historical rollups without double counting.
- Admin Usage & Governance supports reporting windows up to 730 days and shows whether historical rollups contributed to the result.

### Changed

- Usage reporting windows are defined as UTC calendar days so daily historical rollups have deterministic boundaries.
- Request-metric cleanup rolls complete UTC days into durable aggregates before deleting the corresponding raw rows.

## [0.1.0-preview.1] - 2026-09-16

### Added

- OpenAI-compatible Chat Completions and Responses surfaces with SSE streaming and cancellation.
- Logical-model routing across DGX/vLLM with weighted least loaded, round robin and weighted round robin strategies.
- Distributed runtime state using PostgreSQL as durable truth, Redis as shared L2/coordination and local RAM as request-path L1.
- Physical capacity admission with Redis leases, fail-closed coordination and active lease-loss cancellation.
- Caller governance with HMAC-backed credentials, Usage Groups, request-rate limits and output-token budgets.
- Credential rotation with one-time replacement secrets and cross-replica runtime propagation.
- Metadata-only request metrics, audit, vLLM runtime telemetry and optional DGX hardware telemetry.
- Repository-supported PostgreSQL backup/restore operators for Bash and PowerShell with destructive clean-target verification.
- Safe node maintenance flow with distributed admission pre-block, drain-to-zero, health/models/warm-up validation and controlled resume.
- Product version and release notes exposed through the Admin API and Admin UI.

### Changed

- Node maintenance is now the supported path for runtime/model upgrades; a draining node cannot re-enter routing until validation succeeds.
- Release management now follows SemVer while the product remains pre-1.0.

### Fixed

- Closed the cross-replica drain race by enforcing the maintenance marker inside distributed capacity admission.
- Removed the unsafe legacy drain path from normal administration; callers are directed to the maintenance API.

### Security

- Raw prompts, generated content and API secrets remain excluded from persistent telemetry and audit by default.
- Inference credentials are persisted as HMAC hashes; raw credential material is returned only at creation or rotation time.

### External validation still required

- Real DGX Spark/vLLM/model benchmark sweeps and representative multi-DGX coding load.
- Real GitHub Copilot BYOK end-to-end.
- Real Entra app/role configuration and Cloudflare/public-hostname acceptance.
- Customer production backup destination/encryption/retention and native deployment-host acceptance where applicable.
