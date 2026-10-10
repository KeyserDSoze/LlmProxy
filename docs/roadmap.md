# Roadmap

Status legend: `DONE` implemented and validated; `PLANNED` not complete; `EXTERNAL` requires target infrastructure/tenant/hardware; `OWNER ACTION` requires an explicit product-owner decision.

For canonical current state use `docs/project-status.md`. Product-visible changes live in `CHANGELOG.md` and `/admin/releases`.

## 2026-10-03 control-plane follow-up

- [x] Persist administrator automatic-update policy: Manual / ASAP 5m / Nightly / Weekly / Monthly.
- [x] Always update to latest through the complete intermediate stable release chain; never skip a custom update plan.
- [x] Make future manual `llmproxyctl update` resolve the same safe multi-release chain.
- [x] Add retry/backoff to immutable release downloads.
- [x] Promote System One to the shared logical-model/deployment/node topology.
- [x] Apply routing, capacity, failover, request-rate governance and metrics to System One.
- [x] Surface System One deployment/routing in Models & Deployments and Playground.
- [ ] Validate the exact main head and real target-host update from the previous installed release.

## M0 — Repository bootstrap — DONE

- DONE: .NET 10 layered solution, React/TypeScript Admin, PostgreSQL/EF, Docker, GitHub Actions, GHCR.
- DONE: repository-first handover discipline.
- DONE: SemVer identity, changelog and Admin release-notes page.

## M1 — Copilot -> gateway -> one inference node — REPOSITORY DONE / EXTERNAL ACCEPTANCE REMAINS

- DONE: logical models, `/v1/models`, Chat Completions + SSE, Responses compatibility.
- DONE: bearer/API-key auth from runtime L1.
- DONE: route/model/deployment runtime catalog.
- EXTERNAL: real GitHub Copilot BYOK through target public endpoint.

## M2 — Multi-node — DONE FOR CURRENT MVP

- DONE: node/model/deployment administration and health hysteresis.
- DONE: weighted least loaded / round robin / weighted round robin.
- DONE: pre-response-only failover.
- DONE: deployment + physical-node capacity admission.
- DONE: Redis distributed capacity leases and fail-closed lease-loss handling.
- DONE: safe distributed maintenance drain/resume with cross-replica admission pre-block and validated warm-up.

## M3 — Enterprise administration — DONE FOR CURRENT MVP

- DONE: Entra plumbing with Admin/User/Reader roles.
- DONE: React admin control plane plus `/admin/me` personal-key user portal.
- DONE: task-focused Admin UX with tabs/dialogs for long configuration pages and a unified Models & Deployments workspace.
- DONE: first-class platform-user registry with administrator-selectable manual census or automatic first-login provisioning.
- DONE: administrator user disable/re-enable; disable blocks self-service and revokes active personal API keys.
- DONE: user dashboard recent request history plus owner-scoped exact request/response audit from personal credentials.
- DONE: HMAC-hashed DB-backed service and Entra-owned personal credentials + runtime cache.
- DONE: stable `tid+oid` ownership, rotation/revocation and audit.
- DONE: administrator-recoverable encrypted copies for newly created/rotated client API keys with audited reveal/copy; authentication remains HMAC-only.
- DONE: per-page closed documentation accordions and dedicated Help & Endpoints guidance.
- DONE: product version/build and patch-note visibility.
- DONE: administrator release discovery plus immediate/scheduled control-plane updates through a persistent host Update Agent.
- DONE: immutable per-release update-plan metadata with standard fallback and constrained bundled custom-update procedures.
- EXTERNAL: real Entra app registration/roles.

## M4 — Observability — DONE FOR CURRENT MVP / PRODUCTION STORAGE EVOLUTION REMAINS

