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

Personal-key ownership is based on the stable Entra `tid` + `oid` claims. Username, email and display name may be retained as non-authoritative metadata but must not be used to authorize key ownership.

See `docs/identity-api-keys.md` for the complete role, ownership and lifecycle contract.

## Inference authentication

GitHub Copilot BYOK and other OpenAI-compatible clients use bearer API keys on `/v1/*`.

Two credential forms are supported:

- **service credential**: administrator-created, with no Entra owner, suitable for shared integrations such as centrally configured Copilot or unattended applications;
- **personal credential**: self-created by an Entra `LlmProxy.User`/`LlmProxy.Admin` and permanently associated with the creator's tenant/object identity.

The bootstrap key comes from runtime configuration only and must never be committed.

## API-key storage and lifecycle

LlmProxy persists only a cryptographic HMAC hash plus safe metadata such as key prefix, name, timestamps, optional Usage Group and optional Entra owner identifiers. The HMAC pepper remains deployment/recovery secret material outside PostgreSQL.

Raw keys are shown exactly once at creation or rotation. Responses containing the one-time secret use `Cache-Control: no-store`. Raw API keys must never be persisted in PostgreSQL, audit, request metrics, logs or runtime-state payloads.

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

Prompts, source code and generated content are not operational telemetry. They must not be persisted by default.

Allowed default metadata includes timestamp, request identifier, logical model, deployment/node, API credential identifier, optional Usage Group, status, duration, TTFT and token counts where available. Personal-user attribution is resolved through the credential owner rather than copying user PII into every request record.

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
