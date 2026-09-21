# Usage governance

This document is the focused product/engineering contract for inference authentication, credential lifecycle, caller request-rate limiting, output-token budgets, Usage Groups and consolidated usage reporting.

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

## Inference identity and credential lifecycle

Raw API secrets are shown once and never persisted. PostgreSQL stores an HMAC-SHA256 hash and safe metadata. The `/v1` authentication path hashes the supplied bearer secret and resolves it from the local runtime credential cache; there is no synchronous credential SQL lookup per request.

Credential creation returns a generated secret once. Credential rotation follows the same secret-handling rule.

Credentials may be administrator-created **service credentials** or Entra-owned **personal credentials**. Personal ownership is immutable `(tid, oid)` metadata on the credential; usernames/email are display metadata only. The runtime snapshot carries owner IDs, while durable request telemetry continues to store `ApiCredentialId` so user attribution is resolved without duplicating user PII per request.

Normal users manage their own keys at `/admin/me` or through `/api/me/*`. See `docs/identity-api-keys.md`.

### In-place credential rotation

```http
POST /api/admin/api-credentials/{id}/rotate
```

Rotation is an **in-place hard cutover**. It intentionally does not create a second durable credential identity or an overlap window.

Preserved:

```text
Id
Name
CreatedAtUtc
ExpiresAtUtc
LastUsedAtUtc
UsageGroupId
all RateLimitPolicy / output-token policy linkage
historical request/accounting identity
```

Replaced:

```text
KeyPrefix
KeyHash
```

Rules:

- revoked credentials cannot be rotated;
- a fresh `lp_...` secret is generated server-side;
- only the HMAC and safe prefix are stored;
- the raw replacement secret is returned exactly once;
- the response is marked `Cache-Control: no-store`;
- audit action is `credential.rotate`;
- audit details contain safe previous/new prefixes, expiry/group metadata and no raw secret/HMAC;
- the origin replica updates its local credential L1 only after the PostgreSQL save succeeds;
- Redis-enabled replicas receive the new credential snapshot through the transactional runtime-state outbox;
- cache upsert by the same credential ID removes the previous hash, so the old secret is no longer accepted after local convergence;
- Redis credential state is an upsert for the same ID and therefore replaces the previous HMAC rather than retaining an alias;
- restart/startup hydration restores only the new hash.

This is a hard cutover rather than a grace-period rotation. Clients must update to the newly returned secret immediately. A future overlapping-key model would require an explicit product/security decision and a different persisted model; do not silently add multiple accepted hashes to one credential.

## Usage Groups

A credential can have zero or one primary `UsageGroupId`. The request-time group id is copied into request metrics so historical accounting does not change when a credential is moved later. Rotation preserves the same credential ID and Usage Group, so it also preserves attribution/history naturally.

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

Policy configuration is kept in local L1 and republished through PostgreSQL transactional-outbox -> Redis runtime-state publication. Credential rotation keeps the same `ApiCredentialId`, so policy records are not recreated or rewritten.

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

A naive post-response counter is not used because concurrent requests could all pass before final usage exists. Instead LlmProxy reserves the maximum output tokens the accepted request is allowed to generate before forwarding it.

### Request cap and reservation

For Chat Completions:

```text
max_completion_tokens
max_tokens
```

For Responses:

```text
max_output_tokens
```

If a positive client limit exceeds `MaxOutputTokensPerRequest`, LlmProxy caps it. If no output limit is supplied, LlmProxy injects the policy maximum. Invalid/non-positive output limits under an active budget return `400 invalid_output_token_limit`.

Before inference, `OutputTokenBudgetLimiter` atomically reserves the request cap. For budget `B`, used/reserved `U` and new reservation `R`:

```text
admit iff U + R <= B
```

A budget rejection is `429 token_budget_exceeded` with `Retry-After` to the fixed-window reset.

### Settlement

```text
successful 2xx + observed output usage
  -> charge actual output tokens
  -> refund Reserved - Actual

no upstream attempt
  -> settle actual = 0
  -> refund full reservation

client cancellation / upstream failure / interrupted stream /
missing or otherwise uncertain usage after upstream work
  -> keep the full reservation charged
```