- DONE: request/status/duration/TTFT/token/attempt metrics.
- DONE: paginated/filterable administrator request browser with server-side model/node/credential/status filtering.
- DONE: vLLM pressure + optional DCGM telemetry.
- DONE: OTEL Collector + Tempo + Loki + Prometheus + Grafana bundle.
- DONE: trace correlation and capacity-lease-loss evidence.
- IMPLEMENTED / VALIDATION PENDING: bounded incremental reconstruction of Chat Completions and Responses SSE into single encrypted audit JSON, with partial output and cancellation/interruption status (no legacy log migration).
- DONE: encrypted full request/response audit for Chat Completions, Responses and System One, with global Admin inspection and stable `tid+oid` owner-scoped user self-inspection.
- DONE: paginated/filterable Admin Request Audit with optional 2-second live refresh, exact payload inspection and correlation to request metrics.
- DONE: request-audit retention administrator-configurable 10-4015 days (11 years) with four-hour cleanup.
- DONE: Admin model/System One Playground and classifier configuration visibility.
- PLANNED/EXTERNAL: customer-specific HA/object-storage/retention choices.

## M5 — Capacity and smart routing — DONE FOR CURRENT MVP / EXTERNAL CALIBRATION REMAINS

- DONE: vLLM queue/running/KV-cache signals and EWMA feedback.
- DONE: persisted routing tuning and benchmark-derived Capacity Profiles.
- DONE: benchmark harness + audited capacity apply workflow.
- DONE: Redis lease renewal/recovery and proactive safety watchdog.
- DONE: maintenance marker participates in atomic Redis admission.
- EXTERNAL: real hardware benchmark profiles + representative Copilot load.

## M6 — Operator onboarding / Linux deployability — REPOSITORY DONE / EXTERNAL HOST ACCEPTANCE REMAINS

- DONE: development quickstart and distributed full-stack Compose bundle.
- DONE: canonical production topology is the Redis-enabled full stack.
- DONE: dedicated production env template with explicit secret/inference-node placeholders.
- DONE: `docker/scripts/install-linux.sh` prepares a new host and invokes the canonical production deploy path.
- DONE: Docker official repository path for Debian, Ubuntu, Fedora, CentOS and RHEL.
- DONE: common distro-package fallbacks for `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk`, plus Compose CLI-plugin fallback.
- DONE: preserve existing working Docker + Compose installations and existing `/opt/llmproxy/.env`.
- DONE: generate initial PostgreSQL/Redis/API-key/pepper/Grafana secrets without printing them.
- DONE: optional GHCR login without persisting the package token into application config.
- DONE: inference runtime `/health` + `/v1/models` precheck before normal first deployment.
- DONE: production runtime assets staged under `/opt/llmproxy/runtime` instead of runner workspace.
- DONE: manual deployment and GitHub Actions deployment share `docker/scripts/deploy.sh`.
- DONE: production preflight validates placeholders, Production environment, Compose rendering and Entra-before-public-Cloudflare rule.
- DONE: deployment requires `/healthz` + `/readyz` before success.
- DONE: PostgreSQL backup/restore Bash + PowerShell operators with clean-target proof.
- DONE: full Linux production runbook including installer, Entra/Cloudflare, backup, update and rollback.
- DONE: executable `docker/scripts/environment-acceptance.sh` for host, direct inference runtime and gateway functional acceptance.
- DONE: metadata-only `summary.md` + `checks.tsv` acceptance evidence with secret-content guard.
- DONE: canonical bodyless vLLM `/health` handled as status-only acceptance.
- DONE: CI smoke exercises Chat/Responses streaming and non-streaming through direct/mock DGX and gateway surfaces.
- DONE: `.github/workflows/environment-acceptance.yml` for manual production acceptance on the `llmproxy-prod` self-hosted runner; no API-key dispatch inputs; short-lived metadata artifact only.
- EXTERNAL: execute installer on the chosen production distro/version and record package/service behavior.
- EXTERNAL: execute the acceptance workflow against the real gateway host + inference runtime.
- EXTERNAL: production Cloudflare Tunnel + self-hosted runner operational/reboot proof.
- EXTERNAL: customer backup destination, encryption and retention schedule.

## M7 — Caller governance — DONE FOR CURRENT V1

- DONE: credential/model request-rate policies and Redis shared counters.
- DONE: aggregate Entra-user request-rate policies spanning all personal keys with atomic user+credential admission.
- DONE: output-token budgets on credential/model scope.
- DONE: pre-inference reservation and Chat/Responses output-cap injection.
- DONE: settlement/refund and conservative uncertain-usage charge.
- DONE: `429 rate_limit_exceeded` / `429 token_budget_exceeded` separation.
- DONE: fail-closed Redis token-budget coordination.
- DONE: runtime/outbox policy propagation.
- DONE: Admin Apply/Clear UI and audit.

