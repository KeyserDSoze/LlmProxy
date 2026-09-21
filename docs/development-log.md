# Development log

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
