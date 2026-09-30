# Entra identity and API-key ownership

This document defines the identity, authorization, API-key and user-request-quota contract introduced across `0.2.0-preview.6` and `0.2.0-preview.7`.

## Goals

LlmProxy supports two distinct credential types:

```text
service credential   created/administered by LlmProxy administrators; no Entra owner
personal credential  created by an authenticated Entra user; permanently bound to that Entra identity
```

Existing GitHub Copilot/shared integration credentials remain valid service credentials. Personal credentials add self-service without changing the `/v1/*` bearer contract.

## Entra roles

The application roles are:

```text
LlmProxy.Admin   full product administration + self-service
LlmProxy.User    normal product user; personal API-key and own-usage self-service
LlmProxy.Reader  read-only operational/admin visibility; retained for operators
```

`LlmProxy.User` is intentionally different from `LlmProxy.Reader`: a Reader is an operator with read access to the administrative control plane, while a User is a consumer of the inference service.

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

The existing security model remains authoritative:

1. generate a high-entropy raw secret;
2. derive a safe display prefix;
3. store only the HMAC hash plus metadata in PostgreSQL;
4. return the raw secret exactly once at creation or rotation;
5. send `Cache-Control: no-store` on responses containing a raw key;
6. keep the HMAC pepper outside PostgreSQL and preserve it as deployment/recovery secret material.

A raw personal or service API key must never be written to audit, request metrics, logs or runtime-state payloads.

## Self-service endpoints

When Entra is enabled, `LlmProxy.Admin` and `LlmProxy.User` may call:

```http
GET  /api/me
GET  /api/me/api-credentials
POST /api/me/api-credentials
POST /api/me/api-credentials/{id}/rotate
POST /api/me/api-credentials/{id}/revoke
GET  /api/me/usage?days=30
GET  /api/me/rate-limits
```

The server derives ownership exclusively from the authenticated Entra principal. A caller cannot submit another tenant/object ID in a request body.

List/rotate/revoke operations filter by both credential ID and the current `(tid, oid)` pair. A credential owned by another user therefore behaves as not found rather than exposing ownership information.

## Administrator visibility

Administrators/read-only operators can inspect identity attribution through:

```http
GET /api/admin/identity/api-credentials
GET /api/admin/identity/users
```

The existing `/api/admin/api-credentials` lifecycle remains the administration path for service credentials and broad supervision. Administrators retain the ability to revoke/rotate credentials through the existing admin contract.

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

This preserves the existing privacy rule: prompts, source code, generated output and raw secrets are not persisted as usage telemetry.

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
8. whether a disabled/deleted Entra account should trigger automatic key revocation and how directory reconciliation would be performed.

## External acceptance

Repository tests validate ownership invariants and claim resolution without calling Entra. Real acceptance still requires an actual app registration with roles `LlmProxy.Admin`, `LlmProxy.User` and (if used) `LlmProxy.Reader`, plus browser login and end-to-end self-service/inference tests in the target tenant.
