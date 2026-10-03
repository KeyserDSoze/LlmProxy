# Usage governance

This document is the focused product/engineering contract for inference authentication, credential lifecycle, caller request-rate limiting, output-token budgets, Usage Groups and consolidated usage reporting.

## Product boundary

Current request governance is:

```text
OpenAI-compatible inference request
  -> bearer/API credential authentication from local L1
  -> credential + Entra owner + UsageGroup resolution
  -> organization-key caller-governance mode
  -> user + group + credential output-token reservations when applicable
  -> user + group + credential request-rate admission when applicable
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

The `/v1` authentication path remains HMAC-only: PostgreSQL stores the HMAC-SHA256 hash and safe metadata, and the supplied bearer secret is hashed and resolved from the local runtime credential cache without a synchronous credential SQL lookup per request.

Credential creation and rotation return the generated raw secret with `Cache-Control: no-store`. In addition, newly created/rotated credentials store an application-encrypted recovery copy in PostgreSQL so `LlmProxy.Admin` can reveal/copy it later. The recovery ciphertext is not part of runtime state and is never used for authentication. Raw secrets remain excluded from audit, request metrics, content logs, OTEL and generic application logs.

Credentials are either administrator-created **organization credentials** or Entra-owned **personal credentials**. Personal ownership is immutable `(tid, oid)` metadata on the credential; usernames/email are display metadata only. Personal credentials always participate in caller governance. Organization credentials default to caller-governance off and can be opted in only by an administrator. The runtime snapshot carries owner IDs, while durable request telemetry continues to store `ApiCredentialId` so user attribution is resolved without duplicating user PII per request.

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
SecretCiphertext
```

Rules:

- revoked credentials cannot be rotated;
- a fresh `lp_...` secret is generated server-side;
- the HMAC and safe prefix remain the authentication material;
- an encrypted administrator recovery copy is stored with the credential;
- the raw replacement secret is returned to the rotating caller and may later be revealed only by `LlmProxy.Admin`;
- secret-bearing responses are marked `Cache-Control: no-store`;
- audit action is `credential.rotate`;
- audit details contain safe previous/new prefixes, expiry/group metadata and no raw secret/HMAC;
- the origin replica updates its local credential L1 only after the PostgreSQL save succeeds;
- Redis-enabled replicas receive the new credential snapshot through the transactional runtime-state outbox;
- cache upsert by the same credential ID removes the previous hash, so the old secret is no longer accepted after local convergence;
- Redis credential state is an upsert for the same ID and therefore replaces the previous HMAC rather than retaining an alias;
- restart/startup hydration restores only the new hash.

This is a hard cutover rather than a grace-period rotation. Clients must update to the newly returned secret immediately. A future overlapping-key model would require an explicit product/security decision and a different persisted model; do not silently add multiple accepted hashes to one credential.

## Usage Groups

Usage Groups now group both platform users and credentials. A platform user has zero or one current `UsageGroupId`; administrators control that membership. New personal credentials inherit the user's current group, and changing user membership propagates the group to that user's personal credentials.

The request-time group id is copied into request metrics. Historical accounting therefore remains attached to the group that owned the request at the time, even when a user or credential changes group later.

Organization credentials may also be assigned to a Usage Group. Their group quota applies only when the organization credential's `EnforceCallerGovernance` switch is enabled.

Never infer a user or Usage Group from source IP. A centrally configured GitHub Copilot credential may be shared, so gateway attribution is credential/group-level unless a trustworthy per-user identity is actually present.

## Unified caller policy scope

Three caller scopes can participate when caller governance is enabled:

```text
UserRateLimitPolicy          stable Entra tid + oid
UsageGroupRateLimitPolicy    one UsageGroupId
RateLimitPolicy              one API credential
```

Each scope supports an optional logical-model override, request count/window, and optional output-token budget using `OutputTokensPerWindow + MaxOutputTokensPerRequest`.

Policy precedence inside one scope remains exact logical model over all-model fallback. Across scopes, policies compose rather than override one another:

```text
user AND group AND credential
```

An organization credential with `EnforceCallerGovernance=false` skips these caller-specific policies. When an administrator enables that switch, its credential policy and any assigned group policy become active. Organization credentials do not acquire a synthetic user identity.

## Request-rate admission

Request-rate admission is fixed-window. Redis-enabled deployments use shared Redis counters across gateway replicas; Redis-disabled deployments use the local in-memory store.

For governed credentials, request admission evaluates every applicable user, Usage Group and credential policy together. Counter acquisition is atomic across the applicable request-count policies: if any applicable scope rejects, none of those request counters is incremented. User policies therefore aggregate traffic from every personal credential owned by the same stable `(tid, oid)`.

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


When multiple token budgets apply, LlmProxy reserves the same request cap against each applicable user/group/credential policy. The request's injected maximum is the smallest applicable `MaxOutputTokensPerRequest`. Successful completion settles the actual output against every acquired reservation. If a later scope cannot reserve, earlier reservations from that request are released before rejection, preventing a failed multi-scope admission from leaking reserved budget.

