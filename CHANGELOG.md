## Unreleased — Admin model/runtime downloads and AirLLM experiment (2026-10-10)

- Admin Infrastructure: selectable compatible inference runtime, advanced execution profile fields, and guarded custom Hugging Face model repository installation.
- Paired Linux Node Agent: persistent asynchronous image/model download jobs, per-stage progress, status/cancellation and explicit interrupted state after restart; Agent relay management allowlist updated for remote job APIs.
- Experimental AirLLM 4.0.0: source-bundled Linux Agent Docker build and a bounded single-worker, non-streaming Chat adapter, initial Qwen3-4B catalog profile. Not production-validated; no SSE, Responses or tool calling.
- The Admin finalizes downloads into deployment records when it observes successful completion. Full autonomous reconciliation and GPU performance acceptance are not yet complete.

# Changelog

All notable product changes to **LlmProxy** are recorded here.

The project follows Semantic Versioning from the first formal preview release onward. Because the product is still pre-1.0, incompatible changes may occur between preview/minor releases and must be called out explicitly in release notes.

## [Unreleased]

### Added

- Public same-domain Linux Agent installer and version-pinned archive download links, usable with curl without login; Admin-only 30-minute pairing invitations remain mandatory to enroll a node.

- Redis-coordinated cross-replica Agent WSS relay with encrypted transient per-request streaming forwarding, short-lived owner leases and fail-closed missing-owner handling. Requires acceptance testing across independent gateways.

- Cancel ongoing Admin benchmark jobs without CLI access; cancelled and stale jobs cannot write or apply recommendations.

- Administrator-scheduled checksum-verified Node Agent upgrades delivered through paired Agent heartbeats, with independent systemd updater and automatic rollback when service verification fails.

- One-click Admin inference benchmarks run as persisted background jobs on the gateway with per-concurrency metrics and provisional SLO-based capacity evidence. Results display in the UI; no automatic limit changes.

- Self-service Linux Node Agent pairing in Infrastructure: short-lived one-use invitations, checksum-verified installer, automatic discovery/heartbeats, plus outbound WSS management and SSE inference transport for machines outside the gateway network. Requires one gateway replica for this initial relay and real host acceptance before HA/production use.

- Benchmark SSE integrity checks: interrupted streams and explicit Responses errors now count as failures rather than successful requests, preventing inflated concurrency claims.

- Infrastructure model install dialog accepts logical alias and optional host port; each running installation provides a copyable direct benchmark command without changing production capacity limits.

- Observational per-managed-deployment runtime pressure metrics for vLLM, SGLang and llama.cpp; Infrastructure displays running/queued requests and available cache usage without modifying routing behavior.

- Multiple managed inference configurations of one model on a single physical node, with distinct container/deployment IDs and a PostgreSQL migration using filtered unique indexes. Shared physical-node capacity remains enforced.

- Managed inference deployments support vLLM execution profiles (`maxNumSeqs`, context, KV cache auto/FP8 and CPU offload), official Qwen3 AWQ 4-bit checkpoint entries and experimental llama.cpp (GGUF) / SGLang runtime profiles through the Node Agent. Admin installation records the selected profile and rejects conflicting reinstalls; existing vLLM defaults and gateway routing contract are preserved. Other runtimes require real-device acceptance before production routing.
- Benchmark harness can evaluate explicitly configured p95 TTFT, success-rate and throughput-per-slot proxy SLOs, returning sample-size-aware advisory capacity candidates and per-level reasons in JSON. Live physical/deployment concurrency limits remain operator-controlled; no 12-concurrent-user improvement is claimed without measured evidence.


- Streaming Chat Completions and Responses Request Audit now stores **one reconstructed JSON response** (including partial content, tool calls, usage when known, and terminal/error status) rather than raw per-token SSE events. The Admin and user audit modal offers a readable response and structured JSON inspector. Original client streaming remains unchanged; audit capture is bounded and reports truncation. **Breaking pre-production audit format change:** old raw-SSE audit data is not migrated or supported by a legacy viewer.

