# Project status / handover snapshot

Last reviewed: **2026-09-15**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Current validated product baseline

Backend/runtime output-token budget behavior:

```text
commit     887ebfac98389c0115eaf9c102a60133ede745ff
CI         34987407172 SUCCESS
Full Stack 34987407169 SUCCESS
```

React Admin output-token budget management:

```text
commit     426c545e841865406615998ca50b28a45c40e6f4
CI         34988084106 SUCCESS
```

`426c545e...` changes only React/Admin client code and Playwright coverage relative to the runtime-validated `887ebfac...`; the runtime code validated by Full Stack `34987407169` is unchanged.

## Core product scope

LlmProxy is Agic's enterprise inference-governance boundary, not only a DGX router:

```text
1. inference authentication
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

## Implemented and validated

### Gateway / security

- .NET 10 ASP.NET Core gateway.
- `/v1/models`, Chat Completions and Responses compatibility.
- streaming/non-streaming and incremental SSE.
- arbitrary compatible payload preservation with logical-model rewrite.
- HMAC-hashed API credentials; raw secret shown once and never persisted.
- local runtime credential cache with startup rebuild/live publication.
- Entra admin plumbing with `LlmProxy.Admin` / `LlmProxy.Reader`.
- React control plane + administrative audit trail.

### Usage Groups / request-rate governance

- persisted Usage Groups and primary group per credential;
- request-time UsageGroup snapshot in metrics;
- persisted credential/model request-rate policies;
- fixed-window request admission with calculated `Retry-After`;
- Redis shared request counters in distributed mode;
- distinct `429 rate_limit_exceeded`;
- usage aggregation/UI by group, credential and logical model.

### Output-token budgets — V1 DONE

Output-token budget configuration is stored on the existing credential/model `RateLimitPolicy`:

```text
OutputTokensPerWindow : int?
MaxOutputTokensPerRequest : int?
```

V1 intentionally shares policy scope and `WindowSeconds` with request-rate admission.

Semantics:

- reserve output capacity before inference, preventing concurrent oversubscription;
- cap/inject Chat `max_completion_tokens` / `max_tokens` and Responses `max_output_tokens`;
- successful usage settles the reservation to actual output tokens and refunds unused capacity;
- if no upstream attempt occurred, refund the reservation fully;
- if upstream work occurred but usage is uncertain because of cancellation/failure/interrupted stream/missing usage, keep the full reservation charged;
- `429 token_budget_exceeded` when the fixed-window budget cannot admit the reservation;
- Redis-enabled budget coordination fails closed as `503 token_budget_coordination_unavailable`; it never falls back to an unsafe local distributed guess;
- token policy definitions propagate through the existing transactional runtime-state outbox and peer L1 synchronization;
- Admin API and React **Usage & Governance** support visibility, Apply and Clear workflows;
- changes are audited.

Standard governance smoke proves `10` reserved -> `7` actual settlement, two requests fitting budget `17`, third request rejected, invalid token cap handling and restart policy rebuild.

Dedicated Full Stack smoke proves live policy propagation to a pre-existing peer, shared Redis usage `7 -> 14`, cross-gateway rejection, Redis outage fail-closed behavior and recovery.

### Routing / physical capacity

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

### Distributed runtime state / transactional outbox

```text
PostgreSQL = durable source of truth + transactional runtime-state outbox
Redis      = distributed L2 snapshots/events + shared counters/leases/budget state
local RAM  = per-gateway request-path L1
```

Runtime Node/Model/Deployment/Credential/RatePolicy mutations and outbox records commit in the same PostgreSQL transaction. One advisory-lock worker publishes globally ordered events to Redis, retries failures, applies acknowledged events to the publishing replica's own L1 and marks rows processed only after durable Redis acknowledgement.

`GET /api/admin/runtime-sync` exposes Redis status and outbox backlog/retry diagnostics. Processed outbox rows default to 30-day retention; pending rows are never retention-deleted.

### Retention / observability

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
429 rate_limit_exceeded
429 token_budget_exceeded
429 capacity_exhausted
503 token_budget_coordination_unavailable
503 capacity_coordination_unavailable
503/abort capacity_lease_lost
503 no_healthy_deployment
```

Keep caller request rate, caller token budget and physical infrastructure admission distinct in code, metrics, traces and UI.

## Current development focus

The V1 **output-token** budget is complete and validated. The next production-hardening order is:

1. credential rotation workflow without exposing/re-persisting raw keys;
2. backup/restore + actual restore verification;
3. decide whether product requirements need input/total-token budgets, monetary budgets or a token-budget period independent from request-rate `WindowSeconds`;
4. optional long-term usage rollups;
5. production Redis/observability HA/storage guidance where required;
6. physical DGX/Copilot/Entra/Cloudflare acceptance when external access is available.

Quota follow-up should not be implemented casually: input/total-token admission requires tokenizer/estimation semantics and cost budgets require stable pricing/accounting rules.

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
2. inspect latest `main` and GitHub Actions before changing code;
3. treat `426c545e841865406615998ca50b28a45c40e6f4` / CI `34988084106` as the current complete product/UI checkpoint, with runtime Full Stack evidence `887ebfac98389c0115eaf9c102a60133ede745ff` / `34987407169`;
4. start with credential rotation unless the project owner explicitly chooses a quota-expansion item instead;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics and DB-free configuration lookup on the inference path;
6. update docs and validation evidence after every meaningful increment.