### Redis-enabled multi-replica semantics

Redis-enabled deployments use atomic reservation/settlement against one shared fixed window. Redis server time is used. If reservation coordination is unavailable there is no local distributed fallback:

```text
HTTP 503
Retry-After: 1
error.code = token_budget_coordination_unavailable
```

If settlement itself fails after reservation, the reserved amount remains charged rather than accidentally expanding the budget.

## User self-service API

When Entra is enabled, self-service requires successful Entra authentication plus an enabled LlmProxy platform-user record. The registry can be automatic-on-first-access or administrator-censused; administrators bypass the normal-user registry:

```http
GET  /api/me
GET  /api/me/api-credentials
POST /api/me/api-credentials
POST /api/me/api-credentials/{id}/rotate
POST /api/me/api-credentials/{id}/revoke
GET  /api/me/usage?days=30
GET  /api/me/rate-limits
GET  /api/me/requests?take=50
```

Ownership is derived from the authenticated Entra principal; owner IDs are never accepted from self-service request bodies. User admission uses stable `tid + oid`, not email. Disabling a platform user revokes active personal keys and blocks self-service until re-enabled.

## Admin API

Current governance endpoints include:

```http
GET  /api/admin/users/settings
PUT  /api/admin/users/settings
GET  /api/admin/users
POST /api/admin/users
POST /api/admin/users/{id}/disable
POST /api/admin/users/{id}/enable

GET  /api/admin/api-credentials
POST /api/admin/api-credentials
GET  /api/admin/api-credentials/{id}/secret
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

GET    /api/admin/user-rate-limits
POST   /api/admin/user-rate-limits
PUT    /api/admin/user-rate-limits/{id}
DELETE /api/admin/user-rate-limits/{id}

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

`/admin/governance` exposes Usage KPIs, Usage Groups, credential-to-group assignment, credential rotation, credential request-rate policies, aggregate Entra-user request limits and output-token budgets. `/admin/me` is the normal-user portal for personal key lifecycle, own usage and read-only user request-limit visibility.

Credential administration UI behavior:

- enabled credentials expose `Rotate`;
- credentials with encrypted recovery expose `Reveal / copy` to administrators;
- credentials created before recovery support show `Rotate once`;
- successful rotation displays the new secret and stores the encrypted recovery copy;
- the previous key is invalid immediately after rotation;
- the refreshed row shows the new prefix while preserving Usage Group selection;
- reveal/rotation responses are no-store and reveal actions are audited without secret material.

## Usage metrics and reporting

Request metrics remain metadata-only and include credential/group/model/status/timing plus observed input/output/total tokens where upstream reports them. Full request/response payloads are stored only in the separate encrypted administrator content-log store; bearer/API secrets and request headers are excluded.

Usage aggregation is PostgreSQL-side and reports by Usage Group, credential and logical model. Current raw request retention is 90 days by default; optional long-term rollups remain future work.

## Validation

Validated preview.7 runtime baseline:

```text
source      df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI          35592623906 SUCCESS
Full Stack  35592624282 SUCCESS
Publish     35593081824 SUCCESS
image       sha-df3ecf7
digest      sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
```

Standard CI proves credential and user request-limit domain semantics, frontend build/Vitest/Playwright, PostgreSQL migration, governance/restart-republish, retention and restore regressions.

The governance PostgreSQL smoke proves two personal credentials sharing one Entra `tid+oid` consume one aggregate user request quota and that the persisted user policy republishes after gateway restart.

The Full Stack Redis smoke proves:

- a user request policy created after two gateways are running propagates through the transactional RatePolicy outbox channel;
- two different personal API keys with the same Entra owner share one Redis fixed-window counter across gateway replicas;
- the third cross-gateway request is rejected with `429 rate_limit_exceeded`;
- a rejected request does not increment the shared user counter;
- existing distributed output-token reservation/refund, credential rotation and safe-maintenance regressions remain green.

## Remaining governance backlog

- input-token or total-token budgets require explicit tokenizer/estimation semantics before admission;
- monetary/cost budgets require stable pricing/model accounting semantics;
- independent token-budget periods may be added if required;
- provider-contract anomaly handling if upstream reports usage above enforced cap;
- optional long-term aggregate usage rollups;
- optional Copilot usage-metrics ingestion for per-user/adoption analytics;
- an overlapping/grace credential-rotation model only if a future requirement explicitly prefers overlap over the current hard-cutover security contract.


## Per-user and monetary limits

Aggregate **request-count** limits across all personal keys are implemented at Entra user/model scope. They compose with per-credential request limits using AND semantics.

Output-token budgets remain credential/model scoped. An aggregate user token budget would need explicit reservation/settlement precedence semantics before implementation.

Currency/spend limits are not enforced. On-prem vLLM usage has no authoritative monetary rate; a pricing/chargeback model (for example per-model token rates or GPU-time allocation) must be defined before currency budgets are implemented.