- Admin **Infrastructure** unifies physical Fleet & access, Capacity & telemetry, and Inventory & model lifecycle, with post-creation editing of the hardware-wide simultaneous-request ceiling.
- Legacy same-host node rows can be consolidated safely after a distributed drain; deployments preserve their runtime root, encrypted upstream credential and prior inherited concurrency ceiling while moving under one physical hardware limit.
- Model deployments support an encrypted upstream-bearer override so multiple authenticated runtimes/ports on one physical host can share hardware capacity without sharing provider credentials.
- Models & Deployments exposes an explicit per-deployment concurrency editor; an empty deployment limit inherits the physical hardware ceiling.
- Administrator automatic-update policies support Manual, ASAP (five-minute checks), Nightly, Weekly and Monthly cadence, always targeting the latest stable release.
- System One classifiers are first-class logical models/deployments with model → node and node → model visibility, deployment-specific runtime roots, routed diagnostics and `GET /v1/systemone/models`.

- Admin **Request Metrics** now starts with the newest 20 requests and provides server-side pagination plus model, node, credential and success/error filters.
- Disabled, idle inference nodes can be deleted safely from Admin; deletion removes ordinary deployments transactionally and is rejected while the node is enabled, still serving active requests, or still owns managed model installations.
- Newly created or rotated API-key secrets use scope-aware prefixes: `lp_org_` for organization credentials and `lp_usr_` for personal credentials. Existing keys keep working unchanged until rotation.
- The Model & Hardware page exposes the Node Agent installer directly for download.

- Admin **Release Notes & Updates** shows the installed version and subsequent immutable GitHub Releases, exposes the per-release operator command/update plan, and supports **Update now**, scheduled updates, cancellation of pending work and recent job status.
- A persistent, bearer-authenticated **LlmProxy Update Agent** runs on the Linux control-plane host outside the gateway container so update jobs survive container replacement/restart.
- Every immutable release publishes `llmproxy-update-plan.json`; the default plan uses the normal versioned installer, while releases that require special host changes can opt into a checksum-verified bundled `distribution/update.sh` procedure instead of accepting arbitrary UI shell commands.
- Administrator **Model & Hardware** inventories prepared Linux hosts, compares curated deployable-model requirements with currently free GPU VRAM/system RAM/disk, and controls install/start/stop/remove through an authenticated node agent.
- The self-contained **LlmProxy Node Agent** manages Docker/vLLM runtimes on prepared x86_64 or ARM64 Linux hardware and publishes CPU, RAM, disk and NVIDIA GPU inventory.
- Managed deployments carry their own runtime endpoint, catalog identity and agent installation identity, allowing several models on one physical server while keeping routing pools grouped strictly by requested logical model.
- Immutable GitHub Releases publish checksummed Node Agent archives for `linux-x64` and `linux-arm64`, including the systemd unit, environment template and installer.
- The Admin application ships an SVG favicon.
- Admin **Users & Access** introduces a first-class Entra end-user registry with selectable **Manual** census or **Automatic** first-login provisioning, keyed by stable tenant/object ID rather than email.
- Administrators can disable/re-enable end users. Disable blocks the personal portal and revokes all active personal API keys owned by that user's `tid + oid`; re-enable does not resurrect revoked keys.
- The normal-user `/admin/me` surface is now **My dashboard** and includes recent request metadata in addition to personal keys, own usage and request limits.
- Admin **Request Audit** persists exact Chat Completions, Responses and System One request/response bodies as application-encrypted PostgreSQL ciphertext and adds server-side filtering/pagination by user, credential, model, surface, status, request ID and time range, with optional live refresh and exact-body inspection.
- Full-body request-audit retention is independently administrator-configurable from 10 through 4015 days (11 x 365 days), defaults to 30 days and is enforced automatically every four hours; administrators can also run an audited cleanup immediately.
- **My dashboard** now includes **My request audit**, allowing an admitted Entra user to inspect exact retained request/response payloads only for calls attributable to that user's own personal API keys.
- Admin **Playground** tests enabled logical models through real routing/capacity admission and tests the configured System One classifier with an editable JSON payload while showing the raw upstream exchange.
- Admin **Help & Endpoints** provides copy-ready examples for Models, Chat Completions, Responses and System One plus an explanation of authentication, governance, routing, capacity, observability and retention.
- Every principal Admin, Governance, Release Notes and User Portal screen now has a collapsed contextual documentation accordion.
- Newly created or rotated client API keys retain an application-encrypted recovery copy so administrators/super admins can reveal and copy the secret later without changing HMAC-based request authentication.
- Optional public `POST /v1/systemone` gateway surface for Jev-compatible System One classifiers such as Laya, protected by the existing LlmProxy API-key middleware while keeping the classifier upstream private.
- Configurable System One upstream base address, bearer credential and timeout; client Authorization is never forwarded to the classifier.
- Every successful push to `main` now creates one immutable distribution release automatically, starting at `v0.0.1`.
- Automatic release numbering defaults to patch increments and supports intentional `release:minor` / `release:major` commit-message markers.
- The release publication workflow is reusable and is invoked directly after tag allocation, avoiding reliance on workflow recursion from a `GITHUB_TOKEN`-created tag.

