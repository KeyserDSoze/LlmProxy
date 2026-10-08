# Development log

## 2026-10-08 — Streaming Request Audit JSON reconstruction — IMPLEMENTED / VALIDATION IN PROGRESS

- Replaced unbounded raw SSE byte accumulation with a request-scoped incremental assembler that stores a compact JSON response (Chat Completions and Responses).
- Preserves downstream SSE verbatim; restores tool arguments, text, usage and event/termination metadata; categorizes completed, cancelled, interrupted and incomplete outputs.
- Saves the part already received if a client disconnects or an upstream stream fails, including distinct lease-loss and upstream failure reasons.
- Bounds audit memory/output and marks truncated content; streaming/non-streaming payloads continue to be encrypted in PostgreSQL under existing access/retention rules.
- Added readable/JSON tabs and stream details in the shared Request Audit modal (admin and normal-user portal).
- Intentionally no migration or compatibility parser for old raw SSE content: product is pre-production.
- Added backend unit tests for UTF-8 fragmentation, chat/tool calls, Responses partial/full, cancellation, interruption, truncation and downstream forwarding, plus frontend modal coverage.
- Validation pending exact-head CI and integration evidence.


## 2026-10-04 — Request audit visibility and long retention — IMPLEMENTED / VALIDATION IN PROGRESS

Expanded the existing encrypted full-body content-log subsystem into an explicit **Request Audit** product surface.

Implemented:

- Admin navigation now separates **Request Audit** (inference request/response payloads) from **Administrative Audit** (configuration and sensitive-control actions).
- Admin Request Audit uses a server-side paginated query with filters for user owner, API credential, logical model, surface, HTTP success/error, exact request ID and time range.
- exact request/response detail remains decrypted only on demand and returned with `Cache-Control: no-store`.
- admitted Entra users can list and inspect request-audit payloads through `/api/me/content-logs*`, but only when the row is linked to a personal API credential owned by the same stable `tid + oid`.
- organization/shared credentials and other users' payloads are excluded from self-service; direct non-owned detail lookup returns not found.
- **My dashboard** now exposes **My request audit** with credential/model/surface/status filters and exact-body inspection.
- administrator retention bounds changed from 10-180 days to 10-4015 days (11 x 365), retaining the existing 30-day default and four-hour cleanup cadence.
- frontend/backend integration fixtures and focused security/retention/identity documentation were updated for the new contract.

Security invariants remain unchanged for secrets: request headers, Authorization values, plaintext API keys and upstream bearer credentials are not persisted in the request-audit store; ordinary metrics/audit/OTEL remain metadata-only.

Validation is still required on the exact resulting main SHA. Do not mark this increment DONE until backend, frontend/Vitest, Playwright, Docker/PostgreSQL and distributed Full Stack CI are green.

## 2026-10-03 — Administrator scheduled/self-service updates — DONE / VALIDATED

Added a release-aware control-plane update path designed to survive replacement of the LlmProxy gateway container.

Implemented:

- **Release Notes & Updates** now shows the installed distribution version and discovers later stable immutable GitHub Releases;
- each new release publishes a checksum-covered `llmproxy-update-plan.json` describing a `standard` or `custom` update procedure, restart requirements and the operator-visible command;
- custom host migrations are constrained to the immutable bundle's fixed `distribution/update.sh` entry point; neither the browser nor the Admin API accepts arbitrary shell commands;
- AdminWrite users can run **Update now**, schedule an exact future update, cancel pending work and inspect recent update outcomes;
- the bearer-authenticated **LlmProxy Update Agent** is installed as a systemd service on the Linux control-plane host, outside Docker, and persists scheduling state under `/var/lib/llmproxy-update-agent`;
- the agent replaces its own binary atomically during an upgrade, records the final job outcome, then schedules a self-restart so later updates use the newly installed updater;
- target-version selection expands to an ordered chain of every intervening published stable release, so a custom migration attached to an intermediate version cannot be skipped;
- manual `llmproxyctl update VERSION` and Admin-triggered updates both use the target release's update-plan contract;
- the release workflow publishes the update-plan asset and embeds self-contained x86_64 + ARM64 Update Agent binaries in the Linux operator bundle.

Security/reliability boundary:

- the Update Agent listens on the Docker bridge address and requires a generated bearer stored only in protected host configuration;
- the Admin API accepts only published stable versions newer than the running version;
- update execution uses argument lists and fixed bundle entry points rather than user-supplied shell text;
- gateway/container restart does not terminate the host update worker;
- PostgreSQL/Redis/observability volumes and `/opt/llmproxy/.env` remain governed by the existing installer/update preservation contract.

Focused contract: `docs/update-management.md`.

Validation evidence:

```text
validated feature head         301bb179c6b848dd245f30c185b923b09491792c
PR #5 CI                       37138750752 SUCCESS
Backend build/unit             SUCCESS
Frontend build/Vitest          SUCCESS
Playwright E2E                 SUCCESS
Docker/PostgreSQL integration  SUCCESS
Redis/OTEL/Grafana full stack  SUCCESS
```