Future quota extensions remain requirements-driven: aggregate **output-token** user budgets need explicit reservation/settlement precedence; input/total-token budgets need tokenizer/estimation semantics; monetary budgets need pricing/accounting semantics.

## M8 — Usage Groups and reporting — DONE FOR CURRENT MVP

- DONE: Usage Group administration and primary group per credential.
- DONE: request-time group snapshot for historical attribution.
- DONE: usage aggregation/UI by group, credential and logical model.
- DONE: credential rotation preserves group/history identity.
- DONE: daily PostgreSQL historical rollups.
- DONE: raw + rollup reporting without double counting.
- DONE: Admin windows up to 730 days with visible raw/rollup provenance.
- PLANNED: archive/deletion semantics if required.
- PLANNED/EXTERNAL: Copilot usage-metrics ingestion for per-user/adoption analytics. GitHub's separate user-level reports must not be treated as a live request identity signal.

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
- DONE: encrypted full-body request-audit retention defaults to 30 days, is bounded to 10-4015 days (11 years) and is automatically cleaned every four hours.

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

## M12 — Managed hardware and model lifecycle — REPOSITORY DONE / REAL-HARDWARE ACCEPTANCE REMAINS

- DONE: hardware-agnostic node terminology and routing grouped by logical model.
- DONE: deployment-specific runtime endpoints so one physical host can serve multiple models on independent ports.
- DONE: unified Infrastructure workspace for physical fleet, capacity/telemetry and management-agent inventory/model lifecycle.
- DONE: editable node-wide simultaneous-request ceiling plus separate per-deployment concurrency controls.
- DONE: safe coordinated consolidation of legacy same-host runtime rows while preserving runtime endpoint, encrypted bearer and deployment ceiling.
- DONE: live node-capacity administration reports distributed Redis activity when available instead of presenting one gateway's local count as the physical total.
- DONE: curated deployable-model metadata with license, source, capabilities and conservative RAM/VRAM/disk/GPU planning envelopes.
- DONE: administrator Model & Hardware UI with live inventory, compatibility explanations and install/start/stop/remove lifecycle actions.
- DONE: authenticated LlmProxy Node Agent for Docker/vLLM lifecycle and NVIDIA inventory on prepared Linux hosts.
- DONE: x86_64 + ARM64 self-contained Node Agent release artifacts and systemd installation assets.
- EXTERNAL: validate each target hardware/driver/runtime combination and benchmark representative context/concurrency before production capacity is applied.

## M13 — Product/release hardening — AUTOMATIC IMMUTABLE RELEASE TRAIN

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
- DONE: Entra-owned personal API keys, `LlmProxy.User` self-service, own-usage view and administrator identity inventory.
- DONE: aggregate Entra-user request quotas + Admin/User UI visibility in `0.2.0-preview.7`.
- DONE: checksummed Linux release bundle + bootstrap + `llmproxyctl` install/update/rollback path.
- DONE: multi-architecture `linux/amd64` + `linux/arm64` GHCR publication and GitHub Release assets.
- DONE: every successful `main` CI run automatically allocates one immutable stable SemVer tag, starting at `v0.0.1`.
- DONE: full-stack distributed acceptance is a required job inside CI, so no release can be minted without it.
- DONE: patch is the default increment; commit markers `release:minor` / `release:major` intentionally advance larger components.

Validated runtime checkpoint:

```text
version           0.2.0-preview.7
commit            df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                35592623906 SUCCESS
Full Stack        35592624282 SUCCESS
Publish GHCR      35593081824 SUCCESS
image alias       sha-df3ecf7
image digest      sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
```

## Current development order

**New priority (2026-10-10):** after restoring green CI and validating outbound Agent transport, deliver the AirLLM experimental runtime and apples-to-apples multi-user benchmark. Do not defer the AirLLM experiment behind a universal model catalog or unrelated engine additions. Refer to [AirLLM concurrency experiment](airllm-concurrency-experiment.md).

