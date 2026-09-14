# Usage governance

This document is the focused product/engineering contract for inference authentication, caller rate limiting, configurable Usage Groups and consolidated usage reporting.

Current implementation status: **validated** on the repository baseline documented in `docs/project-status.md`.

## Product boundary

LlmProxy owns this request chain:

```text
OpenAI-compatible inference request
    -> bearer/API credential authentication
    -> request-time credential + primary UsageGroup resolution
    -> credential/model request-rate admission
    -> logical-model routing + physical capacity admission
    -> DGX / vLLM
    -> metadata-only usage metric
    -> consolidated reporting
```

This is intentionally different from physical DGX capacity control:

```text
caller policy exceeded -> 429 rate_limit_exceeded
physical capacity full -> 429 capacity_exhausted
no operational backend -> 503 no_healthy_deployment
```

## Inference credentials

Raw API secrets are generated/shown once and are not persisted. PostgreSQL stores the HMAC-SHA256 hash plus safe metadata.

Current runtime path:

```text
Bearer secret
  -> HMAC-SHA256 with server-side pepper
  -> in-memory credential cache lookup
  -> enabled + expiry check
  -> credential id + UsageGroupId snapshot placed in HttpContext
```

The authentication middleware does not query PostgreSQL per `/v1` request.

### Runtime cache consistency

PostgreSQL remains durable source of truth. `IApiCredentialCache` is rebuilt at gateway startup and contains only:

```text
Id
KeyHash
Enabled
ExpiresAtUtc
UsageGroupId
```

An EF SaveChanges interceptor observes `ApiCredential` additions/modifications/deletions and publishes the corresponding runtime snapshot only after the database save succeeds. This means credential creation, revocation and Usage Group assignment/clear become visible to inference without restart while avoiding cache state that is ahead of durable state.

`LastUsedAtUtc` is deliberately eventually consistent. Authentication enqueues usage metadata to a background sink, which throttles/batches PostgreSQL updates instead of performing a write on the request path.

## Usage Groups

V1 accounting semantics are deliberately unambiguous:

```text
UsageGroup 1 --- N ApiCredential
ApiCredential -> zero or one primary UsageGroup
```

Current persisted Usage Group fields:

```text
Id
Name
Description
CreatedAtUtc
UpdatedAtUtc
```

Current credential field:

```text
UsageGroupId : Guid?
```

When inference is authenticated, the current `UsageGroupId` is copied into the request metric. This request-time snapshot is critical: moving a credential to another group later does not rewrite historical accounting.

No source IP or network heuristic is used to infer identity or group membership.

## Request-rate policy

Current policy fields:

```text
Id
ApiCredentialId
LogicalModel : string?     # null = credential-wide default
RequestsPerWindow : int
WindowSeconds : int
Enabled : bool
CreatedAtUtc
UpdatedAtUtc
```

Policy precedence:

```text
credential + exact logical model
    overrides
credential-wide policy (LogicalModel = null)
```

If no enabled policy matches, the request is not caller-rate-limited.

The active policy set is held by the thread-safe in-memory `RequestRateLimiter`. Admin changes are persisted then republished, and gateway startup rebuilds runtime policy from PostgreSQL.

Current algorithm is fixed-window request admission. Rate state is runtime-only and intentionally starts with a fresh window after process restart; policy configuration itself survives the restart.

A rejected request returns `429 Too Many Requests`, an OpenAI-style error with `error.code = rate_limit_exceeded`, and a computed `Retry-After`. It is recorded as metadata telemetry and is not forwarded to DGX.

## Usage metric snapshot

A request metric contains the accounting/routing metadata needed for reporting, including:

```text
RequestId
StartedAtUtc
LogicalModel
Surface
DeploymentId?
NodeId?
ApiCredentialId?
UsageGroupId?
StatusCode
DurationMilliseconds
AttemptCount
IsStreaming
UpstreamHeaderMilliseconds?
TimeToFirstByteMilliseconds?
InputTokens?
OutputTokens?
TotalTokens?
ErrorCode?
```

Prompts, source code, generated output and bearer secrets are not persisted by default.

## Reporting APIs

Current admin endpoints include:

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

GET /api/admin/governance/credentials

GET /api/admin/usage/summary?days=30
GET /api/admin/usage/groups?days=30
GET /api/admin/usage/credentials?days=30
GET /api/admin/usage/models?days=30
```

`days` is clamped to the supported 1..365 day range.

Usage aggregation remains PostgreSQL-side. The query materializes aggregate rows rather than loading individual request metrics into application memory. Final mapping/order may happen after aggregate materialization where required by EF/Npgsql translation constraints.

Current summary dimensions:

- Usage Group;
- API credential;
- logical model.

Current measures include:

- request count;
- error count;
- rate-limited request count;
- capacity-exhausted total in overall summary;
- input/output/total tokens;
- average TTFT and average duration for group reporting.

The broader inference observability endpoints continue to provide p50/p95 latency and node/model breakdowns.

## React control plane

The focused UI is:

```text
/admin/governance
```

It provides:

- usage KPIs;
- Usage Group list/create/update-oriented administration;
- credential -> group assignment/clear;
- rate-policy create/enable/disable/delete workflows;
- usage breakdown by group, credential and logical model.

Administrative changes are audited. Secrets and prompt/output content are excluded from audit detail.

## Validated behavior

Caller Governance was validated by the complete CI/integration gate on:

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

The later credential-auth runtime cache was validated on:

```text
commit 1f607c8433fe2ca08a1c243b68d87587204f35ee
CI     34860662747
```

The Docker/PostgreSQL Governance smoke proves:

- Usage Group creation and credential assignment;
- first two requests accepted under a 2/60s policy;
- third request receives `429 rate_limit_exceeded` + `Retry-After`;
- rate-limit error remains distinct from `capacity_exhausted`;
- group snapshot is visible in usage history;
- group/model/credential usage aggregation;
- policy and group membership survive restart;
- runtime rate policy is republished after restart;
- live Usage Group assignment after startup is seen by the in-memory credential cache.

## Identity limitation: GitHub Copilot

A centrally configured Copilot custom/BYOK provider may use one shared provider credential. In that topology LlmProxy can attribute traffic to that credential and its Usage Group, but it cannot reliably identify the individual GitHub user behind a request.

Therefore:

- use separate provider credentials for team/group attribution where the client/tenant configuration permits it;
- optionally ingest GitHub Copilot usage metrics later for per-user/adoption analytics;
- never treat source IP as user identity.

## Remaining governance backlog

### Token / budget quotas

Request-rate limiting is admission-time and is complete. Token quotas are different because final output token usage is usually known only when inference finishes. Before implementing quotas, define explicit reservation/settlement semantics, including streaming, cancellation, upstream failures and overage behavior.

### Retention

`request_metrics` currently grows without a product retention policy. Add configurable retention (initial target 30–90 days), background cleanup and optional long-term rollups. Audit retention may need a separate, longer policy.

### External analytics

GitHub Copilot usage-metrics ingestion remains optional/external for per-user or adoption analytics and must not be confused with gateway credential/group accounting.

## Related next hot-path work

Credential authentication and request-rate admission are now in-memory, but `EfDeploymentCatalog` still queries PostgreSQL per inference request for logical model/node/deployment resolution. That work belongs to routing/hot-path hardening rather than this governance contract and is the current engineering resume point in `AGENTS.md` / `docs/project-status.md`.