The final documentation-status commit must itself re-pass CI before merge. A green `main` CI is still required by the automatic immutable release gate before the new distribution version is published.

## 2026-10-03 — Configurable end-user provisioning and suspension — CANDIDATE

Extended the Entra personal-key model with a first-class `platform_users` registry and administrator-controlled admission policy.

Implemented:

- persisted singleton provisioning mode, default `manual`, with `automatic` first-login registration as an administrator-selectable alternative;
- stable normal-user identity keyed by Entra `tid + oid`; email/principal/display name remain mutable metadata;
- startup migration of pre-existing personal-key owners into the registry;
- `SelfService` authorization now requires successful Entra authentication plus an enabled platform-user record; full administrators bypass the normal-user registry;
- Admin **Users & Access** UI/API for mode changes, manual registration, inventory, 30-day request counts, personal-key counts and enable/disable;
- disable revokes all active personal API keys for the user as part of the same control-plane operation, so both portal and personal-key inference access are blocked;
- re-enable restores portal admission but deliberately leaves revoked keys revoked;
- `/api/me/requests` and **My dashboard** recent-call visibility;
- focused GitHub Copilot attribution documentation: a shared custom-model/BYOK API key is reliable workload/credential identity, not a documented individual developer identity signal.

Public GitHub documentation was reviewed for the custom-model/BYOK and Copilot usage-metrics contracts. The design does not assume undocumented provider-facing user headers. Per-user GitHub metrics may be used later for aggregate adoption reporting, while deterministic request-time LlmProxy attribution requires per-user credentials or a trusted signed identity assertion.

Validation is pending on the exact feature head. Do not promote/release this increment until backend, frontend/Playwright, Docker/PostgreSQL and distributed Full Stack CI are green.

## 2026-10-03 — Administrator observability, testing and in-app documentation — DONE / VALIDATED

Implemented the operator-facing visibility requested for the current LlmProxy control plane on `feature/admin-observability-docs` / PR #1.

Product behavior:

- newly created/rotated client API keys retain their HMAC authentication material and additionally store a purpose-bound AES-GCM recovery copy derived from the deployment API-key pepper;
- `LlmProxy.Admin` and configured super admins can reveal/copy recoverable keys later; reveal responses are no-store and each reveal writes safe audit metadata without the secret;
- pre-feature keys remain cryptographically non-recoverable from HMAC and show **Rotate once**; the configured bootstrap key can be backfilled when its original secret is still supplied at startup;
- Chat Completions, Responses and System One requests are captured at the gateway boundary and persisted only as encrypted request/response ciphertext;
- content-log APIs require `AdminWrite`, excluding `LlmProxy.Reader`;
- full-body retention defaults to 30 days, is configurable from 10 through 180 days, and cleanup runs at startup then every four hours;
- Admin Content Logs polls every two seconds and exposes exact request/response bodies plus correlated routing/TTFT/token metadata where request metrics exist;
- Admin Playground can execute a real logical-model chat through production routing/capacity admission and send editable JSON directly to the configured System One classifier;
- Help & Endpoints documents Models, Chat Completions, Responses and System One usage plus authentication/routing/capacity/rate-limit/observability semantics;
- every principal UI screen now has a closed-by-default contextual documentation accordion.

Security decision:

- ordinary request metrics, audit and OTEL remain metadata-only;
- full prompt/source/output persistence is allowed only inside the dedicated encrypted administrator content-log store under bounded retention;
- request headers, client API keys and upstream bearer tokens are never copied into content logs;
- the API-key pepper is now also a decryption/recovery dependency and must remain backed up outside PostgreSQL.

Validation evidence:

```text
validated feature head         6641739bd80f7eaf2b8a92594a5a75541c82546d
PR #1 CI                       37073147425 SUCCESS
Backend build/unit             SUCCESS
Frontend build/Vitest          SUCCESS
Playwright E2E                 SUCCESS
Docker/PostgreSQL integration  SUCCESS
Redis/OTEL/Grafana full stack  SUCCESS
```

The backend integration smoke proves both requested diagnostic flows: a System One classifier call reaches the classifier mock and returns the expected decision payload, while a model-chat diagnostic traverses normal logical-model routing/capacity and reaches the selected inference mock. The same smoke verifies administrator recovery of the encrypted bootstrap API key, encrypted exact-body content logging and the 10-180 day retention contract.

An earlier feature run exposed two test-fixture gaps (the new `/api/admin/session` mock and one strict Playwright locator); both were corrected before the green exact-head run above. The final documentation-status commit must itself re-pass CI before merge. A green main CI is still required by the automatic immutable release gate.


## 2026-09-21 — Aggregate Entra user request quotas / 0.2.0-preview.7 — VALIDATED

Extended preview.6 personal-key ownership with aggregate request-count governance across every personal API key owned by the same stable Entra `tid + oid`.

Architecture:

- added persisted `UserRateLimitPolicy` with optional logical-model scope;
- reused the existing transactional RatePolicy outbox channel so PostgreSQL remains authoritative, Redis remains shared L2 and local RAM remains request-path L1;
- changed request-rate counter acquisition to accept all applicable policies atomically;
- enforced `user policy AND credential policy` without consuming either counter when one applicable policy rejects;
- extended Redis fixed-window admission with one Lua transaction spanning the user and credential counters;
- kept output-token budgets credential/model scoped and left monetary/spend budgets intentionally undefined until a pricing/chargeback model exists;
- added Admin CRUD/UI for user request limits and read-only `/api/me/rate-limits` + personal-portal visibility;
- extended PostgreSQL governance smoke for two keys sharing one Entra identity and restart republish;
- extended the existing two-gateway Redis smoke to prove one shared aggregate user counter across replicas.

The first validation attempt caught three test-harness issues before promotion: xUnit analyzer violations in the new async tests, missing Vitest API mocks/ambiguous labels after adding the second quota form, and a transient runtime-sync/outbox convergence race. Those were corrected without changing quota semantics.

Final validation:

```text
version                 0.2.0-preview.7
runtime source          df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                      35592623906 SUCCESS
Full Stack              35592624282 SUCCESS
Publish GHCR            35593081824 SUCCESS
image alias             sha-df3ecf7
image digest            sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

CI proves .NET/unit/frontend/Playwright plus Docker/PostgreSQL governance, restart republish, retention and restore regressives. Full Stack proves transactional outbox recovery and one Redis user request counter shared across two gateway replicas in addition to the existing distributed token-budget, credential-rotation and safe-maintenance proofs.

## 2026-09-21 — Entra-owned personal API keys / 0.2.0-preview.6 — VALIDATED

Implemented the Entra identity/API-key requirement by extending the existing credential and governance model rather than replacing it.

Architecture:

- retained administrator-created **service credentials** for shared/unattended integrations;
- added **personal credentials** permanently bound to stable Entra `tid + oid`;
- added `LlmProxy.User` alongside `LlmProxy.Admin` and `LlmProxy.Reader`;
- added `/api/me` self-service for identity, personal-key list/create/rotate/revoke and own usage;
- added `/admin/me` React self-service UI that does not call administrative APIs;
- added administrator identity inventory under `/api/admin/identity`;
- kept durable request attribution on `ApiCredentialId`, resolving personal-user ownership through the credential instead of duplicating user PII in request rows;
- carried optional owner identity through the local/Redis runtime credential snapshot while preserving compatibility with existing unowned service credentials;
- kept raw secrets one-time-only and HMAC/pepper-backed.

Existing request-rate and output-token policies remain credential/model scoped and therefore apply to personal keys. Aggregated per-user quotas across multiple keys are intentionally not claimed yet because they require explicit precedence/distributed-counter semantics. Monetary/spend budgets are also not implemented because an on-prem vLLM deployment has no authoritative currency cost without an explicit pricing/chargeback model.

The first implementation CI caught two compatibility/test issues before promotion: an E2E strict-selector/text regression and required new owner parameters on the credential snapshot. The UI test was stabilized and owner snapshot fields were made optional-null, preserving existing service-key/test/runtime payload compatibility and rolling-upgrade behavior.

Final validation:

```text
version                 0.2.0-preview.6
runtime source          7da5682f043eeb7e0d0b684eabb0ab6a6b659b35
CI                      35569885810 SUCCESS
Full Stack              35569885843 SUCCESS
Publish GHCR            35570238079 SUCCESS
image alias             sha-7da5682
image digest            sha256:c28c60e004496ae0d3949f616b36cf4ba67ed523f8567218e904bd80730f5a81
attestation manifest    sha256:1530fd684b90668743974ce3d00f8cdd49ca4116d8126619f9e768648e42642a
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10625781303
artifact digest         sha256:6ac1a5ebdceaae1e77108a1631f83ac993b997fd01d1fbc6a163f7b4cb7593b7
```

CI includes backend/unit/frontend/Playwright, PostgreSQL migration/integration, governance/rate-limit, outage, retention and restore smokes. Full Stack proves transactional outbox recovery, distributed output-token governance, cross-replica credential rotation and safe maintenance after the credential snapshot extension.

Real Entra tenant acceptance remains external: create/assign Admin/User/Reader app roles, verify browser login, create a personal key through `/admin/me`, call `/v1/*` with it and confirm administrator/own-usage attribution.

Chronological engineering trace for LlmProxy. Canonical current state and resume point live in `docs/project-status.md`; product-visible release history lives in `CHANGELOG.md` and `/admin/releases`.

## 2026-09-09 — Repository, gateway and multi-DGX foundation

Created the .NET 10 layered solution, React/TypeScript Admin, PostgreSQL persistence, Docker/GitHub Actions foundations, logical models, DGX nodes/deployments, `/v1/models`, Chat Completions + SSE and Responses compatibility. Added bearer credentials, Entra administration plumbing, health hysteresis, audit, routing strategies, pre-response-only failover, metadata-only request metrics and vLLM signals.

## 2026-09-09 — Repository-first handover discipline

Introduced root `AGENTS.md` and the rule that meaningful increments update focused docs, project status, development log and roadmap with actual validation evidence.

## 2026-09-10 — DGX/DCGM telemetry + benchmark harness — VALIDATED

```text
telemetry checkpoint 6c238a095273843e713a72fb2e26b2c7c434fc62
benchmark checkpoint 49e7932f14118be403eec042a1393946143776ae
```

Architecture decision: NVIDIA PAIR was evaluated; project owner chose custom LlmProxy + vLLM.

## 2026-09-13 — Capacity Profiles + physical-node admission — VALIDATED

Added persisted benchmark-derived Capacity Profiles and aggregate physical-node concurrency admission.

```text
600ad42cc53ad1e97a259819654ca5cf5480e1db
```

## 2026-09-14 — Caller governance + Usage Groups — VALIDATED

Implemented persisted Usage Groups, request-time group snapshots, credential/model request-rate policies, governance audit and usage reporting/UI.

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084 SUCCESS
```

## 2026-09-14 — Inference configuration hot path — VALIDATED

Removed synchronous SQL configuration lookups from ordinary inference. Credentials, nodes/models/deployments and caller policies are hydrated at startup and maintained in runtime L1. PostgreSQL-outage smoke proves published configuration remains usable.

```text
credential cache 1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
route catalog     42c44753cd00d679a81bf065f410b7a497cdc000 / CI 34871542047
```

## 2026-09-15 — Redis L2 + observability + distributed coordination — VALIDATED

Promoted Redis to shared runtime/coordination L2 while preserving local L1 and PostgreSQL authority. Added OTEL Collector, Tempo, Loki, Prometheus and Grafana; then shared request-rate counters, distributed capacity leases and active lease-loss cancellation.

```text
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## 2026-09-15 — Transactional runtime-state outbox — VALIDATED

Runtime Node/Model/Deployment/Credential/RatePolicy mutations write an outbox row in the same PostgreSQL transaction. A globally serialized advisory-lock worker publishes ordered Redis state, retries failures and marks rows processed only after acknowledged publication.

```text
implementation 9c6289ef172bed0502068df112b6e6aec4ee8521
CI             34968324786 SUCCESS
Full Stack     34968114492 SUCCESS
```

Later diagnostics/retention proof: CI `34976465066`, Full Stack `34976465149`.

## 2026-09-15 — Output-token budget V1 — VALIDATED

Added shared credential/model output-token budgets with pre-inference reservation, Chat/Responses cap injection, known-usage refund and conservative uncertain-usage charging. Redis coordination is fail closed.

```text
runtime proof 887ebfac98389c0115eaf9c102a60133ede745ff
CI            34987407172 SUCCESS
Full Stack    34987407169 SUCCESS
Admin UI      426c545e841865406615998ca50b28a45c40e6f4 / CI 34988084106 SUCCESS
```

## 2026-09-15 — Credential rotation — VALIDATED

Implemented in-place hard-cutover rotation preserving credential identity/group/policy/history linkage and returning the replacement secret once.

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

## 2026-09-15 — PostgreSQL backup/restore — VALIDATED

Added Bash and PowerShell custom-format PostgreSQL backup/restore operators with SHA-256/non-secret metadata and destructive clean-target restore proof. Redis is rebuilt from PostgreSQL; `Authentication__ApiKeyPepper` remains an external recovery dependency.

```text
cross-platform 66d7a809936f0f21f330d84887c1bb6a4e536f97
CI             35018579785 SUCCESS
```

## 2026-09-15 — Safe model/runtime maintenance — VALIDATED

Implemented distributed maintenance drain/resume with pre-block admission, shared maintenance marker inside capacity admission, drain-to-zero, `/health` + `/v1/models` + one-token warm-up validation and deprecated unsafe legacy drain.

```text
CI         35021524018 SUCCESS
Full Stack 35021524019 SUCCESS
```

## 2026-09-16 — Product SemVer + release/build identity — VALIDATED

Formalized `0.1.0-preview.1`, `/api/admin/product`, `/admin/releases`, compiled/runtime version identity, source SHA/build date, OCI identity labels and main/tag container publication rules.

```text
SemVer final test fix  ee9ac0d17a95b68a79a464dc430e5c8427c9ded9 / CI 35063494349
release identity       c37479bb474d44f9e36726bebba74cdf38e5661e / CI 35064353402 / Publish 35064707488
```

## 2026-09-16 — Historical usage rollups / 0.2.0-preview.1 — VALIDATED

Added daily PostgreSQL usage rollups, raw 90-day vs rollup 730-day retention, complete-day transactional rollup-before-delete, advisory-lock serialization across replicas, idempotent cleanup and raw+rollup reporting without double counting.

```text
commit        5d66c7dcdae42955c6e26849aba84bed4787ff00
CI            35075387110 SUCCESS
Full Stack    35075387186 SUCCESS
Publish GHCR  35075788954 SUCCESS
```

## 2026-09-16 — OCI SBOM + SLSA provenance / 0.2.0-preview.2 — VALIDATED

Added Buildx SPDX SBOM and SLSA/BuildKit provenance, immutable digest capture and post-push OCI-native verification against GHCR. The first convenience-rendering verifier failed despite valid pushed attestations; it was fixed by following OCI attestation descriptors/predicate annotations instead of weakening validation.

```text
commit                d134603672f361474bac9ea330f3bd1a142b5dfa
CI                    35080118201 SUCCESS
Publish GHCR          35080565404 SUCCESS
image digest          sha256:cc26617a5860e126957da2d0e59c8cd8accd1cd3280576d991819ddc2001d880
attestation manifest  sha256:83457ab3eb69c4aac638874daed1f2cf396fa157001f2be9257e48d0d067253d
release artifact      10440082178
```

## 2026-09-16 — Source-validated publication / 0.2.0-preview.3 — VALIDATED

Added `validate-release-main-ci.sh`. Every publication now queries GitHub Actions before GHCR login and requires successful `CI` from a push to `main` on the exact source SHA; workflow-run publication also requires the API-selected CI run to be the triggering run. Exact tags additionally require tag/version match.

```text
commit                e9c8805e8473d3ad4df118d6a623ccef08723761
CI                    35083646699 SUCCESS
Publish GHCR          35084132389 SUCCESS
image digest          sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
attestation manifest  sha256:6d60bd6cb26cce447e403081ae1aa6129920f2716a0a1ccfb579b196054997a9
release artifact      10441770750
```

No real immutable Git tag/GitHub Release was created.

## 2026-09-16 — Consolidated Linux production deployment / 0.2.0-preview.4 — VALIDATED

Consolidated production deployment onto the Redis-enabled full stack. Added the canonical `docker/scripts/deploy.sh`, cross-distribution `docker/scripts/install-linux.sh`, production `.env` contract, optional Cloudflare profile, Entra-before-public validation, runtime-asset staging, `/healthz` + `/readyz` success gate and complete zero-to-running runbook.

```text
version               0.2.0-preview.4
final source          58a80a60c2f3a049b279be6bf9583ffa4c1cc088
CI                    35095161900 SUCCESS
Full Stack            35088765577 SUCCESS
Publish GHCR          35095620725 SUCCESS
image digest          sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
attestation manifest  sha256:cd92f248e73e58fca570a687ca0002d10cfc8e5b308e60ce31351454b4933b0b
release artifact      10445034650
artifact digest       sha256:cb2bf6b6d8d34a545c080b866866d7098cedbab66f66f475aa168caf6a93c977
```

Repository validation deliberately did not claim actual target-distro execution.

## 2026-09-16 — Production environment acceptance / 0.2.0-preview.5 — VALIDATED

Implemented `docker/scripts/environment-acceptance.sh` as the executable bridge from repository validation to physical-environment evidence.

The harness:

- records Linux distro/kernel/architecture plus Docker Engine and Compose v2 availability;
- probes direct VM -> DGX/vLLM `/health`, `/v1/models`, Chat and Responses, streaming and non-streaming;
- probes LlmProxy `/healthz`, `/readyz`, `/v1/models`, Chat and Responses, streaming and non-streaming;
- requires exact provider-model visibility directly and logical public-model visibility through the gateway;
- records status/content-type/TTFB/total-time metadata only;
- keeps synthetic requests and response bodies temporary;
- rejects evidence containing gateway/DGX bearer values;
- writes only `summary.md` + `checks.tsv`.

During current-vLLM compatibility review, canonical `/health` behavior was verified to permit an empty/bodyless HTTP 200 response. The initial harness incorrectly expected JSON and would have false-failed a standard healthy vLLM server. The implementation and CI mock were corrected to validate status only and explicitly exercise bodyless health.

Validated runtime/release evidence:

```text
version                0.2.0-preview.5
runtime source         723c47d919a59cf95e447c071ef377ab92a06498
CI                     35099356925 SUCCESS
Publish GHCR           35099987458 SUCCESS
image alias            sha-723c47d
image digest           sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa
attestation manifest   sha256:b15e45a4024235fd2ba28c6a7711ab64922da4be4003d68b8f7ec0eb78db7712
SBOM predicate         https://spdx.dev/Document
provenance predicate   https://slsa.dev/provenance/v1
release artifact       10448046779
artifact digest        sha256:c90c6ae1db7246afe34f3764543d0ec4a20eed7c6026cf8030e86cc55220562c
```

CI `35099356925` includes the acceptance harness smoke with bodyless `/health`, direct/gateway Chat + Responses SSE/non-SSE, secret scan and the complete existing regression suite.

## 2026-09-16 — Self-hosted production acceptance workflow — VALIDATED

Added `.github/workflows/environment-acceptance.yml` so the same acceptance contract can be launched manually from GitHub Actions once the dedicated production runner exists.

Workflow contract:

- labels: `self-hosted, linux, x64, llmproxy-prod`;
- GitHub environment: `production`;
- no API-key workflow-dispatch inputs;
- protected host configuration read from `/opt/llmproxy/.env` by the root-owned acceptance process;
- non-interactive sudo required on the dedicated runner;
- unexpected evidence files rejected;
- only `summary.md` + `checks.tsv` uploaded;
- 14-day artifact retention;
- available metadata uploaded even when functional acceptance fails, followed by a failing run result;
- runner-local evidence deleted afterward.

Repository checkpoint:

```text
commit                  cdd21d6155de08c6202754560f5b3c9f590071f9
CI                      35110131158 SUCCESS
changed paths           .github/workflows/environment-acceptance.yml
                        README.md
                        docs/environment-acceptance.md
```

CI `35110131158` revalidated backend, frontend/Playwright and the full Docker/PostgreSQL suite: Linux production deployment rendering, environment-acceptance smoke, image identity, backend integration, DGX telemetry, capacity/backpressure, governance, PostgreSQL-outage routing, retention and Bash/PowerShell restore.

This workflow commit is an operator/repository checkpoint, not a replacement for the validated runtime image `sha-723c47d`. The first actual run against the target VM + DGX/vLLM remains external.

## Historical next increment after preview.5

Repository hardening, Linux bootstrap and executable acceptance automation are complete for the current preview. At that checkpoint, the next work was physical/environment acceptance:

1. install the then-current immutable runtime image on the target Linux host;
2. install/validate the `llmproxy-prod` self-hosted runner;
3. run `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM and retain the metadata evidence;
4. benchmark intended models and apply evidence-backed Capacity Profiles;
5. validate real Entra/Cloudflare/GitHub Copilot BYOK;
6. finalize customer-specific HA/storage/backup topology.

Quota expansion remains requirements-driven. Creating a real immutable Git tag/GitHub Release remains an explicit product-owner publication decision.


## 2026-09-30 — Release-based Linux distribution / 0.2.0-preview.8 — IMPLEMENTED IN SOURCE / VALIDATION PENDING

Prepared the next product candidate around immutable operator distribution instead of requiring a Git checkout on each server.

Implemented source changes:

- Linux release bundle builder with SHA-256 checksums;
- release bootstrap that downloads/verifies one exact GitHub Release version;
- `llmproxyctl` for status/health/logs/lifecycle/doctor/version/update/local rollback;
- installed immutable operator bundles under `/opt/llmproxy/releases/<version>` while preserving host-owned `.env` and Docker volumes;
- owner-triggered `Create immutable release tag` workflow requiring exact-SHA CI and Full Stack success;
- tagged publication target expanded to `linux/amd64,linux/arm64`;
- tagged publication prepared to create GitHub Release assets after existing SBOM/provenance verification;
- focused `docs/release-installation.md` runbook.

The existing `install-linux.sh` + `deploy.sh` remain the single host/deployment implementation inside the release bundle.

Validation is intentionally not claimed yet. Required evidence: green candidate CI/Full Stack, multi-arch publication, immutable tag/GitHub Release, then real install/update/rollback on the ARM64 GB10 target.


## 2026-09-30 — Full-stack aggregate user quota smoke convergence hardening

The first `0.2.0-preview.8` validation run exposed a timing race in the existing two-gateway aggregate-user-quota smoke: the first peer request could arrive before the newly-created policy reached that peer's local L1, so it returned 200 without incrementing the shared user counter.

The smoke now proves peer policy application explicitly by probing until the peer enforces the quota, resets only the isolated Redis test counter used by that convergence probe, and then runs the actual primary/peer/primary 200/200/429 assertion from a deterministic empty window. Product quota semantics were not changed.


## 2026-09-30 — Protected local inference + same-host Docker reachability — IMPLEMENTED / VALIDATION IN PROGRESS

Closed the two blockers for a protected llama.cpp/vLLM runtime on the same GB10 host:

- added per-node write-only upstream bearer credentials;
- encrypt provider bearers with AES-GCM before PostgreSQL persistence and Redis/L1 runtime propagation;
- keep the stable encryption master key outside PostgreSQL as `LLMPROXY_UPSTREAM_CREDENTIAL_KEY`;
- apply the node credential to health, model discovery, vLLM metrics, maintenance warm-up and inference while never forwarding the client-facing LlmProxy API key;
- added Admin API/UI set/rotate/clear flows that expose only credential presence;
- added optional one-time `DGX_UPSTREAM_BEARER_TOKEN` bootstrap and remove it from the long-lived container environment after encrypted bootstrap;
- changed same-host Linux preflight to resolve Docker's bridge gateway for `host.docker.internal`, so a llama-server still bound only to loopback fails with an actionable bind-address message;
- added a focused protected-upstream integration smoke;
- made upgrades from earlier installs generate the new stable upstream-credential encryption key when absent;
- aligned deployment validation with the existing runtime rule that Production requires Entra.

The inference runtime/model weights remain externally managed; the gateway now owns the secure connectivity/authentication boundary to that runtime.


## 2026-09-30 — Release operator update hardening

Release-candidate review found and fixed two operator-path issues before publication:

- `llmproxyctl` used a misspelled `INSTAL_DIR` variable for the `current` symlink; CI now executes `config-path` rather than relying only on `bash -n`, catching unbound-variable startup failures.
- protected upstream bearers are write-only after first bootstrap, so release updates cannot repeat a plaintext-authenticated direct provider precheck. First install still performs that check; `llmproxyctl update` skips only the direct DGX check and relies on the persisted encrypted node credential plus post-deploy gateway readiness.
- the release bootstrap help no longer recommends `--skip-dgx-check` for the same-host GB10 first-install path and documents the one-time `DGX_UPSTREAM_BEARER_TOKEN` input.


## 2026-09-30 — Root-safe release bootstrap

Hardened the immutable release bootstrap for operator updates: when already running as root (including `sudo -E llmproxyctl update ...`) it now invokes the bundled installer directly instead of requiring a nested `sudo`. Non-root first installs still use `sudo -E`, with a clear error when sudo is unavailable.


## 2026-09-30 — Maintenance smoke L1 convergence hardening

The final preview.8 Full Stack run exposed a test-only race after validated node resume: the peer's Admin node list reads PostgreSQL and can show `Healthy` before that peer has consumed the route-node event into its local inference L1. The maintenance smoke now waits for an actual peer inference request to succeed, proving cross-replica re-entry on the real request path rather than treating DB visibility as L1 convergence. Product maintenance semantics were unchanged.


## 2026-09-30 — Automatic immutable release train — IMPLEMENTED / LIVE VALIDATION

Replaced owner-triggered version publication with release-on-green-main automation.

New contract:

- every push to `main` receives a complete CI run and older main pushes are no longer cancelled by newer pushes;
- distributed Full Stack acceptance is a required CI job rather than a separate release prerequisite workflow;
- one successful CI run triggers automatic immutable tag allocation;
- the first generated release is `v0.0.1`; normal successful pushes increment patch;
- final commit messages can request `release:minor` or `release:major`;
- source-history version files are not rewritten by bots;
- distribution SemVer is injected into the packaged .NET assembly and OCI labels;
- concurrent tag allocation retries against remote tags rather than reusing an existing version;
- one source SHA receives at most one stable release tag;
- tag allocation calls the reusable publication workflow directly, avoiding GitHub's `GITHUB_TOKEN` recursive-workflow suppression;
- exact-version GHCR tags and GitHub Releases are refused if already present;
- each release publishes amd64 + arm64, verifies SPDX/SLSA evidence and attaches the checksummed Linux bundle/bootstrap.

The historical `0.2.0-preview.*` line remains source/product-history metadata. The installation/update release train is now independent and begins at `0.0.1`.


## 2026-09-30 — Automatic release credential boundary

Live validation of the first automatic tag allocation exposed GitHub's workflow-file protection: the built-in Actions `GITHUB_TOKEN` is a GitHub App installation token and cannot be granted Workflows write permission, so GitHub rejected tagging a source commit that changed `.github/workflows/container.yml`.

Hardened the release train:

- added repository secret contract `RELEASE_TOKEN` using a repository-scoped fine-grained PAT with Contents read/write + Workflows read/write;
- use that credential only for immutable Git tag and GitHub Release operations;
- continue using the short-lived `GITHUB_TOKEN` for CI source validation and GHCR publication;
- added `distribution/AUTOMATIC_RELEASE_SERIES` so migration commits before the final automation setup are intentionally skipped and cannot consume `0.0.1`;
- the first eligible green commit therefore remains the intended `v0.0.1` start of the automatic distribution series.


## 2026-09-30 — Installer progress and persistent diagnostics

Hardened Linux installation as an operator-facing product surface:

- privileged installs emit eight explicit progress stages from host inspection through finalization;
- output is timestamped and persisted under `/var/log/llmproxy/install-<timestamp>.log`, with `latest-install.log` pointing to the newest attempt;
- failure summaries report active stage, exit code, persistent log path and Docker container snapshot without printing secret values;
- deployment reports Compose validation, image pull/start, and liveness/readiness wait progress; readiness timeout retains Compose status + gateway log tail;
- bootstrap failures before privileged installation preserve their temp directory and `bootstrap.log`;
- public release downloads use unauthenticated `curl` when GitHub CLI is absent or not authenticated;
- release docs now document progress, log paths, failure evidence and public download behavior.


## 2026-09-30 — Cloudflare reverse-proxy + Entra browser flow

Physical external acceptance through `llmproxy.opencode.zone` exposed two coupled issues: the Admin SPA loaded through Cloudflare but protected XHR calls were challenged directly to Microsoft, which browsers surface as a cross-origin `Failed to fetch`; and the gateway did not process the tunnel's forwarded HTTPS scheme/host before OIDC.

Fixed the public browser path:

- added explicit one-hop forwarded-header processing behind an opt-in `ReverseProxy:Enabled` setting;
- Cloudflare-enabled Linux installs automatically persist `REVERSE_PROXY_ENABLED=true`;
- protected `/admin` navigation now challenges Entra at the top-level browser request;
- authorization middleware returns plain 401/403 for `/api/*` instead of issuing an OIDC redirect inside fetch;
- frontend differentiates unauthenticated 401 from authenticated-but-forbidden 403;
- documented `http://llmproxy:8080` as the Cloudflare origin and `https://<host>/signin-oidc` as the Entra Web redirect URI.

## 2026-10-03 — Administrator UX consolidation — IMPLEMENTED / VALIDATION IN PROGRESS

Refactored the Admin control plane to reduce page length and remove overlapping workflows without changing routing semantics.

Implemented:

- active/disabled Inference Node tabs, add/credential dialogs, and safe deletion for disabled idle nodes after managed installations are removed;
- Hardware tabs for telemetry, physical capacity and benchmark profiles, with configuration in dialogs;
- Model & Hardware inventory/deploy tabs and direct Node Agent installer download;
- unified Models & Deployments workspace with model-centric and node-centric views plus rapid deployment/routing controls;
- explicit organization/personal API-key scope, administrator secret recovery controls, and scope-aware `lp_org_` / `lp_usr_` prefixes for new/rotated secrets;
- paginated/filterable request-metrics API and Admin request browser, defaulting to the newest 20 rows;
- tabbed Playground, Usage & Governance, and Users & Access, moving create/configure forms into dialogs;
- contextual documentation, unit/e2e coverage and release notes updated with the same product increment.

Compatibility notes:

- existing `lp_` keys remain valid; the scoped prefix is adopted only by newly created or rotated credentials;
- the original `GET /api/admin/metrics?take=` contract remains available while the UI uses `GET /api/admin/metrics/query`;
- the legacy Deployments view identifier resolves to the unified Models & Deployments workspace;
- Routing behavior is intentionally unchanged.

Validation is not claimed until the exact resulting `main` SHA completes CI/Full Stack and the automatic immutable release workflow.


## 2026-10-03 — Safe automatic updates and routed System One

Implemented on the post-v0.0.14 development head:

- added a durable administrator update policy with Manual, ASAP/five-minute, Nightly, Weekly and Monthly modes;
- automatic policy always targets the newest stable release while handing the Update Agent the complete ascending intermediate release chain;
- changed future manual `llmproxyctl update VERSION` to resolve and execute every published stable release between installed and target, failing safe if the chain cannot be proven;
- added retry/backoff to GitHub bootstrap downloads to tolerate transient curl/network reset failures without weakening checksum verification;
- added `ModelSurface` so OpenAI and System One models share the same catalog without being exposed through the wrong public API;
- moved `POST /v1/systemone` onto normal routing, distributed capacity, upstream bearer protection, failover, request-rate governance and request metrics;
- added `GET /v1/systemone/models`, Admin surface labels, System One deployment visibility, deployment-specific runtime roots and a routed classifier Playground diagnostic;
- retained upgrade compatibility by importing the historical `SYSTEM_ONE_*` configuration into a normal node + model + deployment on startup when no System One model exists;
- added update-schedule and route-surface unit coverage and refreshed frontend E2E contracts.

Validation is intentionally not claimed until the exact final main SHA completes the repository CI/full-stack gate and the immutable release workflow.


### 2026-10-03 — Credential runtime-cache race found by governance acceptance

The Docker/PostgreSQL governance smoke exposed a real local-L1 race: background credential-usage persistence can update `LastUsedAtUtc` from an entity loaded before an administrator changes caller governance. The credential cache interceptor previously republished every modified credential, so that non-runtime timestamp write could restore a stale `EnforceCallerGovernance` (or other runtime snapshot field) in memory even though PostgreSQL contained the newer value.

The interceptor now republishes only when runtime-significant credential fields change (hash, enabled/expiry, ownership, usage group or caller-governance). Usage timestamps remain durable/observable but cannot mutate authentication/governance runtime state. The existing governance smoke is the regression acceptance: the first two governed calls must succeed and the third must be rejected immediately after enabling caller governance.


## 2026-10-05 — Physical infrastructure and capacity consolidation

Implemented the operator-facing correction for capacity confusion exposed by GitHub Copilot `429 capacity_exhausted` responses:

- consolidated Inference Nodes, Hardware and Model & Hardware navigation into one **Infrastructure** workspace with Fleet & access, Capacity & telemetry, and Inventory & model lifecycle tabs;
- made the physical node concurrency ceiling editable after creation and labelled it as simultaneous inference requests rather than people;
- exposed separate per-deployment concurrency editing while retaining inheritance from the hardware limit;
- changed live node-capacity reporting to use distributed maintenance/capacity coordination state when available and retain the per-gateway counter as diagnostics;
- added a coordinated same-host consolidation operation for legacy pseudo-nodes: drain source admissions, wait for zero active requests, preserve the runtime root and encrypted upstream bearer at deployment scope, preserve any previously inherited deployment ceiling, move the deployment, then remove the duplicate physical node row;
- made legacy System One bootstrap reuse an existing same-host physical node when possible;
- documented the intended split: Users & Access = admitted people, Infrastructure = physical simultaneous requests, Usage & Governance = caller request/token quotas.

Local validation on the implementation head: Admin production build passed and Vitest passed 26/26 tests. Backend local build could not start CoreCLR in the constrained runner (`0x8007000E`), and Playwright browser installation returned a zero-byte/truncated CDN archive; neither is recorded as a product test failure. Exact-head GitHub CI/Full Stack is the release gate and validation authority.
