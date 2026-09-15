# Project status / handover snapshot

Last reviewed: **2026-09-15**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

This baseline includes output-token budgets, React governance controls and the credential-rotation workflow. The standard CI proves backend/unit/benchmark, frontend/Vitest/Playwright, production image build and all Docker/PostgreSQL smoke suites. The dedicated Full Stack run proves Redis runtime sync, transactional outbox recovery, distributed token-budget coordination and cross-replica credential rotation.

## Core product scope

LlmProxy is Agic's enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication + credential lifecycle
2. request-rate + output-token governance
3. consolidated usage accounting
4. configurable Usage Groups
5. logical-model routing across DGX/vLLM
6. distributed multi-instance coordination with Redis
7. metadata-only observability and operational controls
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

Rotation endpoint:

```http
POST /api/admin/api-credentials/{id}/rotate
```

Rotation is an in-place hard cutover. The credential keeps the same `Id`, name, creation time, expiry, Usage Group assignment and all policy/history linkage. Only `KeyPrefix` and `KeyHash` are replaced.

Validated properties:

- revoked credentials return conflict and cannot be rotated;
- replacement secret is returned one time only;
- secret response is `Cache-Control: no-store`;
- old secret is removed from local cache when the same credential ID changes hash;
- origin gateway applies the committed credential immediately through the existing post-save cache interceptor;
- Redis-enabled replicas converge through the transactional outbox/runtime event path;
- Redis stores the new HMAC and no raw secret;
- the old HMAC is replaced, not retained as an accepted alias;
- Usage Group and request-rate/output-token policies remain attached to the same credential ID;
- audit `credential.rotate` records safe previous/new prefixes and never the raw secret/HMAC;
- peer restart/startup hydration accepts only the rotated key.

Full Stack `34996328588` explicitly proves old key works on both replicas before rotation, then new key returns 200 and old key 401 on both replicas after convergence, and the same behavior remains after peer restart.

React **Usage & Governance** provides a `Rotate` action and one-time copy box for the replacement secret. Playwright covers the workflow and preservation of group membership.

## Usage Groups / request-rate governance

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

## Routing / physical capacity

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

## Distributed runtime state / transactional outbox

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

Credential rotation is complete and validated. The next production-hardening order is:

1. **backup/restore + actual restore verification**;
2. decide whether product requirements need input/total-token budgets, monetary budgets or a token-budget period independent from request-rate `WindowSeconds`;
3. optional long-term usage rollups;
4. production Redis/observability HA/storage guidance where required;
5. model/runtime upgrade and draining strategy;
6. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available.

Backup/restore must be tested against a clean restore target. Merely documenting backup commands is not sufficient. The verification should demonstrate recovery of durable configuration and safe credential hashes, then show a restored gateway can rebuild/publish runtime state and serve authenticated traffic.

## Identity limitation

A centrally configured GitHub Copilot BYOK provider may use one shared credential. LlmProxy can attribute traffic to the credential/group but cannot infer the individual GitHub user. Never infer identity from source IP.

## External validation still required

- real DGX Spark/vLLM/model benchmark runs;
- representative multi-DGX coding workload;
- real Entra app/roles;
- Cloudflare Tunnel/public hostname;
- real GitHub Copilot BYOK end-to-end;
- self-hosted deployment runner;
- Copilot usage-metrics/custom-model reporting if per-user analytics are required.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `docs/usage-governance.md`, `docs/runtime-cache.md` and latest development-log entries;
2. inspect latest `main` and Actions before changing code;
3. treat `628fbc15dc2c963db802f9f2d9aca4b324225c99` / CI `34996328467` / Full Stack `34996328588` as the current validated runtime/product baseline;
4. begin backup/restore with an explicit backup artifact format, clean-target restore procedure and automated restore verification;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics and DB-free configuration lookup on the inference path;
6. update docs and validation evidence after every meaningful increment.
