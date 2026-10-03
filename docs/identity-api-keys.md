# Entra identity and API-key ownership

This document defines the identity, authorization, API-key and user-request-quota contract introduced across `0.2.0-preview.6` and `0.2.0-preview.7`.

## Goals

LlmProxy supports two distinct credential types:

```text
organization credential  created/administered by LlmProxy administrators; no Entra owner
personal credential      created by an authenticated Entra user; permanently bound to that Entra identity
```

Organization credentials are intended for shared workloads such as GitHub Copilot. Administrators create organization credentials through the Admin API/UI and can also create their own personal credentials through `/admin/me`. Normal users can create only personal credentials owned by their own stable Entra identity.

## Entra roles and platform-user admission

The Entra application roles remain:

```text
LlmProxy.Admin   full product administration
LlmProxy.User    optional normal-user app-role assignment
LlmProxy.Reader  read-only operational/admin visibility
```

Normal-user portal admission is now controlled by the LlmProxy platform-user registry after Entra authentication, rather than requiring the `LlmProxy.User` role on every user. The administrator selects either automatic first-login registration or manual census. Stable `tid + oid` remains the authorization identity; email/UPN is metadata only. See `docs/user-access.md`.

### Installer-configured super administrators

A production host may keep an explicit local allow-list of Entra identities that receive the internal `LlmProxy.Admin` role after Entra has authenticated them successfully:

```env
ENTRA_SUPER_ADMINS=admin1@example.com;admin2@example.com
```

This is an authorization elevation only; it does not bypass Entra authentication or tenant validation. Plain values match `preferred_username`, email or UPN. For durable production identity, prefer stable Entra object IDs:

```env
ENTRA_SUPER_ADMINS=oid:<object-id-1>;oid:<object-id-2>
```

The list is host-local configuration in `/opt/llmproxy/.env`, not source-controlled product configuration. An omitted value during update preserves the current list; explicitly passing a new list replaces it.

## Stable ownership identity

Personal-key ownership is identified by the pair:

```text
TenantId = Entra tid claim
ObjectId = Entra oid claim
```

`preferred_username`, email and display name are metadata only. They are mutable and must never be used as the ownership key.

A personal `ApiCredential` stores:

```text
OwnerTenantId
OwnerObjectId
OwnerPrincipalName   optional display/audit metadata
```

Tenant and object ID are both present or both absent. Credentials without an Entra owner are organization credentials. Personal credentials are always caller-governed; organization credentials are caller-quota exempt by default and may be opted into normal caller governance only by an administrator.

## API-key storage

The authentication model remains HMAC-based, with a separate administrator recovery layer:

1. generate a high-entropy raw secret;
2. derive a safe display prefix;
3. store the HMAC hash plus metadata used for authentication;
4. store an application-encrypted recovery copy bound to the credential ID for administrator reveal/copy;
5. return the raw secret at creation or rotation and mark secret-bearing responses `Cache-Control: no-store`;
6. keep the HMAC pepper outside PostgreSQL and preserve it as deployment/recovery secret material because it is also required to decrypt recovery copies.

The encrypted recovery value is not published to Redis/runtime credential snapshots and is never used for request authentication. Only `LlmProxy.Admin` can reveal it; every reveal is audited without the secret. Credentials created before encrypted recovery remain non-recoverable until rotated once, except the configured bootstrap key can be backfilled when its original secret is still available at startup.

A raw personal or service API key must never be written to audit, request metrics, full-body content logs, OTEL, generic application logs or runtime-state payloads.

### Scope-aware display prefixes

New credentials make ownership scope visible in the secret itself without changing the authentication primitive:

```text
lp_org_...   administrator-created organization credential
lp_usr_...   Entra-owned personal credential
```

The display prefix is informational; authentication still hashes the complete raw secret with the deployment pepper. Existing historical `lp_...` credentials remain valid and are not rewritten. Rotating an existing credential adopts the prefix for its current ownership type. Usage Group membership is deliberately not encoded in the prefix because a group is governance/accounting metadata and can change independently from credential ownership.


## Self-service endpoints

When Entra is enabled, an authenticated normal user may call these endpoints only when the platform-user registry admits the stable `tid + oid` identity. Administrators bypass the normal-user registry:

```http
GET  /api/me
GET  /api/me/api-credentials
POST /api/me/api-credentials
POST /api/me/api-credentials/{id}/rotate
POST /api/me/api-credentials/{id}/revoke
GET  /api/me/usage?days=30
GET  /api/me/rate-limits
GET  /api/me/requests?take=50
GET  /api/me/content-logs
GET  /api/me/content-logs/{id}
```

The server derives ownership exclusively from the authenticated Entra principal. A caller cannot submit another tenant/object ID in a request body.

List/rotate/revoke operations filter by both credential ID and the current `(tid, oid)` pair. A credential owned by another user therefore behaves as not found rather than exposing ownership information. Request-audit self-service follows the same rule: list queries are restricted to content-log rows linked to the caller's personal credential IDs, and a direct detail request for a non-owned row returns not found. Shared organization credentials have no user owner and are therefore excluded from self-service payload inspection.

## User provisioning and suspension

Administrator-only user-access endpoints are:

```http
GET  /api/admin/users/settings
PUT  /api/admin/users/settings
GET  /api/admin/users
POST /api/admin/users
POST /api/admin/users/{id}/disable
POST /api/admin/users/{id}/enable
PUT  /api/admin/users/{id}/usage-group
```

Manual is the default provisioning mode. Automatic mode creates an enabled normal user on first successful Entra portal access. Disabling a user blocks `/api/me/*` and `/admin/me` and revokes all currently active personal API keys for the same `tid + oid`. Re-enabling portal access does not resurrect revoked keys.

## Administrator visibility

Administrators/read-only operators can inspect credential-derived identity attribution through:

```http
GET /api/admin/identity/api-credentials
GET /api/admin/identity/users
```

The existing `/api/admin/api-credentials` lifecycle is the administration path for organization credentials and broad supervision. `PUT /api/admin/api-credentials/{id}/caller-governance` lets an administrator opt an organization key into or out of caller-specific limits; personal keys cannot opt out. Administrators retain the ability to revoke/rotate credentials through the existing admin contract. Administrators (not read-only operators) may also reveal an encrypted recovery copy through `GET /api/admin/api-credentials/{id}/secret` when `secretAvailable=true`.

## Request attribution

The inference path remains API-key based:

```text
Bearer API key
  -> HMAC lookup in local credential L1
  -> ApiCredentialId
  -> optional UsageGroupId
  -> logical model / rate policy / routing
  -> vLLM
```

For a personal credential, the runtime credential snapshot also contains `OwnerTenantId` and `OwnerObjectId`. Durable request telemetry continues to persist `ApiCredentialId`; user attribution is resolved through the credential owner instead of duplicating mutable user metadata on every request row.

This preserves the usage-telemetry privacy rule: request metrics and usage rollups do not persist prompts, source code, generated output or raw secrets. Full request/response bodies live only in the separate application-encrypted request-audit store. Administrators can inspect all retained entries; a normal user can inspect only entries attributable to that user's personal credentials. Its independent retention defaults to 30 days and is administrator-configurable from 10 through 4015 days (11 x 365 days).

## Usage, groups and limits

A platform user may belong to zero or one current Usage Group. Group assignment is administrator-controlled. The assignment is copied to the user's personal credentials so request-time telemetry keeps the existing historical `UsageGroupId` snapshot even if the user later changes group.

For a governed personal credential, caller policy composition is:

```text
applicable user policy
AND applicable usage-group policy
AND applicable credential policy
```

Each scope may define request-count limits and optional output-token budgets, optionally narrowed to one logical model. Request counters are acquired together with existing atomic AND semantics. Output-token reservations are applied to every applicable budget; the effective per-request output cap is the smallest applicable maximum.

An administrator-created **organization credential** defaults to `EnforceCallerGovernance=false`. In that mode user/group/credential caller quotas are not applied, which is the expected default for shared workloads such as centrally configured GitHub Copilot. Authentication, model routing, health/capacity admission and other platform-wide infrastructure protections still apply. An administrator may explicitly enable caller governance on that organization credential; any credential-specific and assigned-group policies then become applicable.

The personal portal and `GET /api/me/rate-limits` expose the current user's effective user/group request and token policies read-only. Only administrators configure these policies.

### Monetary/spend limit

No currency budget is enforced today. On-prem vLLM has no authoritative provider invoice from which LlmProxy can infer cost. A spend feature therefore requires a product decision defining the cost model, for example:

- internal price per input/output token per logical model;
- GPU-time chargeback;
- fixed department/project allocation;
- externally supplied accounting rates.

Until that model exists, token/request counts are factual usage, not currency cost. LlmProxy must not invent a monetary value.

## Scripts, applications and deployment

For interactive/user-owned development, a personal API key can be created in the portal/API and injected into the application through the application's normal secret mechanism:

```text
Authorization: Bearer <LlmProxy personal key>
```

Do not commit it to source control, container images, scripts or CI logs.

For unattended/shared production applications, use an administrator-created organization credential rather than an employee's personal key. Organization credentials are caller-quota exempt by default but may be opted into governance when the workload should behave like a governed client. A future option may use Entra service principals/workload identity to obtain or broker application credentials.

## Open decisions

Before extending governance beyond the current increment, decide:

1. maximum active personal keys per user;
2. default/maximum personal-key lifetime and mandatory rotation policy;
3. whether administrators may create a personal key on behalf of a user (currently no);
4. whether personal keys may be assigned to Usage Groups by users or only administrators;
5. whether aggregate **output-token** budgets should also exist at user scope and how they interact with credential budgets;
6. the monetary/chargeback model required for spend limits;
7. whether unattended applications remain on service API keys or move to an Entra workload-identity flow;
8. whether Microsoft Graph/directory reconciliation should automatically disable LlmProxy users whose Entra account is disabled/deleted; local administrator disable already revokes active personal keys.

## External acceptance

Repository tests validate ownership invariants and claim resolution without calling Entra. Real acceptance still requires an actual app registration and browser login in the target tenant. Validate Admin/Reader roles, both manual and automatic platform-user provisioning, user disable/re-enable, personal-key revocation, user dashboard calls and end-to-end inference.