### Changed

- Capacity administration now distinguishes admitted people, physical simultaneous inference requests and caller rate/token governance; the live physical-capacity view uses distributed coordination counts when available and shows the local gateway count only as diagnostics.
- Legacy System One bootstrap attaches to an already-registered same-host physical node when possible, keeping its distinct runtime port and bearer at deployment scope instead of manufacturing a separate physical capacity pool.
- Version jumps now preserve every intermediate stable release in order for Admin, automatic and future manual `llmproxyctl update` paths, preventing an intermediate custom migration from being skipped.
- System One now uses the common routing, distributed capacity, failover, request-rate governance, request metrics and encrypted upstream-credential path; legacy `SYSTEM_ONE_*` configuration is imported once for compatibility.
- Immutable release downloads retry transient network failures such as connection resets before failing checksum-verified bootstrap.

- Admin information architecture was compacted around task-focused tabs and action dialogs: Inference Nodes, Hardware, Model & Hardware, Playground, Usage & Governance, and Users & Access no longer keep every creation/configuration form expanded on the page.
- **Models** and **Deployments** are now one bidirectional workspace: operators can inspect model → nodes or node → models, publish logical aliases, deploy to another/all active nodes, and enable/disable routing participation without switching between overlapping pages.
- API Credentials now labels organization vs personal ownership explicitly, explains Usage Group as governance rather than ownership, and keeps administrator reveal/rotate/revoke controls in the same table.
- Logical-model documentation now explains the distinction between client-facing logical aliases and provider/runtime model identifiers.

- Hardware terminology and deployment tooling are hardware-agnostic; DGX remains a supported example rather than the only assumed inference platform.
- Usage & Governance and Release Notes are part of the main Admin navigation; write-only hardware/model lifecycle and user-access controls remain administrator-only.
- Managed stop/remove disables routing before changing the remote model runtime so traffic fails safe.
- Self-service authorization is now Entra authentication + the LlmProxy platform-user registry. The `LlmProxy.User` app role may remain assigned, but normal-user portal admission no longer depends on that role when the registry admits the user.
- GitHub Copilot documentation now distinguishes shared provider/API-key attribution from individual developer identity and explicitly avoids IP/User-Agent/undocumented-header inference.
- API credential rows expose whether a recovery secret is available; older non-recoverable keys can be rotated once, while a still-configured bootstrap key is backfilled automatically at startup.
- The product security contract now separates metadata-only metrics/audit/OTEL from the dedicated encrypted request-audit store; Admin has global visibility, normal users have stable `tid + oid` owner-scoped visibility for personal credentials only, and request headers plus bearer/API secrets remain excluded.
- Distributed Full Stack acceptance is now a required job inside the main CI workflow, so the single successful CI result is the complete release gate.
- Distribution SemVer is injected into the packaged .NET assembly and OCI metadata instead of requiring a bot commit that rewrites source version files.
- Exact version, `latest`, `main`, `sha-<7>` and major/minor GHCR aliases are published from the same validated multi-architecture build.

### Security

- End-user authorization and manual census use stable Entra `tid + oid`; email/UPN/display name remain mutable metadata only.
- Disabling a normal user revokes active personal credentials through the existing runtime cache/outbox propagation path. Shared service credentials are intentionally unaffected because they are not attributable to one person.
- API-key reveal still requires `LlmProxy.Admin`/`AdminWrite`. Global request-audit detail also requires AdminWrite; read-only operators cannot inspect payloads. Normal users can decrypt only request-audit rows attributable to their own personal credentials through `/api/me/content-logs*`.
- API-key reveal actions and retention changes/manual cleanup are audited without secret or payload content.
- Decrypted secret/request-audit responses use `Cache-Control: no-store`; headers, client API keys and upstream bearer credentials are not persisted in request-audit payloads.
- The deployment API-key pepper is now also required to decrypt administrator recovery copies and full-body logs and must be preserved as recovery material.

