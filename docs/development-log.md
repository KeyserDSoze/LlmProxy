# Development log

Chronological engineering trace for LlmProxy. Canonical current state and resume point live in `docs/project-status.md`; product-visible release history lives in `CHANGELOG.md` and `/admin/releases`.

## 2026-09-09 — Repository, gateway and multi-DGX foundation

Created the .NET 10 layered solution, React/TypeScript Admin, PostgreSQL persistence, Docker/GitHub Actions foundations, logical client-facing models, internal DGX nodes/deployments, `/v1/models`, Chat Completions + SSE and Responses compatibility.

Added bearer credentials, Entra administration plumbing, node/model/deployment management, health hysteresis, audit, routing strategies, pre-response-only failover, metadata-only request metrics and vLLM runtime signals. Raw prompts/source/generated content remain excluded from telemetry.

## 2026-09-09 — Repository-first handover discipline

Introduced root `AGENTS.md` and the rule that meaningful increments update focused docs, project status, development log and roadmap with actual validation evidence.

## 2026-09-10 — DGX/DCGM telemetry + benchmark harness — VALIDATED

Optional DGX/DCGM GPU telemetry remains observational. Added the .NET benchmark harness for direct-vLLM vs gateway measurements, streaming/non-streaming, Chat/Responses and concurrency sweeps.

```text
telemetry checkpoint 6c238a095273843e713a72fb2e26b2c7c434fc62
benchmark checkpoint 49e7932f14118be403eec042a1393946143776ae
```

Architecture decision: NVIDIA PAIR was evaluated; project owner chose custom LlmProxy + vLLM.

## 2026-09-13 — Capacity Profiles + physical-node admission — VALIDATED

Added persisted benchmark-derived Capacity Profiles and aggregate physical-node concurrency admission. Saturation returns `429 capacity_exhausted` with `Retry-After: 1`.

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

## 2026-09-15 — Redis L2 + observability stack

Promoted Redis to shared runtime/coordination L2 while preserving local L1 and PostgreSQL durable authority. Added reconciliation plus OTEL Collector, Tempo, Loki, Prometheus and Grafana.

## 2026-09-15 — Distributed request/capacity coordination — VALIDATED

Added Redis shared request-rate counters, atomic deployment + physical-node capacity leases, fail-closed acquisition and active lease-loss inference cancellation.

