# Roadmap

Status legend: `DONE` implemented and validated; `PLANNED` not complete; `EXTERNAL` requires target infrastructure/tenant/hardware; `OWNER ACTION` requires an explicit product-owner decision.

For canonical current state use `docs/project-status.md`. Product-visible changes live in `CHANGELOG.md` and `/admin/releases`.

## M0 — Repository bootstrap — DONE

- DONE: .NET 10 layered solution, React/TypeScript Admin, PostgreSQL/EF, Docker, GitHub Actions, GHCR.
- DONE: repository-first handover discipline.
- DONE: SemVer identity, changelog and Admin release-notes page.

## M1 — Copilot -> gateway -> one DGX — REPOSITORY DONE / EXTERNAL ACCEPTANCE REMAINS

- DONE: logical models, `/v1/models`, Chat Completions + SSE, Responses compatibility.
- DONE: bearer/API-key auth from runtime L1.
- DONE: route/model/deployment runtime catalog.
- EXTERNAL: real GitHub Copilot BYOK through target public endpoint.

## M2 — Multi-DGX — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration and health hysteresis.
- DONE: weighted least loaded / round robin / weighted round robin.
- DONE: pre-response-only failover.
- DONE: deployment + physical-node capacity admission.
- DONE: Redis distributed capacity leases and fail-closed lease-loss handling.
- DONE: safe distributed maintenance drain/resume with cross-replica admission pre-block and validated warm-up.

## M3 — Enterprise administration — DONE FOR MVP / EXTERNAL SETUP REMAINS

- DONE: Entra plumbing and Admin/Reader roles.
- DONE: React control plane.
- DONE: HMAC-hashed DB-backed credentials + runtime cache.
- DONE: one-time credential creation/rotation and audit.
- DONE: product version/build and patch-note visibility.
- EXTERNAL: real Entra app registration/roles.

## M4 — Observability — DONE FOR CURRENT MVP / PRODUCTION STORAGE EVOLUTION REMAINS

- DONE: request/status/duration/TTFT/token/attempt metrics.
- DONE: vLLM pressure + optional DCGM telemetry.
- DONE: OTEL Collector + Tempo + Loki + Prometheus + Grafana bundle.
- DONE: trace correlation and capacity-lease-loss evidence.
- PLANNED/EXTERNAL: customer-specific HA/object-storage/retention choices.

## M5 — Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/running/KV-cache signals and EWMA feedback.
- DONE: persisted routing tuning and benchmark-derived Capacity Profiles.
- DONE: benchmark harness + audited capacity apply workflow.
- DONE: Redis lease renewal/recovery and proactive safety watchdog.
- DONE: maintenance marker participates in atomic Redis admission.
- EXTERNAL: real DGX benchmark profiles + representative Copilot load.

## M6 — Operator onboarding / Linux deployability — REPOSITORY DONE / EXTERNAL HOST ACCEPTANCE REMAINS

- DONE: development quickstart and distributed full-stack Compose bundle.
- DONE: canonical production topology is the Redis-enabled full stack.
- DONE: dedicated production env template with explicit secret/DGX placeholders.
- DONE: `docker/scripts/install-linux.sh` prepares a new host and invokes the canonical production deploy path.
- DONE: Docker official repository path for Debian, Ubuntu, Fedora, CentOS and RHEL.
- DONE: common distro-package fallbacks for `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk`, plus Compose CLI-plugin fallback.
- DONE: preserve existing working Docker + Compose installations and existing `/opt/llmproxy/.env`.
- DONE: generate initial PostgreSQL/Redis/API-key/pepper/Grafana secrets without printing them.
- DONE: optional GHCR login without persisting the package token into application config.
- DONE: DGX `/health` + `/v1/models` precheck before normal first deployment.
- DONE: production runtime assets staged under `/opt/llmproxy/runtime` instead of runner workspace.
- DONE: manual deployment and GitHub Actions deployment share `docker/scripts/deploy.sh`.
- DONE: production preflight validates placeholders, Production environment, Compose rendering and Entra-before-public-Cloudflare rule.
- DONE: deployment requires `/healthz` + `/readyz` before success.
- DONE: PostgreSQL backup/restore Bash + PowerShell operators with clean-target proof.
- DONE: full Linux production runbook including installer, Entra/Cloudflare, backup, update and rollback.
- DONE: executable `docker/scripts/environment-acceptance.sh` for host, direct DGX/vLLM and gateway functional acceptance.
- DONE: metadata-only `summary.md` + `checks.tsv` acceptance evidence with secret-content guard.
- DONE: canonical bodyless vLLM `/health` handled as status-only acceptance.
- DONE: CI smoke exercises Chat/Responses streaming and non-streaming through direct/mock DGX and gateway surfaces.
- DONE: `.github/workflows/environment-acceptance.yml` for manual production acceptance on the `llmproxy-prod` self-hosted runner; no API-key dispatch inputs; short-lived metadata artifact only.
- EXTERNAL: execute installer on the chosen production distro/version and record package/service behavior.
- EXTERNAL: execute the acceptance workflow against the real VM + DGX/vLLM.
- EXTERNAL: production Cloudflare Tunnel + self-hosted runner operational/reboot proof.
- EXTERNAL: customer backup destination, encryption and retention schedule.

## M7 — Caller governance — DONE FOR CURRENT V1

