# Project status / handover snapshot

Last reviewed: **2026-09-15**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product / operator baseline

```text
commit     66d7a809936f0f21f330d84887c1bb6a4e536f97
CI         35018579785 SUCCESS
```

This checkpoint includes the current product/runtime code plus repository-supported PostgreSQL backup/restore operators for Bash and PowerShell. CI proves backend/unit/benchmark, frontend/Vitest/Playwright, production image build, all ordinary Docker/PostgreSQL smoke suites, destructive clean-target restore and the PowerShell backup/restore path.

The latest distributed runtime Full Stack evidence remains:

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

The backup/restore increment does not change inference/runtime coordination behavior relative to that Full Stack checkpoint.

## Core product scope

LlmProxy is Agic's enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication + credential lifecycle
2. request-rate + output-token governance
3. consolidated usage accounting
4. configurable Usage Groups
5. logical-model routing across DGX/vLLM
6. distributed multi-instance coordination with Redis
7. backup/recovery of durable application state
8. metadata-only observability and operational controls
```

## Current request path

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC-hashed bearer credential from local L1
  -> credential + UsageGroup + caller policy from local L1
  -> output-token budget reservation when configured
       local atomic store in Redis-disabled single instance
       Redis atomic shared store in distributed mode
  -> request-rate admission
  -> logical model -> deployment/node catalog from local L1
  -> smart routing
  -> Redis/local physical-capacity admission
  -> vLLM
  -> output-token budget settlement
  -> metadata-only request metric + OTEL telemetry
```

Ordinary inference configuration lookup remains DB-free after startup/runtime publication.

## Credential lifecycle — DONE / VALIDATED

Secrets are generated and shown once. PostgreSQL stores HMAC-SHA256 hashes and safe metadata; Redis/runtime payloads also contain only hashes and safe credential state.

`POST /api/admin/api-credentials/{id}/rotate` performs an in-place hard cutover: same credential identity/group/policy/history linkage, new prefix/HMAC, replacement secret shown once, old secret invalid immediately after convergence. The response uses `Cache-Control: no-store` and audit contains no raw secret/HMAC.

Full Stack `34996328588` proves old key works before rotation, new key works and old key fails on both replicas after convergence, Redis contains the new HMAC only, and restart hydration preserves the cutover.

## Usage Groups / request-rate governance — DONE / VALIDATED

- persisted Usage Groups and primary group per credential;
- request-time UsageGroup snapshot in metrics;
- persisted credential/model request-rate policies;
- fixed-window request admission with calculated `Retry-After`;
- Redis shared request counters in distributed mode;
- distinct `429 rate_limit_exceeded`;
- usage aggregation/UI by group, credential and logical model.

## Output-token budgets — V1 DONE / VALIDATED

Budget configuration lives on the existing credential/model `RateLimitPolicy`:

```text
OutputTokensPerWindow : int?
MaxOutputTokensPerRequest : int?
```

V1 shares policy scope and `WindowSeconds` with request-rate admission.

Semantics:

- reserve output capacity before inference, preventing concurrent oversubscription;
- cap/inject Chat `max_completion_tokens` / `max_tokens` and Responses `max_output_tokens`;
- successful usage settles to actual output tokens and refunds unused reservation;
- no upstream attempt refunds fully;
- uncertain usage after upstream work keeps the full reservation charged;
- `429 token_budget_exceeded` when the fixed-window budget cannot admit the reservation;
- Redis-enabled budget coordination fails closed as `503 token_budget_coordination_unavailable`;
- policy definitions propagate through the transactional runtime-state outbox and peer L1 synchronization;
- Admin API/UI supports visibility, Apply and Clear.

## Backup / restore — DONE / VALIDATED

PostgreSQL is the durable recovery authority. Redis is runtime/coordination state and is rebuilt after restore.

Repository-supported operator scripts:

```text
docker/scripts/postgres-backup.sh
docker/scripts/postgres-restore.sh
docker/scripts/postgres-backup.ps1
docker/scripts/postgres-restore.ps1
```

Backup creates a PostgreSQL custom-format archive, SHA-256 checksum and non-secret metadata. Restore validates the archive/checksum, stops the Compose-managed gateway, recreates the target database instead of merging rows, restores with `pg_restore`, clears only LlmProxy-prefixed Redis runtime state when Redis belongs to the selected Compose stack, then restarts the gateway.

Critical recovery boundary: raw API secrets are never in PostgreSQL. `Authentication__ApiKeyPepper` and all external deployment secrets must be preserved independently in the approved secret manager. A DB restore with a different pepper cannot authenticate the existing client secrets.