## [0.2.0-preview.8] - 2026-09-30

### Added

- Immutable GitHub Releases can distribute a checksummed Linux operator bundle containing the production Compose stack, observability configuration, operator scripts and the new `llmproxyctl` command.
- `llmproxyctl` provides status, health, logs, lifecycle, diagnostics, explicit versioned update and local rollback operations while preserving `/opt/llmproxy/.env` and Docker data volumes.
- An immutable release workflow validates the exact `main` SHA before publication; the later automatic release train removes the manual dispatch step.
- Tagged container publication targets both `linux/amd64` and `linux/arm64`, enabling the same product release on x86_64 Linux and DGX Spark / GB10-class ARM64 hosts.
- Tagged publication packages the Linux release bundle/bootstrap/checksums and creates the matching GitHub Release after container SBOM/provenance verification.
- Per-node write-only upstream bearer credentials support protected llama.cpp/vLLM runtimes across health, model discovery, runtime metrics, maintenance warm-up and inference.
- Same-host inference bootstrap validates `host.docker.internal` through Docker's bridge gateway and reports a concrete bind-address fix when the runtime is loopback-only.

### Changed

- The existing production Linux installer is reused as a versioned release primitive instead of requiring a long-lived repository checkout for normal installation and updates.
- Release installs pin the application image to the exact bundle version and keep installed operator bundles under `/opt/llmproxy/releases/<version>`.

### Security

- Linux release bundles are SHA-256 verified before privileged installation.
- Installer callers cannot override the exact release image tag, and the release-tag workflow refuses existing tags rather than moving/reusing them.
- GitHub repository/package download credentials remain outside the LlmProxy application environment.
- Upstream provider bearer plaintext is never returned by Admin APIs or stored in PostgreSQL/Redis; AES-GCM ciphertext uses a stable deployment master key that must be backed up separately.
- A bootstrap provider bearer may be supplied as a one-time installer environment variable and is removed from the long-lived container environment after encrypted bootstrap.

### Known scope

- The release installer deploys LlmProxy and its control-plane dependencies; it does not install llama.cpp/vLLM or model weights.

## [0.2.0-preview.7] - 2026-09-21

### Added

- Administrators can configure request-rate policies for an Entra user across all personal API keys, optionally scoped to one logical model.
- User-level rate policies are persisted in PostgreSQL, published through the transactional runtime-state outbox, synchronized through Redis and enforced from local runtime state without database reads on the inference path.
- `/api/me/rate-limits` and the personal portal expose effective user request-limit metadata in read-only form.
- Admin Usage & Governance exposes user quota creation, enable/disable and deletion for Entra users that already own personal API keys.

### Changed

- Request admission applies user and credential request-rate policies with **AND** semantics.
- Applicable counters are acquired atomically: if either the user policy or credential policy rejects the request, neither counter is incremented.

### Security

- User quota scope uses stable Entra tenant ID + object ID rather than username/email.
- User quotas do not change API-key storage: raw secrets remain one-time values and are never persisted.
- Currency/spend enforcement remains intentionally disabled until an explicit model pricing or chargeback policy exists.

### Known scope

- Aggregate request-count limits are implemented at user/model scope.
- Output-token budgets remain credential/model scoped.
- Monetary spend limits still require a product-defined pricing/accounting model and are not inferred from raw token counts.

## [0.2.0-preview.6] - 2026-09-21

### Added

- Microsoft Entra users with the `LlmProxy.User` application role can create and manage multiple personal inference API keys through `/api/me/api-credentials`.
- Personal API keys are bound to the creator's stable Entra tenant ID (`tid`) and object ID (`oid`), while existing administrator-created credentials remain supported as unowned service credentials.
- Self-service endpoints expose the current Entra identity, personal key metadata, one-time key creation/rotation, revocation and usage aggregated from the existing credential-level telemetry and historical rollups.
- Administrators can inspect credential ownership and a user-oriented credential inventory through `/api/admin/identity`.
- The React control plane exposes `/admin/me` so normal Entra users can create, rotate and revoke personal keys and inspect their own usage without calling administrative APIs.

### Changed

- The Entra role model now distinguishes normal product users (`LlmProxy.User`) from read-only operators (`LlmProxy.Reader`) and administrators (`LlmProxy.Admin`).
- Credential runtime snapshots carry owner identity so key-to-user attribution survives normal local/Redis runtime-state publication.

