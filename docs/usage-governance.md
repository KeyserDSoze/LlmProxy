# Usage governance

This document is the focused product/engineering contract for inference authentication, caller request-rate limiting, output-token budgets, Usage Groups and consolidated usage reporting.

## Product boundary

Current request governance is:

```text
OpenAI-compatible inference request
  -> bearer/API credential authentication from local L1
  -> credential + UsageGroup resolution
  -> output-token budget reservation when configured
  -> request-rate admission
  -> logical-model routing + physical-capacity admission
  -> DGX / vLLM
  -> output-token budget settlement
  -> metadata-only usage metric / reporting
```

Caller governance remains distinct from infrastructure admission:

```text
request-rate policy exceeded       -> 429 rate_limit_exceeded
output-token budget exceeded       -> 429 token_budget_exceeded
output-budget coordinator unsafe   -> 503 token_budget_coordination_unavailable
physical DGX saturated             -> 429 capacity_exhausted
capacity coordinator unavailable   -> 503 capacity_coordination_unavailable
active capacity lease unsafe       -> 503/abort capacity_lease_lost
no operational backend             -> 503 no_healthy_deployment
```

## Inference identity and Usage Groups

Raw API secrets are shown once and never persisted. PostgreSQL stores an HMAC-SHA256 hash and safe metadata. The `/v1` authentication path hashes the supplied bearer secret and resolves it from the local runtime credential cache; there is no synchronous credential SQL lookup per request.

A credential can have zero or one primary `UsageGroupId`. The request-time group id is copied into request metrics so historical accounting does not change when a credential is moved later.

Never infer a user or Usage Group from source IP. A centrally configured GitHub Copilot BYOK credential may be shared, so gateway attribution is reliably credential/group-level unless the client uses separate credentials.

## Unified caller policy scope

`RateLimitPolicy` is the persisted caller-governance policy for one credential and optional logical model:

```text
Id
ApiCredentialId
LogicalModel : string?          # null = credential-wide default
RequestsPerWindow : int
WindowSeconds : int
OutputTokensPerWindow : int?    # null = no token budget
MaxOutputTokensPerRequest : int?# required together with OutputTokensPerWindow
Enabled : bool
CreatedAtUtc
UpdatedAtUtc
```

Policy precedence is:

```text
credential + exact logical model
    overrides
credential-wide policy (LogicalModel = null)
```

An output-token budget is active only when the policy is enabled and both token fields are configured. In V1 the request-rate counter and output-token budget deliberately share the same `WindowSeconds` value and policy scope.

Request-rate updates preserve an existing token budget unless token-budget fields are explicitly supplied. Token budget can also be configured or cleared independently through dedicated endpoints.

Policy configuration is kept in local L1 and is republished through the same PostgreSQL transactional-outbox -> Redis runtime-state pipeline used for rate policies. Startup rebuild also restores token-budget configuration.

## Request-rate admission

Request-rate admission is fixed-window. Redis-enabled deployments use one shared Redis counter across gateway replicas; Redis-disabled deployments use the local in-memory store.

A rejection returns:

```text
HTTP 429
Retry-After: <seconds>
error.type = rate_limit_error
error.code = rate_limit_exceeded
```

## Output-token budget — V1 semantics

The token-budget implementation protects **generated/output tokens**, not input tokens or monetary cost.

A naive post-response counter is intentionally not used because concurrent requests could all pass before any of them reports final usage. Instead LlmProxy reserves the maximum output tokens that the accepted request is allowed to generate before forwarding it.

### Request cap and reservation

For Chat Completions:

```text
max_completion_tokens
max_tokens              # legacy-compatible field
```

For Responses:

```text
max_output_tokens
```

If the client supplies a positive limit above `MaxOutputTokensPerRequest`, LlmProxy caps it before forwarding. If no output limit is supplied, LlmProxy injects the policy maximum. If Chat supplies both supported fields, both are capped and the reservation uses the larger effective value.

An invalid/non-positive output limit under an active budget returns:

```text
HTTP 400
error.code = invalid_output_token_limit
```

The reserved amount therefore represents an upper bound that a compatible upstream is instructed not to exceed.

### Atomic admission

Before inference, `OutputTokenBudgetLimiter` asks `IOutputTokenBudgetStore` to atomically reserve the request cap.

For a window with budget `B`, already charged/reserved usage `U`, and requested reservation `R`:

```text
admit iff U + R <= B
```

If the budget would be exceeded:

```text
HTTP 429
Retry-After: <seconds until fixed-window reset>
error.type = rate_limit_error
error.code = token_budget_exceeded
```

### Settlement

After the inference endpoint completes:

```text
successful 2xx + observed output token usage
  -> charge actual output tokens
  -> refund Reserved - Actual

no upstream attempt occurred
  -> settle actual = 0
  -> refund full reservation

client cancellation / upstream failure / interrupted stream /
missing or otherwise uncertain usage after upstream work
  -> keep the full reservation charged
```

Settlement is idempotent. Unknown usage is deliberately conservative: the system never assumes zero after upstream generation may have occurred.

A compatible upstream should not exceed the forwarded maximum. If reported usage is invalid or above the reservation, V1 does not refund any part of the reservation; such provider-contract anomalies are future hardening work.

### Redis-enabled multi-replica semantics

Redis-enabled deployments use `RedisOutputTokenBudgetStore`. Reservation is atomic through Lua and Redis server time; the fixed-window state is stored in a TTL-backed hash. Settlement refunds unused reservation only if the same budget window is still active.

Unlike the existing request-rate counter's degraded local fallback, token-budget admission is a hard distributed governance boundary:

```text
Redis reservation coordination unavailable
  -> no local guess / no local fallback
  -> HTTP 503
  -> Retry-After: 1
  -> token_budget_coordination_unavailable
```

If Redis settlement itself fails after a successful reservation, the already-reserved amount remains charged. This is conservative and prevents accidental budget expansion.

Redis AOF preserves current window state across the bundled Redis service restart. In Redis-disabled single-instance mode the local in-memory window state resets on process restart; persisted policy configuration is rebuilt from PostgreSQL.

## Admin API

Current governance endpoints include:

```http
GET  /api/admin/usage-groups
POST /api/admin/usage-groups
PUT  /api/admin/usage-groups/{id}

PUT    /api/admin/api-credentials/{id}/usage-group
DELETE /api/admin/api-credentials/{id}/usage-group

GET    /api/admin/rate-limits
POST   /api/admin/rate-limits
PUT    /api/admin/rate-limits/{id}
DELETE /api/admin/rate-limits/{id}

GET    /api/admin/output-token-budgets
PUT    /api/admin/rate-limits/{id}/output-token-budget
DELETE /api/admin/rate-limits/{id}/output-token-budget

GET /api/admin/governance/credentials
GET /api/admin/usage/summary?days=30
GET /api/admin/usage/groups?days=30
GET /api/admin/usage/credentials?days=30
GET /api/admin/usage/models?days=30
```

Token-budget update/clear operations are audited as `output_token_budget.update` and `output_token_budget.clear`.

## React control plane

`/admin/governance` exposes:

- Usage KPIs and breakdowns;
- Usage Group administration;
- credential -> Usage Group assignment;
- request-rate policies;
- output-token budget visibility;
- Apply/Clear output-token budget workflow per rate-policy scope.

The UI keeps request-rate and output-token budget controls conceptually separate even though they share the persisted credential/model/window scope.

## Usage metrics and reporting

Request metrics remain metadata-only and include credential/group/model/status/timing plus observed input/output/total tokens where the upstream reports them. Prompts, source code, generated output and bearer secrets are not persisted by default.

Usage aggregation is PostgreSQL-side and reports by Usage Group, credential and logical model. Current raw request retention is 90 days by default; optional long-term rollups remain future work.

## Validation

Output-token budget backend/runtime behavior is validated on:

```text
commit     887ebfac98389c0115eaf9c102a60133ede745ff
CI         34987407172 SUCCESS
Full Stack 34987407169 SUCCESS
```

The standard governance smoke proves:

- local reservation and actual-usage refund (`10 reserved -> 7 charged`);
- two requests fit a 17-token window only because settlement refunds unused reservation;
- the third request returns `429 token_budget_exceeded`;
- invalid output-token limit returns 400;
- persisted policy is rebuilt after gateway restart.

The dedicated distributed smoke proves:

- a peer started before policy creation receives the policy into local L1 through runtime-state publication;
- gateway A settles shared Redis usage to 7;
- gateway B settles the same shared window to 14;
- the next cross-gateway request is rejected without changing Redis usage;
- Redis outage fails admission closed with `503 token_budget_coordination_unavailable`;
- Redis recovery preserves/exposes the still-exhausted shared window.

The React control-plane increment is validated by the CI baseline documented in `docs/project-status.md`.

## Remaining governance backlog

- input-token or total-token budgets require explicit tokenizer/estimation semantics before admission;
- monetary/cost budgets require stable pricing/model accounting semantics;
- a dedicated token-budget period separate from `WindowSeconds` may be added if product requirements require it;
- define explicit provider-contract anomaly handling if upstream reports output usage above the enforced request cap;
- consider request-rate-vs-token-budget rejection precedence optimization; current token reservation is safely refunded when no upstream attempt occurs;
- long-term aggregate usage rollups if reporting must outlive raw request retention;
- optional Copilot usage-metrics ingestion for per-user/adoption analytics.
