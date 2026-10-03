# End-user access and provisioning

## Purpose

LlmProxy keeps end-user portal authorization separate from administrator/operator roles. Microsoft Entra authenticates the person; LlmProxy then decides whether that stable Entra identity is an enabled platform user.

The security key is always:

```text
TenantId = tid
ObjectId = oid
```

Email, UPN, principal name and display name are mutable metadata. They are useful for display/search but are never the authorization key.

## Provisioning modes

The administrator chooses one global mode in **Admin -> Users & Access**.

### Manual

```text
Entra sign-in succeeds
  -> lookup platform_users by tid + oid
  -> registered + enabled   => /admin/me allowed
  -> missing or disabled   => 403
```

Manual is the default. Administrators register users with the stable Entra object ID, optional tenant ID override, and optional display metadata.

### Automatic

```text
Entra sign-in succeeds
  -> lookup platform_users by tid + oid
  -> missing => create enabled user with provisioningSource=automatic
  -> enabled => /admin/me allowed
  -> disabled => 403
```

Automatic provisioning never creates an administrator role. It creates only a normal platform-user record.

Existing personal-key owners are migrated into the user registry at startup with `provisioningSource=migration` so enabling the new registry does not lock out previously established users.

## Administrator API

```http
GET  /api/admin/users/settings
PUT  /api/admin/users/settings
GET  /api/admin/users
POST /api/admin/users
POST /api/admin/users/{id}/disable
POST /api/admin/users/{id}/enable
PUT  /api/admin/users/{id}/usage-group
```

Provisioning mode update:

```json
{ "provisioningMode": "manual" }
```

or:

```json
{ "provisioningMode": "automatic" }
```

Manual registration:

```json
{
  "tenantId": "<entra-tenant-id>",
  "objectId": "<entra-user-object-id>",
  "principalName": "user@example.com",
  "displayName": "Example User",
  "usageGroupId": "<optional-usage-group-id>",
  "enabled": true
}
```

When `tenantId` is omitted, the configured `EntraId:TenantId` is used.

## User groups

The existing **Usage Group** entity is also the end-user grouping boundary. A platform user may have zero or one current group. Administrators assign the group in **Users & Access**.

When the group changes:

1. the platform-user row is updated;
2. all of that user's personal API credentials receive the same current `UsageGroupId`;
3. new personal keys inherit the user's current group;
4. historical request metrics keep the group snapshot captured when the request happened.

This gives administrators group-level usage reporting and group-level request/output-token quotas without rewriting history.

## Disable semantics

Disabling a platform user does two things atomically in the control plane:

1. marks the platform user disabled so `/admin/me` and `/api/me/*` authorization fails;
2. revokes every currently enabled **personal API key** owned by the same `tid + oid`.

Credential revocation flows through the existing runtime-cache/outbox path, so inference calls using those personal keys stop being accepted.

Re-enabling restores portal access but intentionally does **not** resurrect revoked keys. The user creates new credentials after reactivation.

Administrator/super-admin access is not controlled by the normal-user registry.

## User dashboard

`/admin/me` exposes only the signed-in user's own data:

- stable Entra identity metadata;
- personal API-key lifecycle;
- 30-day request/token/error usage;
- applicable user and group request/output-token limits;
- latest request metadata from the user's personal credentials.

Recent-call rows expose metadata such as time, logical model, surface, HTTP status, duration, TTFT, token counts and error code. Full prompt/response payload inspection remains administrator-only in Content Logs.

## GitHub Copilot and end-user identity

A shared GitHub Copilot custom-model/BYOK provider credential identifies the configured provider/workload at LlmProxy, not a guaranteed individual developer identity.

The public GitHub custom-model/BYOK contract documents provider configuration such as provider type, base URL and API/bearer credential, but does not define a guaranteed provider-facing per-request header containing a developer email, GitHub user ID/login or Microsoft Entra object ID. LlmProxy therefore must not use undocumented headers, source IP or User-Agent as an authorization or billing identity.

GitHub exposes separate Copilot usage metrics with user-level identifiers such as `user_id` and `user_login`. Those reports may be useful for adoption/aggregate analytics, but they are not treated as a deterministic request-by-request identity signal for an LlmProxy inference call.

For deterministic per-user gateway attribution choose one of:

1. one personal LlmProxy credential per user/client configuration;
2. a trusted intermediary that authenticates the person and injects a signed identity assertion under a contract LlmProxy controls.

Disabling a user in LlmProxy cannot selectively block that person from a **shared** Copilot provider key because the gateway cannot distinguish them from other users sharing the key. Individual Copilot access must be removed in GitHub, or the integration must use a per-user identity/credential design.
