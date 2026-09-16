# Development log

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

Consolidated production deployment onto the Redis-enabled full stack instead of maintaining a separate minimal production overlay.

Implemented:

- canonical Linux production topology: LlmProxy + PostgreSQL + Redis + OTEL Collector + Prometheus/Tempo/Loki/Grafana;
- optional Cloudflare Tunnel Compose profile;
- separate production `.env` template with fail-safe secret/DGX placeholders;
- `docker/scripts/deploy.sh` as the single manual/self-hosted-runner deployment implementation;
- staging of Compose + observability assets into `/opt/llmproxy/runtime` so running containers do not depend on a transient runner workspace;
- production preflight requiring resolved placeholders and `ASPNETCORE_ENVIRONMENT=Production`;
- Entra-before-public-Cloudflare validation;
- Compose rendering before container changes;
- both `/healthz` and `/readyz` required before deployment success;
- GitHub Actions deploy workflow aligned to the same full-stack script.

Added cross-distribution host bootstrap `docker/scripts/install-linux.sh`:

- reads `/etc/os-release` and detects common package managers;
- Docker official-repository installation for Debian, Ubuntu, Fedora, CentOS and RHEL;
- controlled distro-package fallbacks for `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk` families;
- Docker Compose v2 CLI-plugin fallback when required;
- preserves working existing Docker/Compose and an existing production `.env`;
- prepares `/opt/llmproxy/{runtime,backups}`;
- generates initial PostgreSQL/Redis/API-key/pepper/Grafana secrets without printing them;
- optional GHCR login using transient `GHCR_USER`/`GHCR_TOKEN` inputs;
- DGX `/health` and `/v1/models` preflight;
- `--prepare-only`, `--skip-dgx-check`, `--skip-docker-install`, `--non-interactive`, `--validate-only` and help modes;
- invokes the same canonical deploy script after host preparation.

Focused documentation now uses `docs/linux-production-deployment.md` as the zero-to-running runbook. `README.md` cleanly separates development quickstart from production installation.

An accidental empty `NONEXISTENT` file was created during an API experiment while preparing this increment and immediately removed by a normal fast-forward corrective commit; history was not rewritten and no such file remains.

Validation:

```text
version               0.2.0-preview.4
final source          58a80a60c2f3a049b279be6bf9583ffa4c1cc088
CI                    35095161900 SUCCESS
Full Stack            35088765577 SUCCESS
Publish GHCR          35095620725 SUCCESS
validating CI run     35095161900
image digest          sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
attestation manifest  sha256:cd92f248e73e58fca570a687ca0002d10cfc8e5b308e60ce31351454b4933b0b
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
release artifact      10445034650
artifact digest       sha256:cb2bf6b6d8d34a545c080b866866d7098cedbab66f66f475aa168caf6a93c977
```

CI explicitly proves installer compatibility/syntax mode and production deployment rendering for both private-LAN and Entra+Cloudflare configurations. Full Stack proves the Compose/runtime change does not regress Redis/OTEL, outbox recovery, shared token budgets, cross-replica rotation or safe maintenance.

Repository validation deliberately does not claim that package installation has been executed on every Linux derivative. Actual target-distro package/service behavior remains an environment acceptance step; unknown hosts can preinstall Docker Engine + Compose v2 and reuse the same installer/deploy path with `--skip-docker-install`.

## Current next increment

Generic repository hardening is complete for the current preview. Next work should be physical/environment acceptance: install on the intended Linux host, validate real DGX/vLLM/model benchmarks, then Entra/Cloudflare/Copilot BYOK and the self-hosted deployment runner. Customer-specific HA/storage/backup destination choices follow the actual deployment topology.

Quota expansion remains requirements-driven. Creating a real immutable Git tag/GitHub Release remains an explicit product-owner publication decision.