CI `35018579785` proves:

- real custom-format dump + checksum generation;
- complete destruction of the PostgreSQL volume;
- creation of a genuinely clean/queryable target DB;
- destructive restore from the backup artifact;
- recovery of credential identity/HMAC, Usage Group membership, request-rate/output-token policies and audit/history state;
- authenticated inference using the same pre-backup credential/pepper;
- clean Redis startup and republishing of credential/route/policy snapshots from restored PostgreSQL;
- PowerShell operator path using binary-safe `docker compose cp`;
- a post-backup mutation disappears after PowerShell restore, proving rollback to the backup point rather than a no-op restore.

PowerShell is exercised under `pwsh` in GitHub-hosted CI. Native customer Windows/Docker Desktop execution and production backup storage/encryption/retention remain deployment-environment acceptance items.

Read `docs/backup-restore.md` for procedure and caveats.

## Routing / physical capacity — DONE FOR CURRENT MVP

- logical models hide provider/DGX topology;
- weighted least loaded / round robin / weighted round robin;
- health hysteresis, drain/disable and path-prefixed service roots;
- pre-response failover only;
- vLLM pressure + EWMA feedback;
- benchmark-derived Capacity Profiles;
- atomic deployment + physical-node admission;
- Redis capacity leases across replicas;
- fail-closed capacity acquisition;
- active-inference cancellation before an unsafe distributed lease can expire.

## Distributed runtime state / transactional outbox — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + transactional runtime-state outbox
Redis      = distributed L2 snapshots/events + shared counters/leases/budget state
local RAM  = per-gateway request-path L1
```

Runtime Node/Model/Deployment/Credential/RatePolicy mutations and outbox records commit in the same PostgreSQL transaction. One advisory-lock worker publishes globally ordered events to Redis, retries failures, applies acknowledged events to the publishing replica's own L1 and marks rows processed only after durable Redis acknowledgement.

`GET /api/admin/runtime-sync` exposes Redis status and outbox backlog/retry diagnostics. Processed outbox rows default to 30-day retention; pending rows are never retention-deleted.

## Retention / observability

Defaults:

```text
request_metrics              90 days
audit_events                 365 days
processed runtime outbox      30 days
cleanup interval              24 hours
batch size                    5000
```

Full stack includes PostgreSQL, Redis, OpenTelemetry Collector, Tempo, Loki, Prometheus and Grafana. Telemetry is metadata-only; prompts/source/generated output/secrets are excluded.

## Current error taxonomy

```text
401 invalid_api_key
400 invalid_output_token_limit
409 revoked credential rotation
429 rate_limit_exceeded
429 token_budget_exceeded
429 capacity_exhausted
503 token_budget_coordination_unavailable
503 capacity_coordination_unavailable
503/abort capacity_lease_lost
503 no_healthy_deployment
```

## Current development focus

Credential rotation and backup/restore are complete and validated. The next production-hardening order is:

1. **model/runtime upgrade + draining strategy**;
2. decide whether product requirements need input/total-token budgets, monetary budgets or a token-budget period independent from request-rate `WindowSeconds`;
3. optional long-term usage rollups;
4. customer-specific Redis/observability HA/storage and scheduled backup guidance;
5. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available.

The next implementation should define how a deployment/node enters drain, stops accepting new work, waits for active work to reach zero, is upgraded/restarted/replaced, passes health/warmup checks and only then re-enters routing. Preserve the rule that in-flight streaming work is never failed over after downstream bytes have started.

Quota expansion remains requirements-driven: input/total-token admission needs tokenizer/estimation semantics and monetary budgets need stable pricing/accounting rules.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/group but cannot infer the individual GitHub user. Never infer identity from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark runs;
- representative multi-DGX coding workload;
- real Entra app/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- customer production backup destination/encryption/retention and native Windows/Docker Desktop acceptance where applicable;
- Copilot usage-metrics/custom-model reporting if per-user analytics are required.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `docs/development-log.md`, `docs/roadmap.md`, `docs/routing.md`, `docs/capacity-control.md` and `docs/backup-restore.md`;
2. inspect latest `main` and Actions before changing code;
3. treat `66d7a809936f0f21f330d84887c1bb6a4e536f97` / CI `35018579785` as the complete repository/operator checkpoint, with distributed runtime evidence `628fbc15dc2c963db802f9f2d9aca4b324225c99` / Full Stack `34996328588`;
4. start the model/runtime upgrade + draining strategy increment;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, DB-free configuration lookup and pre-response-only failover;
6. update docs and validation evidence after every meaningful increment.
