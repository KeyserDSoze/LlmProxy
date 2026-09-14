# Usage governance

## Product contract

LlmProxy is not only a southbound router. The gateway is also the enterprise governance boundary for callers.

The intended control flow is:

```text
OpenAI-compatible client / GitHub Copilot
    -> inference authentication
    -> caller / credential resolution
    -> configurable usage group
    -> credential + logical-model rate policy
    -> routing and physical-capacity admission
    -> DGX / vLLM
    -> metadata-only request metric
    -> usage aggregation and reporting
```

These capabilities are core product scope:

1. LlmProxy owns inference authentication.
2. LlmProxy owns caller rate limiting / future quotas.
3. LlmProxy consolidates inference usage.
4. Usage can be queried by configurable groups through Admin API and React UI.

Administration remains protected separately by Microsoft Entra ID.

## Current state

Already validated:

- inference bearer/API-key authentication;
- HMAC-hashed persisted API credentials;
- per-request metrics;
- logical model, credential id, deployment/node, status, duration, TTFT, attempts/failover and token counts when upstream reports usage;
- Admin metrics views and summary API;
- physical capacity backpressure (`429 capacity_exhausted`).

Still to implement:

- request-rate policy per credential and optional logical-model override;
- usage-group domain/persistence/API/UI;
- assignment of credentials to groups;
- group-level usage aggregation/query UI;
- token/budget quotas after request-rate limiting.

## Authentication boundary

Inference requests use gateway bearer credentials:

```http
Authorization: Bearer <llmproxy-api-key>
```

The raw key is shown only once. LlmProxy persists only a secure hash plus metadata.

The authenticated API credential is the reliable caller identity available in the inference hot path. Do not infer identity from source IP.

Microsoft Entra ID is used for the administrative control plane, not as a requirement for every OpenAI-compatible inference call.

## Usage groups

### V1 model

Introduce a persisted `UsageGroup` entity with at least:

```text
Id
Name
Description (optional)
ExternalReference (optional, e.g. cost center/team code)
Enabled
CreatedAtUtc
UpdatedAtUtc
```

Each `ApiCredential` may have one optional primary `UsageGroupId`.

```text
UsageGroup 1 --- N ApiCredential
```

A single primary group is deliberate for V1: it keeps accounting/report aggregation unambiguous and prevents one request from being double-counted across overlapping groups.

Examples:

```text
Development - CRM
Development - Data & AI
Platform Engineering
Presales
Internal R&D
Customer Project FAAC
```

If overlapping labels are needed later, add separate reporting tags rather than changing the primary accounting group semantics.

### GitHub Copilot identity limitation

A centrally configured GitHub Copilot BYOK/custom provider may send one shared provider credential. In that case LlmProxy can reliably attribute requests to that credential/group, but cannot infer the individual GitHub user from the gateway request.

Therefore:

- to split usage by team entirely inside LlmProxy, configure distinct provider credentials for those teams/groups where the client configuration supports it; or
- ingest GitHub Copilot usage metrics for user/adoption reporting and correlate them analytically with LlmProxy infrastructure usage.

Never infer individual users from source IP.

## Rate limiting

Rate limiting is logically different from physical DGX saturation.

Request flow:

```text
authenticated request
    -> credential/model rate policy
        -> exceeded: 429 rate_limit_exceeded
    -> routing / physical admission
        -> saturated: 429 capacity_exhausted
    -> upstream
```

The first implementation should support request-count admission by credential with optional logical-model override.

Suggested policy fields:

```text
Id
ApiCredentialId
LogicalModel (nullable = credential default)
RequestsPerMinute
Enabled
CreatedAtUtc
UpdatedAtUtc
```

Policy is persisted in PostgreSQL but published into an in-memory limiter. PostgreSQL must not be queried on every inference request.

When exceeded, return an OpenAI-style error plus a calculated `Retry-After` header:

```json
{
  "error": {
    "message": "Rate limit exceeded for this credential and model.",
    "type": "rate_limit_error",
    "code": "rate_limit_exceeded"
  }
}
```

Do not conflate this with `capacity_exhausted`.

Token/budget quotas are a later increment because final output token usage is generally known only after the inference completes.

## Usage consolidation

Every completed or rejected inference request should remain metadata-only. Do not persist prompt/source/output bodies.

Usage reporting should support at minimum:

```text
requests
successful requests
failed requests
rate-limited requests
capacity-rejected requests
input tokens
output tokens
total tokens
streaming requests
failover requests
p50/p95 duration
p50/p95 TTFT
```

Dimensions:

```text
time window
usage group
API credential
logical model
DGX node
deployment
surface (chat_completions / responses)
status/error code
```

Because the existing request metrics already record credential/model/node/token metadata, group aggregation should be derived through the credential -> group relationship rather than duplicating group labels into every hot-path database lookup.

For historical correctness, when implementing group assignment decide explicitly whether metrics should snapshot the group id at request time. Recommended approach: persist `UsageGroupId` into `RequestMetric` at admission/completion so moving a credential to another group does not rewrite historical accounting.

## Admin API target

Recommended V1 endpoints:

```http
GET    /api/admin/usage-groups
POST   /api/admin/usage-groups
PUT    /api/admin/usage-groups/{id}
POST   /api/admin/usage-groups/{id}/enable
POST   /api/admin/usage-groups/{id}/disable

PUT    /api/admin/credentials/{id}/usage-group
DELETE /api/admin/credentials/{id}/usage-group

GET    /api/admin/rate-limits
POST   /api/admin/rate-limits
PUT    /api/admin/rate-limits/{id}
DELETE /api/admin/rate-limits/{id}

GET    /api/admin/usage/summary?hours=24
GET    /api/admin/usage/groups?hours=24
GET    /api/admin/usage/credentials?hours=24&groupId=<id>
GET    /api/admin/usage/models?hours=24&groupId=<id>
```

Exact REST shapes may evolve during implementation, but the product capability and accounting semantics above are mandatory.

All create/update/assignment/rate-policy changes must be audited.

## React Admin target

Add a **Usage & Governance** area containing:

- overall usage KPIs;
- group breakdown;
- drill-down group -> credentials -> logical models;
- request/token totals over a selectable time window;
- error/rate-limit/capacity-rejection visibility;
- usage-group CRUD;
- credential-to-group assignment;
- credential/model rate-policy administration.

No prompt or generated content should appear in usage reporting.

## Implementation order

1. Credential/model request rate limiting and distinct `rate_limit_exceeded` behavior.
2. `UsageGroup` persistence and credential assignment.
3. Snapshot `UsageGroupId` into request metrics for historically stable reporting.
4. Group usage summary queries.
5. React Usage & Governance UI.
6. Token/budget quotas if required.
7. Optional GitHub Copilot usage-metrics ingestion for per-user/adoption analytics.

Every increment must update `docs/project-status.md`, `docs/development-log.md`, `docs/roadmap.md` and `AGENTS.md` as appropriate and must not be marked validated until the complete CI/integration gate passes.