1. Validate the first automatically generated `v0.0.x` GitHub Releases, including multi-architecture assets.
2. Install/update/rollback the latest exact `0.0.x` release on the ARM64 GB10 target; keep `sha-7e1534c` as the pre-autorelease fallback checkpoint until target-host acceptance is green.
2. Install/validate the dedicated `llmproxy-prod` self-hosted runner and execute `.github/workflows/environment-acceptance.yml` against the real gateway host + inference runtime.
3. Run real hardware benchmark sweeps + representative Copilot load and apply measured Capacity Profiles.
4. Validate real Entra Admin/User/Reader login + personal-key self-service + aggregate user request quotas, then Cloudflare + GitHub Copilot BYOK end-to-end.
5. Define customer-specific PostgreSQL/Redis/observability HA/storage and scheduled-backup destination/encryption/retention.
6. Expand remaining quota semantics only with explicit tokenizer/pricing requirements.
7. Preserve the automatic immutable release train; use `release:minor` / `release:major` only for intentional SemVer line changes.

NVIDIA Personal AI Router (PAIR) was evaluated and rejected for the current direction; continue LlmProxy + vLLM unless explicitly reopened.

## Multi-runtime inference roadmap (2026-10-09)

- IMPLEMENTED / VALIDATION PENDING: vLLM managed execution profiles (sequence cap, max context, KV dtype, CPU offload) with admin UI.
- IMPLEMENTED / VALIDATION PENDING: llama.cpp GGUF experimental managed engine, NVIDIA CUDA image and source checkpoint.
- NEXT: real hardware direct-versus-gateway benchmarks with sustainable-concurrency SLO and measured capacity profile, not hypothetical user-count multipliers.
- IMPLEMENTED / VALIDATION PENDING: separate installations/deployments for multiple profiles of the same model on one node, with filtered database uniqueness.
- IMPLEMENTED / VALIDATION PENDING: initial SGLang managed Docker launch and curated AWQ pilot; NEXT: independent metrics parser, OpenAI Chat/Responses/SSE/tool-call compatibility and hardware acceptance.
- **PRIORITY P0 — PLANNED / NOT IMPLEMENTED:** AirLLM layer-by-layer/expert streaming as a managed experimental engine on paired Linux hosts, with an LLMProxy-owned OpenAI-compatible serving adapter, explicit runtime image/version and hardware compatibility gates. This is a core hypothesis of LLMProxy, not deferred background research. See [AirLLM concurrency experiment](airllm-concurrency-experiment.md).
- **PRIORITY P0 — PLANNED:** benchmark AirLLM versus vLLM (and optionally llama.cpp/SGLang) on the **same host and same verified checkpoint**, comparing serial execution, request queueing, layer-aware batching if technically feasible, and multiple independent instances. Test 1/2/4/8/12/16 clients and report sustainable concurrency, TTFT, latency p50/p95, throughput, failures, GPU/RAM/disk usage and model-preparation overhead.
- **PRIORITY P0 — SAFETY:** never equate low VRAM consumption or accepted requests with sustainable parallelism. Keep AirLLM experimental/out of production routing until Chat/Responses/SSE completion, cancellation, HTTP compatibility, health, resource isolation and real-host acceptance have passed. Never automatically raise concurrency based only on theoretical fit.

- IMPLEMENTED / VALIDATION PENDING: benchmark SLO evaluator proposing candidate concurrency from measured evidence; still requires controlled live hardware repetitions and operator approval.

- IMPLEMENTED / VALIDATION PENDING: per-deployment vLLM/SGLang/llama.cpp telemetry normalized for the Admin; do not feed scheduler until benchmark evidence.

- [x] Admin one-time Agent invitations, verified installer, registration, heartbeat and outbound WebSocket relay (CI/target-host acceptance pending).
- [x] Redis-backed cross-replica Agent relay with ephemeral ownership and encrypted transient request/response frames (CI/HA soak and failover acceptance outstanding).
- [ ] Real remote WSS SSE resilience, tool-calling and load acceptance.
- [x] Admin-queued immutable Node Agent self-update (version resolution, checksum validation, systemd swap/rollback, heartbeat result), pending Linux host acceptance.
- [x] Admin-run benchmark jobs with server-side SLO results, cancellation and manual capacity approval, pending live inference acceptance.
- [ ] Safe one-time host Docker/NVIDIA prerequisites onboarding and diagnostics without manual commands where distributions support automated setup.

- [x] Admin-run persisted benchmark sweeps with synthetic payloads and explicit application of recommended capacity (CI/hardware acceptance pending).
