# Development log

This is the chronological engineering trace for LlmProxy. For canonical current state and exact resume point use `docs/project-status.md`.

## 2026-09-09 — Repository, gateway and multi-DGX foundation

Created the .NET 10 layered solution, React/TypeScript admin, PostgreSQL persistence, Docker/GitHub Actions foundations, logical client-facing models, internal DGX nodes/deployments, `/v1/models`, Chat Completions + SSE and Responses compatibility.

Added bearer credentials, Entra administration plumbing, node/model/deployment management, health hysteresis, drain/disable, audit, routing strategies, pre-response-only failover, metadata-only request metrics and vLLM runtime signals. Prompts/source/generated content remain excluded from telemetry.

## 2026-09-09 — Repository-first handover discipline

Introduced root `AGENTS.md` and the rule that meaningful increments update focused docs, canonical project status, development log and roadmap with actual validation evidence.

## 2026-09-10 — DGX/DCGM hardware telemetry — VALIDATED

Added optional per-node NVIDIA/DCGM telemetry independent from vLLM health. GPU utilization, framebuffer memory, temperature and power remain observational.

Checkpoint: `6c238a095273843e713a72fb2e26b2c7c434fc62`.

## 2026-09-10 — Architecture decision: custom LlmProxy, not NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. The project owner chose custom LlmProxy + vLLM.

## 2026-09-10 — Benchmark harness — VALIDATED

Added the .NET benchmark harness under `tests/performance/` for direct-vLLM vs gateway measurements, streaming/non-streaming, Chat/Responses and concurrency sweeps.

Checkpoint: `49e7932f14118be403eec042a1393946143776ae`.

## 2026-09-13 — Capacity Profiles + physical-node admission — VALIDATED

Added persisted benchmark-derived Capacity Profiles and aggregate physical-node concurrency admission. Defined saturation as `429 capacity_exhausted` with `Retry-After: 1`.

Checkpoint: `600ad42cc53ad1e97a259819654ca5cf5480e1db`.

## 2026-09-14 — Caller governance + Usage Groups — VALIDATED

Implemented persisted Usage Groups, request-time group snapshots, credential/model request-rate policies, governance audit and usage reporting/UI.

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

## 2026-09-14 — Runtime credential and route-catalog hot path — VALIDATED

Removed synchronous SQL configuration lookups from ordinary inference. Credential, node/model/deployment and caller-policy definitions are rebuilt at startup and maintained in local runtime state.

```text
credential cache 1f607c8433fe2ca08a1c243b68d87587204f35ee / CI 34860662747
route catalog     42c44753cd00d679a81bf065f410b7a497cdc000 / CI 34871542047
```

PostgreSQL-outage smoke proves already-published inference configuration remains usable.

## 2026-09-14 — Retention foundation

Added independent request-metric and audit retention, background batched cleanup and manual audited cleanup. Defaults: request metrics 90 days, audit 365 days.

## 2026-09-15 — Redis L2 synchronization + observability stack

Promoted Redis to shared runtime/coordination L2 while preserving local L1 and PostgreSQL durable authority. Added Redis snapshot/version/event synchronization, reconciliation and bundled OTEL Collector + Tempo + Loki + Prometheus + Grafana.

## 2026-09-15 — Distributed request/capacity coordination — VALIDATED

Added Redis shared request-rate counters, atomic deployment + physical-node capacity leases, fail-closed acquisition and active lease-loss inference cancellation.

Final lease-hardening validation:

```text
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## 2026-09-15 — Transactional runtime-state outbox — VALIDATED

Closed the PostgreSQL-commit -> Redis-publication process-crash window. Runtime Node/Model/Deployment/Credential/RatePolicy mutations now capture an outbox row in the same PostgreSQL transaction. One advisory-lock worker publishes ordered events, retries failures and marks rows processed only after durable Redis acknowledgement.

Fault validation stops Redis, commits a policy, stops the origin gateway, recovers Redis and requires a surviving peer to publish/enforce the change.

```text
implementation 9c6289ef172bed0502068df112b6e6aec4ee8521
CI             34968324786 SUCCESS
Full Stack     34968114492 SUCCESS
```

Outbox diagnostics/retention and deployment knobs were then validated:

```text
97e3b8f6b909156cbd58363d12f2fcbaf0627f5a / cfc742db84223a7bed2f8a80cab3e5680615efad
79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

