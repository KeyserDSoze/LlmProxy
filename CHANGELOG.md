# Changelog

All notable product changes to **LlmProxy** are recorded here.

The project follows [Semantic Versioning](https://semver.org/) from the first formal preview release onward. Because the product is still pre-1.0, incompatible changes may occur between preview/minor releases and must be called out explicitly in release notes.

## [Unreleased]

No unreleased product changes are recorded after the initial versioned preview baseline yet.

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
- Published container images carry OCI product-version, source-revision and build-date metadata that is also available to the runtime release view.
- Tagged container publication rejects a Git tag that does not exactly match the compiled product version.
- Prerelease image tags publish the exact prerelease version plus source-SHA alias without updating a stable major/minor alias.

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
