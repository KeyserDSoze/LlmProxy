# Security

## Trust boundaries

1. Internet / Cloudflare edge.
2. Cloudflare Tunnel to the on-prem VM.
3. LlmProxy application boundary.
4. PostgreSQL network/container boundary.
5. Private network between gateway and DGX runtimes.

## Administration authentication

Production administration uses Microsoft Entra ID through OpenID Connect. Initial application roles are:

- `LlmProxy.Admin`: full configuration access.
- `LlmProxy.Reader`: read-only operational access.

Production startup is expected to fail when Entra authentication is not enabled rather than silently exposing administration endpoints.

## Inference authentication

GitHub Copilot BYOK requires a static provider credential. `/v1/*` therefore accepts a bearer API key. The bootstrap key comes from runtime configuration only and must never be committed.

The productized credential store will persist only a cryptographic hash plus metadata. Raw keys are shown once at creation time.

## Content logging

Prompts, source code and generated content are not operational telemetry. They must not be persisted by default.

Allowed default metadata includes timestamp, request identifier, logical model, deployment/node, status, duration, TTFT and token counts where available.

## Network

- DGX vLLM endpoints remain private.
- PostgreSQL is not published outside the Docker network.
- Cloudflare Tunnel makes an outbound connection; no public inbound VM port is required for normal external access.
- A local-only published HTTP port may be retained for troubleshooting and can be firewall-restricted.

## Secrets

Secrets belong in VM/runtime secret configuration or a future secret manager. GitHub Actions secrets are used only when the workflow truly needs the value. No `.env`, client secret, API key or tunnel token is committed.

## CI/CD

Production deployment uses a self-hosted runner on the target VM. The repository should remain private. The runner should be dedicated to trusted repositories and have only the host permissions required for Docker deployment.