Settlement is idempotent. Unknown usage is deliberately conservative.

### Redis-enabled multi-replica semantics

Redis-enabled deployments use atomic reservation/settlement against one shared fixed window. Redis server time is used. If reservation coordination is unavailable there is no local distributed fallback:

```text
HTTP 503
Retry-After: 1
error.code = token_budget_coordination_unavailable
```

If settlement itself fails after reservation, the reserved amount remains charged rather than accidentally expanding the budget.

## User self-service API

When Entra is enabled, `LlmProxy.User` and `LlmProxy.Admin` may call:

```http
GET  /api/me
GET  /api/me/api-credentials
POST /api/me/api-credentials
POST /api/me/api-credentials/{id}/rotate
POST /api/me/api-credentials/{id}/revoke
GET  /api/me/usage?days=30
```

Ownership is derived from the authenticated Entra principal; owner IDs are never accepted from request bodies.

## Admin API

Current governance endpoints include:

```http
GET  /api/admin/api-credentials
POST /api/admin/api-credentials
POST /api/admin/api-credentials/{id}/rotate
POST /api/admin/api-credentials/{id}/revoke

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

Administrative mutations are audited. Secrets and prompt/output content are excluded from audit detail.

## React control plane

`/admin/governance` exposes Usage KPIs, Usage Groups, credential-to-group assignment, credential rotation, request-rate policies and output-token budgets. `/admin/me` is the normal-user portal for personal key lifecycle and own usage.

Credential rotation UI behavior:

- enabled credentials expose `Rotate`;
- successful rotation displays the new secret in a one-time copy box;
- the UI explicitly warns that the previous key is invalid;
- the refreshed row shows the new prefix while preserving Usage Group selection;
- Playwright verifies this workflow.

## Usage metrics and reporting

Request metrics remain metadata-only and include credential/group/model/status/timing plus observed input/output/total tokens where upstream reports them. Prompts, source code, generated output and bearer secrets are not persisted by default.

Usage aggregation is PostgreSQL-side and reports by Usage Group, credential and logical model. Current raw request retention is 90 days by default; optional long-term rollups remain future work.

## Validation

Canonical credential-rotation/product baseline:

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

The standard CI proves domain rotation semantics, frontend build/Vitest/Playwright and all existing Docker/PostgreSQL governance/regression suites.

The dedicated Full Stack credential-rotation smoke proves:

- old key is valid on both gateways before rotation;
- same credential ID/group are returned after rotation;
- origin gateway immediately accepts new key and rejects old key after committed save;
- a peer that existed before rotation converges to new key 200 / old key 401;
- Redis credential snapshot contains the new HMAC, not the previous HMAC and never the raw secret;
- transactional credential outbox publication completes;
- Usage Group and caller-policy linkage remain unchanged;
- `credential.rotate` audit contains safe prefix transition metadata but no secret/HMAC;
- peer restart hydrates only the rotated key.

Output-token budget runtime behavior remains validated in the same Full Stack run together with outbox and baseline Redis/OTEL behavior.

## Remaining governance backlog

- input-token or total-token budgets require explicit tokenizer/estimation semantics before admission;
- monetary/cost budgets require stable pricing/model accounting semantics;
- independent token-budget periods may be added if required;
- provider-contract anomaly handling if upstream reports usage above enforced cap;
- optional long-term aggregate usage rollups;
- optional Copilot usage-metrics ingestion for per-user/adoption analytics;
- an overlapping/grace credential-rotation model only if a future requirement explicitly prefers overlap over the current hard-cutover security contract.


## Per-user and monetary limits

Existing request-rate and output-token budgets are credential/model scoped, so they can be applied to each personal key today. **Aggregated per-user quotas across multiple keys are not yet enforced.** They require an explicit precedence/counter model keyed by Entra `(tid, oid)`.

Currency/spend limits are also not yet enforced. On-prem vLLM usage has no authoritative monetary rate; a pricing/chargeback model (for example per-model token rates or GPU-time allocation) must be defined before currency budgets are implemented.