```text
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## 2026-09-15 — Transactional runtime-state outbox — VALIDATED

Closed the PostgreSQL-commit -> Redis-publication process-crash window. Runtime Node/Model/Deployment/Credential/RatePolicy changes now write an outbox row in the same PostgreSQL transaction. An advisory-lock worker publishes globally ordered state, retries failures and marks rows processed only after acknowledged Redis persistence/publication.

```text
implementation 9c6289ef172bed0502068df112b6e6aec4ee8521
CI             34968324786 SUCCESS
Full Stack     34968114492 SUCCESS
```

Outbox diagnostics/retention were later validated by CI `34976465066` and Full Stack `34976465149`. Pending rows remain non-deletable.

## 2026-09-15 — Output-token budget V1 — VALIDATED

Added `OutputTokensPerWindow` + `MaxOutputTokensPerRequest`. Chat/Responses output caps are injected/capped before inference; output capacity is reserved atomically; known usage refunds unused reservation; no-upstream-attempt paths refund fully; uncertain post-upstream usage remains conservatively charged. Redis mode is shared/fail-closed.

```text
implementation ff9af90144a19159d3c8208d8cedd95500b3b984
runtime proof  887ebfac98389c0115eaf9c102a60133ede745ff
CI             34987407172 SUCCESS
Full Stack     34987407169 SUCCESS
Admin UI       426c545e841865406615998ca50b28a45c40e6f4 / CI 34988084106 SUCCESS
```

## 2026-09-15 — Credential rotation — VALIDATED

Implemented in-place hard-cutover rotation on the existing credential identity. Group/policy/history linkage is preserved; replacement secret is returned once with `Cache-Control: no-store`; audit never stores secret/HMAC. Full Stack proves old-key rejection/new-key acceptance across replicas and restart hydration.

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

## 2026-09-15 — PostgreSQL backup/restore — VALIDATED

Added Bash and PowerShell operators. Backup uses PostgreSQL custom format + SHA-256 + non-secret metadata. Restore is explicit/destructive; Redis is rebuilt from PostgreSQL. `Authentication__ApiKeyPepper` remains an external recovery dependency.

The first destructive smoke destroys the source PostgreSQL volume, restores to a clean target and proves credential/group/policy/history/inference plus clean-Redis republish. PowerShell parity is exercised under `pwsh` using binary-safe `docker compose cp`.

```text
Linux foundation  b3cbe1ace209989aef845024259c0e4c6def4039 / CI 34997715107 SUCCESS
cross-platform     66d7a809936f0f21f330d84887c1bb6a4e536f97 / CI 35018579785 SUCCESS
```

## 2026-09-15 — Safe model/runtime maintenance — VALIDATED

Implemented distributed maintenance drain/resume:

- pre-block admission before `Draining`;
- Redis maintenance marker checked inside atomic capacity admission;
- existing requests/streams drain normally;
- resume requires zero global active work;
- `/health`, `/v1/models` and one-token model warm-up validation;
- failed validation keeps node draining;
- legacy direct drain endpoint deprecated.

```text
8220967141b9a3be7d96bbd7500d8958df60dbc1  backend protocol
9fca1e18dca37ab50c421e718f32365771a2032a  HA smoke
2e3e8e285267bcf9f1d80dc4e2b494914226c50f  Admin safe routing
CI         35021524018 SUCCESS
Full Stack 35021524019 SUCCESS
```

## 2026-09-16 — Product SemVer + patch notes in Admin — VALIDATED

Formalized first product baseline `0.1.0-preview.1` with compiled version identity, `/healthz`, `/api/admin/product`, persistent Admin version badge, `/admin/releases`, `CHANGELOG.md`, `docs/versioning.md`, backend tests and Playwright coverage.

```text
implementation 4b1f42daf8acb449526658b3a189535d7674c4b3
final test fix ee9ac0d17a95b68a79a464dc430e5c8427c9ded9
CI             35063494349 SUCCESS
Full Stack     35063309417 SUCCESS
```

From this point every product/operator-visible change must be represented in version metadata and release notes.

## 2026-09-16 — Release/build identity hardening — VALIDATED

Added:

- source SHA + UTC build timestamp into runtime identity;
- OCI `version`, `revision`, `created` labels;
- CI verification of image labels/environment values;
- SemVer/changelog/Admin package consistency validator;
- negative CI test proving mismatched candidate tag is rejected;
- container publish rule: main -> `main` + `sha-<7>`; matching Git tag -> exact version + SHA; prerelease never updates stable-looking aliases.

```text
commit        c37479bb474d44f9e36726bebba74cdf38e5661e
CI            35064353402 SUCCESS
Publish GHCR  35064707488 SUCCESS
```

## 2026-09-16 — Historical usage rollups / 0.2 preview — VALIDATED

Bumped product to `0.2.0-preview.1` and added durable daily PostgreSQL usage rollups so reporting survives raw metric expiry.

Implemented:

- one rollup cube keyed by UTC day + credential + Usage Group + logical model;
- request/token/error/rate-limit/capacity counts plus duration/TTFT sums and sample counts;
- independent retention defaults: raw request metrics 90 days, rollups 730 days;
- complete-day rollup-before-delete compaction;
- transactional aggregate + delete semantics;
- PostgreSQL advisory transaction lock to serialize compaction across gateway replicas;
- idempotent rerun behavior;
- reporting merge of historical rollups + newer raw metrics without double counting;
- API provenance fields for raw vs rolled-up request counts;
- UTC calendar-day reporting semantics;
- Admin reporting windows through 730 days and visible historical-rollup notice;
- updated changelog/runtime release catalog preserving `0.1.0-preview.1` as prior release.

The retention smoke inserts old/recent metrics, compacts the expired day, deletes its raw row, proves the 60-day report still contains the old usage from the rollup, runs cleanup again and proves the rollup is not duplicated. Existing audit/outbox/pending-outbox safety checks remain.

Validation:

```text
commit        5d66c7dcdae42955c6e26849aba84bed4787ff00
version       0.2.0-preview.1
CI            35075387110 SUCCESS
Full Stack    35075387186 SUCCESS
Publish GHCR  35075788954 SUCCESS
```

## Current next increment

Repository hardening is complete through historical usage rollups. The next non-external engineering work should be supply-chain/release hardening only where useful: immutable tagged release workflow, SBOM/provenance/attestation and operator-verifiable image identity. Customer-specific Redis/observability HA/storage and scheduled backup guidance follows when deployment topology is known.

Quota expansion remains requirements-driven because input/total-token/cost budgets need explicit tokenizer/pricing semantics.