## 2026-09-15 — Output-token budget V1 — VALIDATED

Added `OutputTokensPerWindow` + `MaxOutputTokensPerRequest` to caller policy. Chat/Responses output caps are injected/capped before inference, capacity is reserved atomically, known output usage refunds unused reservation, no-upstream-attempt paths refund fully, and uncertain post-upstream usage remains conservatively charged.

Redis-enabled token-budget admission is shared and fails closed; quota definitions reuse the transactional outbox + peer L1 path.

```text
implementation ff9af90144a19159d3c8208d8cedd95500b3b984
runtime proof  887ebfac98389c0115eaf9c102a60133ede745ff
CI             34987407172 SUCCESS
Full Stack     34987407169 SUCCESS
```

React Admin Apply/Clear management was validated at:

```text
426c545e841865406615998ca50b28a45c40e6f4
CI 34988084106 SUCCESS
```

## 2026-09-15 — Credential rotation — VALIDATED

Implemented API-key rotation as an in-place hard cutover on the existing credential identity. The same credential ID/group/policy/history linkage is preserved while prefix/HMAC change. Replacement raw secret is returned once with `Cache-Control: no-store`; revoked credentials cannot rotate; audit never stores secret/HMAC.

Dedicated Full Stack coverage verifies old key before rotation, new-key 200 / old-key 401 after convergence on both replicas, Redis new-HMAC-only state, preserved Usage Group/policy identity and peer restart hydration.

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

## 2026-09-15 — PostgreSQL backup/restore foundation — VALIDATED

Added Bash operators:

```text
docker/scripts/postgres-backup.sh
docker/scripts/postgres-restore.sh
```

Backup uses PostgreSQL custom format plus SHA-256 and non-secret metadata. Restore is explicit/destructive, recreates the database and treats Redis as rebuildable runtime state. `Authentication__ApiKeyPepper` and other external secrets are documented as separate recovery dependencies.

The first destructive smoke creates durable governance state, performs inference, backs up, destroys the PostgreSQL volume, proves the new target is clean, restores it, validates the original credential/group/policy/history and inference, then attaches a clean Redis peer and proves runtime snapshots are republished from PostgreSQL.

```text
commit b3cbe1ace209989aef845024259c0e4c6def4039
CI     34997715107 SUCCESS
```

## 2026-09-15 — Cross-platform backup/restore operators — VALIDATED

Added PowerShell equivalents:

```text
docker/scripts/postgres-backup.ps1
docker/scripts/postgres-restore.ps1
```

The PowerShell backup deliberately writes the custom-format archive inside the PostgreSQL container and transfers it with binary-safe `docker compose cp`, avoiding text-pipeline corruption. The restore verifies checksum/archive, recreates the target DB, restores, optionally clears the LlmProxy Redis prefix and restarts the selected gateway.

A new PowerShell smoke creates credential/group/request+token policy state, backs up through `pwsh`, performs a post-backup mutation, restores through `pwsh`, then proves the mutation disappeared while the backed-up credential/group/policy and authenticated inference returned.

The first cross-platform run exposed a nondeterministic test readiness race after destructive volume recreation: `pg_isready` could report accepting before the configured database was actually queryable. The smoke was hardened to require a real `SELECT 1` round-trip before the clean-target assertion.

Final validation:

```text
implementation/operator commit 66d7a809936f0f21f330d84887c1bb6a4e536f97
CI                           35018579785 SUCCESS
```

The same final CI run passes both:

```text
Backup and clean-target restore smoke suite  SUCCESS
PowerShell backup and restore smoke suite    SUCCESS
```

PowerShell semantics are exercised under `pwsh` in GitHub-hosted CI; native customer Windows/Docker Desktop remains deployment-environment acceptance.

## Next increment — model/runtime upgrade + draining

Backup/restore is complete for the repository-supported operator path. The next default production-hardening increment is a safe model/runtime upgrade and draining strategy: stop routing new work, observe/await in-flight work, upgrade/restart/replace the target, validate health/warmup, then re-enable routing without violating the existing pre-response-only failover and streaming-safety rules.

Further quota expansion remains requirements-driven because input/total/cost budgets need explicit tokenizer/pricing semantics. Long-term rollups and customer-specific HA/storage/backup scheduling are also requirements/deployment driven.