- DONE: request-rate policies and Redis shared counters.
- DONE: output-token budgets on credential/model scope.
- DONE: pre-inference reservation and Chat/Responses output-cap injection.
- DONE: settlement/refund and conservative uncertain-usage charge.
- DONE: `429 rate_limit_exceeded` / `429 token_budget_exceeded` separation.
- DONE: fail-closed Redis token-budget coordination.
- DONE: runtime/outbox policy propagation.
- DONE: Admin Apply/Clear UI and audit.

Future quota extensions remain requirements-driven: input/total-token budgets need tokenizer/estimation semantics; monetary budgets need pricing/accounting semantics.

## M8 — Usage Groups and reporting — DONE FOR CURRENT MVP

- DONE: Usage Group administration and primary group per credential.
- DONE: request-time group snapshot for historical attribution.
- DONE: usage aggregation/UI by group, credential and logical model.
- DONE: credential rotation preserves group/history identity.
- DONE: daily PostgreSQL historical rollups.
- DONE: raw + rollup reporting without double counting.
- DONE: Admin windows up to 730 days with visible raw/rollup provenance.
- PLANNED: archive/deletion semantics if required.
- PLANNED/EXTERNAL: Copilot usage-metrics ingestion for per-user/adoption analytics.

Shared Copilot credentials are not individual user identity. Never infer users from IP.

## M9 — Inference hot-path hardening — DONE

- DONE: credentials/routes/policies from local L1.
- DONE: DB-free ordinary config lookup after startup.
- DONE: PostgreSQL-outage inference smoke.
- DONE: Redis L2 synchronization while preserving L1.
- DONE: old credential hash removed after rotation convergence.
- DONE: maintenance block piggybacks on distributed capacity admission.

## M10 — Retention and operational hygiene — DONE FOR CURRENT MVP

- DONE: raw request metrics 90-day default.
- DONE: daily usage rollups 730-day default.
- DONE: audit 365-day default.
- DONE: processed runtime outbox 30-day default.
- DONE: pending outbox never retention-deleted.
- DONE: daily rollup-before-delete compaction with PostgreSQL advisory lock.
- DONE: manual/background cleanup, idempotent rerun and Docker smoke proof.

## M11 — Distributed runtime state / HA — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + runtime-state outbox + usage rollups
Redis      = distributed L2 + shared request/capacity/token-budget/maintenance coordination
local RAM  = per-replica request-path L1
```

- DONE: Redis snapshot/version/event synchronization + reconciliation.
- DONE: peer L1 updates and runtime-sync diagnostics.
- DONE: global request-rate counters.
- DONE: distributed capacity leases + active lease-loss safety.
- DONE: PostgreSQL transactional outbox.
- DONE: globally ordered advisory-lock publication/retry.
- DONE: Redis shared token budget.
- DONE: credential rotation propagation.
- DONE: clean Redis reconstruction from restored PostgreSQL.
- DONE: cross-replica maintenance pre-block/drain/resume.
- DONE: retention compaction serialized across replicas via PostgreSQL advisory transaction lock.
- PLANNED/EXTERNAL: customer-specific Redis HA/redundancy design.

## M12 — Product/release hardening — DONE THROUGH 0.2.0-preview.5

- DONE: output-token quota V1.
- DONE: credential rotation.
- DONE: backup/restore with real clean-target proof.
- DONE: safe model/runtime upgrade + draining strategy.
- DONE: SemVer + runtime release object + Admin patch notes.
- DONE: release/build identity and tag consistency automation.
- DONE: long-term usage rollups.
- DONE: GHCR SBOM/provenance generation and registry-native verification.
- DONE: immutable digest + release-manifest artifact.
- DONE: exact SemVer tag path requires previously successful `main` CI for the same SHA.
- DONE: every main/tag publication uses the same Actions-API source-validation gate before GHCR login.
- DONE: production Linux deployment consolidated onto the Redis-enabled full stack.
- DONE: cross-distribution host bootstrap script + production runbook.
- DONE: production environment-acceptance harness + metadata-only evidence contract.
- DONE: bodyless canonical vLLM health compatibility fix.
- DONE: self-hosted manual acceptance workflow on the production runner labels.
- OWNER ACTION: create a real immutable Git tag/GitHub Release only when explicitly requested.

Validated runtime checkpoint:

```text
version           0.2.0-preview.5
commit            723c47d919a59cf95e447c071ef377ab92a06498
CI                35099356925 SUCCESS
Publish GHCR      35099987458 SUCCESS
image alias       sha-723c47d
image digest      sha256:7b24e16d264c78eb9c6affa8eadf207c756d883799c8e0503b128ef4004ac1fa
```

Validated operator-workflow checkpoint:

```text
commit            cdd21d6155de08c6202754560f5b3c9f590071f9
CI                35110131158 SUCCESS
```

## Current development order

1. Install immutable `sha-723c47d` on the actual target Linux host.
2. Install/validate the dedicated `llmproxy-prod` self-hosted runner and execute `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM.
3. Run real DGX benchmark sweeps + representative Copilot load and apply measured Capacity Profiles.
4. Validate real Entra + Cloudflare + GitHub Copilot BYOK end-to-end.
5. Define customer-specific PostgreSQL/Redis/observability HA/storage and scheduled-backup destination/encryption/retention.
6. Expand quota semantics only with explicit tokenizer/pricing requirements.
7. Create a Git tag/GitHub Release only when explicitly requested.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue LlmProxy + vLLM unless explicitly reopened.