### Security

- API-key ownership authorization uses immutable Entra `tid` + `oid`, not mutable usernames or email addresses.
- Raw personal keys are returned only once at creation/rotation, are never persisted, and continue to use the existing HMAC/pepper hashing model.
- A user can list, rotate or revoke only credentials owned by the same Entra tenant/object identity; administrative service-key behavior remains separate.

### Known scope

- Request/output-token governance in this release remains credential/model scoped; aggregate user request quotas are added in `0.2.0-preview.7`. Currency/spend enforcement still requires an explicit pricing/chargeback model and is not inferred from token counts.

## [0.2.0-preview.5] - 2026-09-16

### Added

- `docker/scripts/environment-acceptance.sh` turns target-host acceptance into an executable production step after installation.
- The acceptance runner checks Linux/Docker/Compose plus direct VM-to-DGX and gateway Models, Chat, Responses and SSE surfaces, including provider/logical-model advertisement.
- `docs/environment-acceptance.md` defines the evidence contract, pass criteria and the follow-on benchmark/Entra/Cloudflare/Copilot/runner acceptance sequence.
- CI exercises the acceptance runner against the repository mock runtime so syntax, HTTP probes, streaming validation and evidence generation are continuously checked.

### Changed

- Environment acceptance is no longer only a manual checklist: operators get a repeatable PASS/FAIL evidence bundle with HTTP status, content type, TTFB and total request timing.

### Fixed

- API credential usage timestamps no longer republish stale runtime snapshots, preventing a concurrent `LastUsedAtUtc` write from reverting caller-governance, ownership, group, enablement or expiry state in the local inference cache.

- DGX `/health` acceptance now matches canonical vLLM behavior: a successful bodyless status response is accepted without incorrectly requiring JSON or a content type.

### Security

- Acceptance evidence is metadata-only and intentionally excludes prompts, request/response bodies, generated output and bearer secrets. Synthetic payloads and response bodies live only in a temporary owner-only directory, and evidence generation fails if a supplied bearer secret is detected in the bundle.

## [0.2.0-preview.4] - 2026-09-16

### Added

- A single Linux production deployment path now installs the validated full stack: LlmProxy, PostgreSQL, Redis and bundled observability.
- `docker/scripts/install-linux.sh` can prepare a new Linux host across the common Debian/Ubuntu, RHEL/Fedora, SUSE, Arch and Alpine package-manager families: it installs or preserves Docker Engine/Compose v2, prepares `/opt/llmproxy`, generates initial secrets, optionally logs into GHCR, validates DGX connectivity and runs the canonical deployment.
- `docker/.env.production.example` provides an operator-oriented production template separate from development/full-stack examples.
- The production deploy script stages Compose and observability assets under `/opt/llmproxy/runtime`, so running containers do not depend on a transient GitHub Actions workspace.
- Cloudflare Tunnel can be enabled as an optional Compose profile when a tunnel token is present.
- A dedicated Linux production guide covers zero-to-running host bootstrap, private-LAN acceptance, DGX connectivity, Entra/public exposure, self-hosted runner setup, backup, update and rollback.

### Changed

- `docker/scripts/deploy.sh` and `.github/workflows/deploy.yml` now deploy the same Redis-enabled full-stack topology used by the supported production documentation instead of the legacy minimal Compose overlay.
- Production deployment performs Compose validation before pull/up and verifies both `/healthz` and `/readyz` before succeeding.
- The production DGX address is an explicit `CHANGE_ME` placeholder rather than a plausible example IP, preventing accidental unattended deployment against sample infrastructure.

### Security

- Grafana can bind to loopback independently from the gateway; generated installer secrets are not printed; and the production path requires Entra configuration before exposing administrative surfaces through a public tunnel.

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
- Usage reporting transparently combines recent raw metrics with historical rollups and newer raw metrics without double counting.
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
- Inference credentials are persisted as HMAC hashes; raw credential material is returned only at creation/rotation time.

### External validation still required

- Real DGX Spark/vLLM/model benchmark sweeps and representative multi-DGX coding load.
- Real GitHub Copilot BYOK end-to-end.
- Real Entra app/role configuration and Cloudflare/public-hostname acceptance.
- Customer production backup destination/encryption/retention and native deployment-host acceptance where applicable.
