# Security

## Trust boundaries

1. Internet / Cloudflare edge.
2. Cloudflare Tunnel to the on-prem VM.
3. LlmProxy application boundary.
4. PostgreSQL network/container boundary.
5. Private network between gateway and DGX runtimes.

## Entra authentication and roles

Production control-plane and user self-service authentication use Microsoft Entra ID through OpenID Connect. Application roles are:

- `LlmProxy.Admin`: full configuration access plus user self-service capabilities.
- `LlmProxy.User`: normal inference consumer; may manage only personal API keys owned by the current Entra identity and inspect own usage.
- `LlmProxy.Reader`: read-only operational/admin access; retained for operators and does not grant normal user self-service by itself.

Production startup fails when Entra authentication is not enabled rather than silently exposing administration endpoints.

An installation may additionally configure a host-local `EntraId:SuperAdmins` allow-list. Matching occurs only after successful Entra authentication in the configured tenant and grants the internal `LlmProxy.Admin` role. Plain UPN/email matching is supported for operator convenience, while `oid:<object-id>` entries are preferred because Entra object IDs are stable and user principal names can change.

Personal-key ownership is based on the stable Entra `tid` + `oid` claims. Username, email and display name may be retained as non-authoritative metadata but must not be used to authorize key ownership.

See `docs/identity-api-keys.md` for the complete role, ownership and lifecycle contract.

## Inference authentication

GitHub Copilot BYOK and other OpenAI-compatible clients use bearer API keys on `/v1/*`.

Two credential forms are supported:

- **service credential**: administrator-created, with no Entra owner, suitable for shared integrations such as centrally configured Copilot or unattended applications;
- **personal credential**: self-created by an Entra `LlmProxy.User`/`LlmProxy.Admin` and permanently associated with the creator's tenant/object identity.

The bootstrap key comes from runtime configuration only and must never be committed.

## API-key storage and lifecycle

LlmProxy authenticates client keys through a cryptographic HMAC hash plus safe metadata such as key prefix, name, timestamps, optional Usage Group and optional Entra owner identifiers. The HMAC pepper remains deployment/recovery secret material outside PostgreSQL.

For newly created or rotated client credentials, LlmProxy also stores an application-encrypted recovery copy of the raw key. The recovery ciphertext is bound to the credential ID and derived from the deployment API-key pepper; it is never used for request authentication. Only `LlmProxy.Admin` (including configured super admins) can call the reveal endpoint. Reveal responses use `Cache-Control: no-store` and every reveal is audited without recording the secret. Credentials created before this feature remain unrecoverable unless the original configured bootstrap key is still available or the credential is rotated once.

Plaintext API keys must never be written to audit events, request metrics, content logs, runtime-state payloads or ordinary application logs.

## Upstream inference credentials

A protected llama.cpp/vLLM node may have its own bearer credential. This credential is **not** a client LlmProxy API key and the incoming client `Authorization` header is never forwarded upstream.

Node upstream bearers are write-only. LlmProxy encrypts them with AES-GCM before persistence; PostgreSQL and Redis runtime snapshots carry ciphertext only. Admin node responses expose only `hasUpstreamCredential`, and audit records configuration state without secret material.

The stable encryption key is supplied as `Security__UpstreamCredentialEncryptionKey` / `LLMPROXY_UPSTREAM_CREDENTIAL_KEY` and must remain outside PostgreSQL. Every replica that may route to protected nodes needs the same key. Losing or changing it makes the stored node credentials undecryptable.

For first installation, `DGX_UPSTREAM_BEARER_TOKEN` is a one-time bootstrap input. The Linux installer uses it for authenticated connectivity checks and initial node creation, then removes it from the long-lived container environment after encrypted bootstrap.

Revocation disables the existing key. Rotation changes the prefix/hash in place so credential identity, ownership, Usage Group, rate policies and usage history remain associated with the same credential record.

A user self-service request can list, rotate or revoke only a credential whose stored `(OwnerTenantId, OwnerObjectId)` matches the current Entra `(tid, oid)` pair.

## Usage governance

Request-rate policies may be enforced both on credential/model scope and, for personal keys, on aggregate Entra user/model scope. User scope is stable `tid + oid`; all applicable request policies must permit admission and their counters are acquired atomically. Output-token budgets remain credential/model scoped.

Currency/spend limits are not currently enforced. On-prem vLLM does not provide an authoritative monetary cost; a pricing/chargeback model must be defined before monetary budgets can be implemented. Token/request counts must not be presented as currency cost without such a model.

## Content logging

The product explicitly provides an administrator-only full-body inference log for authenticated Chat Completions, Responses and System One calls. Request and response payloads are captured byte-for-byte at the gateway boundary and stored only as application-encrypted ciphertext in PostgreSQL. SSE responses are retained in their wire-format text so an administrator can inspect the exact streamed exchange.

The dedicated content-log API requires `AdminWrite` / `LlmProxy.Admin`; `LlmProxy.Reader` cannot access it. Decrypted detail responses use `Cache-Control: no-store`. Authorization headers, client API keys, upstream bearer tokens and other request headers are not persisted in this store.

Full-body log retention is independently configurable from 10 through 180 days, defaults to 30 days, and is enforced by a cleanup worker every four hours. Operators must size PostgreSQL storage for the selected retention because prompts and generated payloads can be materially larger than metadata telemetry.

Ordinary request metrics remain metadata-only: timestamp, request identifier, logical model, deployment/node, API credential identifier, optional Usage Group, status, duration, TTFT and token counts where available. Full payload content must not be exported to OTEL spans, acceptance evidence or generic application logs.

## Network

- DGX vLLM endpoints remain private.
- PostgreSQL is not published outside the Docker network.
- Cloudflare Tunnel makes an outbound connection; no public inbound VM port is required for normal external access.
- A local-only published HTTP port may be retained for troubleshooting and can be firewall-restricted.

## Secrets

Secrets belong in VM/runtime secret configuration or a future secret manager. GitHub Actions secrets are used only when the workflow truly needs the value. No `.env`, Entra client secret, API key, HMAC pepper or tunnel token is committed.

Personal API keys used by scripts/applications must be injected through their normal secret/deployment mechanism rather than source control, container images or command-line logging.

## CI/CD

Production deployment uses a self-hosted runner on the target VM. The repository should remain private. The runner should be dedicated to trusted repositories and have only the host permissions required for Docker deployment.
