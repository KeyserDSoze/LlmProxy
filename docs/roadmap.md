# Roadmap

Status legend: `DONE` implemented and validated; `ACTIVE` current focus; `PLANNED` not complete; `EXTERNAL` requires target infrastructure/tenant/hardware.

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
- PLANNED: customer-specific production HA/object-storage choices.

## M5 — Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/running/KV-cache signals and EWMA feedback.
- DONE: persisted routing tuning and benchmark-derived Capacity Profiles.
- DONE: benchmark harness + audited capacity apply workflow.
- DONE: Redis lease renewal/recovery and proactive safety watchdog.
- DONE: maintenance marker participates in atomic Redis admission.
- EXTERNAL: real DGX benchmark profiles + representative Copilot load.

## M6 — Operator onboarding / deployability — DONE FOR REPOSITORY PATH

- DONE: private GHCR path, Linux/Windows quickstart, minimal/full Compose and init scripts.
- DONE: production image/Compose CI validation.
- DONE: PostgreSQL backup/restore Bash + PowerShell operators.
- DONE: destructive clean-target restore and PowerShell restore CI proof.
- DONE: SemVer + Admin release notes.
- DONE: source SHA/build date embedded in image/runtime identity.
- DONE: mechanical tag/version consistency validation.
- DONE: `main` publishes `main` + immutable SHA alias; exact version tag only from matching Git tag.
- DONE: SPDX SBOM emitted as OCI attestation.
- DONE: SLSA/BuildKit provenance emitted as OCI attestation.
- DONE: immutable image digest recorded for every publication.
- DONE: post-push GHCR verification reads the OCI index and requires both SPDX and SLSA in-toto predicates.
- DONE: release-manifest Actions artifact records image/digest/version/source/build time, validating CI run and attestation descriptors.
- DONE: reusable tagged-release source guard requires successful `CI` from a push to `main` on the exact source SHA.
- DONE: every main/tag publication queries GitHub Actions through the same pre-GHCR source gate; main publication additionally proves the API-selected run equals the triggering CI run.
- PLANNED/OWNER ACTION: create an immutable Git tag/GitHub Release only when a distributable release is explicitly requested.
- EXTERNAL: production Cloudflare Tunnel + self-hosted deployment runner.
- EXTERNAL: customer backup destination, encryption, retention schedule and deployment-host acceptance.

## M7 — Caller governance — DONE FOR CURRENT V1

- DONE: request-rate policies and Redis shared counters.
- DONE: output-token budgets on credential/model scope.
- DONE: pre-inference reservation and Chat/Responses output-cap injection.
- DONE: settlement/refund and conservative uncertain-usage charge.
- DONE: `429 rate_limit_exceeded` / `429 token_budget_exceeded` separation.
- DONE: fail-closed Redis token-budget coordination.
- DONE: runtime/outbox policy propagation.
- DONE: Admin Apply/Clear UI and audit.

Future quota extensions remain PLANNED only when requirements justify them: input/total-token budgets require tokenizer/estimation semantics; monetary budgets require pricing/accounting semantics; independent token-budget periods require a product contract.

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
- PLANNED: customer-specific Redis HA/redundancy guidance.

## M12 — Product hardening — DONE THROUGH 0.2.0-preview.3

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
- DONE: the same Actions-API source-validation gate is exercised by every ordinary main publication before GHCR login.
- PLANNED: customer-specific production HA/storage/scheduled-backup guidance.

Current checkpoint:

```text
version           0.2.0-preview.3
commit            e9c8805e8473d3ad4df118d6a623ccef08723761
CI                35083646699 SUCCESS
Publish GHCR      35084132389 SUCCESS
image digest      sha256:6a7d082ef05d86851926beaf876b933de0fab96255ec25f3ad5ee84a7ac414ec
runtime FullStack 35075387186 SUCCESS
```

## Current development order

1. Add customer-specific Redis/observability HA/storage and scheduled-backup guidance when deployment shape is known.
2. Run real DGX + Copilot BYOK + Entra/Cloudflare acceptance when external access exists.
3. Expand quota semantics only with explicit tokenizer/pricing requirements.
4. Create a real immutable Git tag/GitHub Release only when the project owner explicitly requests publication.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue LlmProxy + vLLM unless explicitly reopened.