# Entra identity and API-key ownership

This document defines the identity, authorization, API-key and user-request-quota contract introduced across `0.2.0-preview.6` and `0.2.0-preview.7`.

## Goals

LlmProxy supports two distinct credential types:

```text
service credential   created/administered by LlmProxy administrators; no Entra owner
personal credential  created by an authenticated Entra user; permanently bound to that Entra identity
```

Existing GitHub Copilot/shared integration credentials remain valid service credentials. Personal credentials add self-service without changing the `/v1/*` bearer contract.

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

Tenant and object ID are both present or both absent. Existing service credentials therefore remain distinguishable without a schema-breaking migration.

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
```

The server derives ownership exclusively from the authenticated Entra principal. A caller cannot submit another tenant/object ID in a request body.

List/rotate/revoke operations filter by both credential ID and the current `(tid, oid)` pair. A credential owned by another user therefore behaves as not found rather than exposing ownership information.

## User provisioning and suspension

Administrator-only user-access endpoints are:

```http
GET  /api/admin/users/settings
PUT  /api/admin/users/settings
GET  /api/admin/users
POST /api/admin/users
POST /api/admin/users/{id}/disable
POST /api/admin/users/{id}/enable
```

Manual is the default provisioning mode. Automatic mode creates an enabled normal user on first successful Entra portal access. Disabling a user blocks `/api/me/*` and `/admin/me` and revokes all currently active personal API keys for the same `tid + oid`. Re-enabling portal access does not resurrect revoked keys.

## Administrator visibility

Administrators/read-only operators can inspect credential-derived identity attribution through:

```http
GET /api/admin/identity/api-credentials
GET /api/admin/identity/users
```

The existing `/api/admin/api-credentials` lifecycle remains the administration path for service credentials and broad supervision. Administrators retain the ability to revoke/rotate credentials through the existing admin contract. Administrators (not read-only operators) may also reveal an encrypted recovery copy through `GET /api/admin/api-credentials/{id}/secret` when `secretAvailable=true`.

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

This preserves the usage-telemetry privacy rule: request metrics and usage rollups do not persist prompts, source code, generated output or raw secrets. Full request/response bodies, when enabled by the product contract, live only in the separate administrator-only encrypted content-log store and are governed by its independent 10-180 day retention.

## Usage and limits

Credential/model-scoped governance remains supported:

```text
requests per time window
output tokens per time window
maximum output tokens per request
```

Starting with `0.2.0-preview.7`, administrators may also configure **aggregate user request-rate policies** keyed by stable Entra `(tid, oid)`, optionally scoped to one logical model. These limits span all personal API keys owned by the user.

Request admission uses:

```text
applicable user request policy
AND
applicable credential request policy
```

The fixed-window counters are acquired atomically: if either applicable request policy rejects, neither counter is incremented. Redis-enabled deployments coordinate the counters across gateway replicas; Redis-disabled deployments use the local in-memory store.

The personal portal and `GET /api/me/rate-limits` expose user request-limit metadata read-only. Only administrators configure user policies.

Output-token budgets remain credential/model scoped in this increment.

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

For unattended/shared production applications, ownership is an open architectural choice. Long-lived automation should generally not depend on an employee's personal key. The supported current option is an administrator-created service credential. A future option may use Entra service principals/workload identity to obtain or broker application credentials.

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
