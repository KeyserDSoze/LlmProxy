# LlmProxy

Enterprise OpenAI-compatible gateway for routing GitHub Copilot and other AI clients to on-premises LLMs running on NVIDIA DGX infrastructure.

> Current preview line: `0.2.0-preview.4`.

## What this product is

LlmProxy is the control and governance boundary between AI clients and a physical inference fleet. Clients see one stable OpenAI-compatible endpoint and logical model names; the gateway resolves credentials/policies, selects an eligible DGX/model deployment, enforces distributed admission/governance and streams the response.

The supported Linux production topology is intentionally single-host for the control plane while DGX/vLLM remains on the private LAN:

```text
GitHub Copilot / OpenAI-compatible clients
                  |
          optional Cloudflare Tunnel
                  |
+------------------------------------------------------+
| Linux production host                                |
|                                                      |
| LlmProxy + PostgreSQL + Redis                        |
| OpenTelemetry Collector                              |
| Prometheus + Tempo + Loki + Grafana                  |
+-----------------------+------------------------------+
                        |
                        | private LAN
                        v
                  DGX Spark / vLLM
```

PostgreSQL is durable truth, Redis provides shared runtime/coordination state, and local RAM remains the request-path configuration L1.

## Core capabilities

- OpenAI-compatible `/v1/models`, Chat Completions and Responses APIs.
- Incremental SSE streaming and cancellation.
- Logical public model aliases with internal DGX/provider model identifiers.
- Weighted least loaded, round robin and weighted round robin routing.
- Health hysteresis and safe drain/resume maintenance.
- Distributed physical-capacity admission with Redis leases.
- HMAC-backed bearer credentials with one-time creation/rotation secrets.
- Usage Groups, request-rate governance and output-token budgets.
- Historical PostgreSQL usage rollups beyond raw-metric retention.
- Transactional PostgreSQL -> Redis runtime-state outbox.
- Metadata-only metrics/audit/OTEL; prompts/source/generated content are excluded by default.
- PostgreSQL backup/restore operators.
- SemVer/build identity, release notes, GHCR digest evidence, SPDX SBOM and SLSA provenance.

## Repository structure

```text
src/                    product code (.NET 10 + React/TypeScript)
tests/                  unit, frontend, integration and performance tests
docker/                 images, Compose, observability and operator scripts
docs/                   architecture/deployment/operations documentation
.github/workflows/       CI, publication and production deployment
AGENTS.md                mandatory engineering handover entry point
CHANGELOG.md              product-visible release history
```

## Development quickstart

For a minimal local development stack use the quickstart assets documented in the repository. For the distributed Redis/observability bundle use:

```bash
bash docker/scripts/full-stack-init.sh
# edit docker/.env.full, especially DGX_NODE_BASE_ADDRESS and PROVIDER_MODEL_NAME
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

The generic full-stack example is intended for development/demo/acceptance setup. Production has a separate operator-owned environment file and deployment path.

## Linux production deployment

The canonical production runbook is:

```text
docs/linux-production-deployment.md
```

Production uses the Redis-enabled full stack, not the legacy minimal overlay.

Prepare an operator-owned environment file:

```bash
sudo install -d -m 0750 -o "$USER" -g "$USER" /opt/llmproxy
cp docker/.env.production.example /opt/llmproxy/.env
chmod 600 /opt/llmproxy/.env
```

After replacing all `CHANGE_ME` values and validating DGX connectivity, deploy a published image:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh main
```

For controlled production changes prefer an immutable `sha-<7>` alias or an exact SemVer tag rather than mutable `main`.

`deploy.sh`:

1. validates required production settings;
2. refuses public Cloudflare exposure until Entra is enabled/configured;
3. stages Compose/observability assets under `/opt/llmproxy/runtime`;
4. validates the rendered Compose model;
5. pulls and starts the full stack;
6. requires both `/healthz` and `/readyz` to succeed.

Cloudflare is optional. Leave `CLOUDFLARE_TUNNEL_TOKEN` blank for private-LAN bootstrap. When configured, the deploy script enables the `cloudflare` Compose profile automatically.

## Automated production deployment

`.github/workflows/deploy.yml` is the supported GitHub Actions deployment path. It runs on a dedicated Linux self-hosted runner labelled:

```text
self-hosted
linux
x64
llmproxy-prod
```

The runner keeps production secrets in `/opt/llmproxy/.env`; secrets are not committed to Git. The workflow uses the same `docker/scripts/deploy.sh` as manual deployment, so there is one production implementation rather than separate manual/CI paths.

## DGX service roots

A node stores the complete inference service root, including optional path prefix:

```text
http://10.0.0.25:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

LlmProxy derives:

```text
<root>/health
<root>/v1/models
<root>/v1/chat/completions
<root>/v1/responses
```

The Admin UI includes a connection test for the configured service root.

## Public API

```http
GET  /v1/models
POST /v1/chat/completions
POST /v1/responses
GET  /healthz
GET  /readyz
```

Inference uses bearer credentials:

```http
Authorization: Bearer lp_xxxxxxxxxxxxxxxxxxxxxxxxx
```

Raw credential secrets are returned only at creation/rotation time and are not stored in PostgreSQL.

## Administration

The React control plane manages nodes, models, deployments, routing, credentials, governance, usage, maintenance, runtime synchronization and audit.

Production administration is designed for Entra ID with roles:

```text
LlmProxy.Admin
LlmProxy.Reader
```

Do not expose administrative surfaces publicly before Entra is configured and validated.

## Persistence and runtime state

```text
PostgreSQL = durable configuration/history + runtime-state outbox + usage rollups
Redis      = distributed L2 + request/token/capacity/maintenance coordination
local RAM  = per-replica request-path configuration L1
```

Ordinary inference configuration lookups are DB-free after startup/runtime publication.

## Backup and recovery

PostgreSQL is the recovery authority; Redis is rebuildable. Bash and PowerShell backup/restore operators live under `docker/scripts/`.

`Authentication__ApiKeyPepper` and deployment secrets are external recovery dependencies and must be preserved separately from database backups.

Read `docs/backup-restore.md` before production restore work.

## CI/CD and supply-chain evidence

Pushes/PRs execute backend, frontend, Docker/PostgreSQL and operational smokes. A container is published only after successful CI for the same `main` source SHA.

Published images include:

```text
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
```

The publish workflow records the immutable image digest and verifies registry-native SPDX SBOM and SLSA/BuildKit provenance attestations. Exact SemVer tag publication additionally requires that the tagged source SHA already has a successful `CI` push run on `main`.

## Important production configuration

Never commit production values for:

```text
POSTGRES_PASSWORD
REDIS_PASSWORD
LLM_PROXY_API_KEY
LLM_PROXY_API_KEY_PEPPER
GRAFANA_ADMIN_PASSWORD
ENTRA_TENANT_ID
ENTRA_CLIENT_ID
ENTRA_CLIENT_SECRET
CLOUDFLARE_TUNNEL_TOKEN
```

Use `docker/.env.production.example` as the production template.

## Documentation map

Start with:

- `docs/linux-production-deployment.md` — canonical Linux production runbook.
- `docs/deployment.md` — deployment contract and automation summary.
- `docs/full-stack.md` — Redis + observability bundle details.
- `docs/operations.md` — health, maintenance, release identity and audit.
- `docs/backup-restore.md` — recovery procedures.
- `docs/github-copilot.md` — Copilot/BYOK integration and limitations.
- `docs/capacity-control.md` and `docs/benchmarking.md` — admission and benchmark calibration.
- `docs/project-status.md` — canonical validated engineering checkpoint.
- `AGENTS.md` — mandatory engineering resume protocol.

## External acceptance still required

Repository automation cannot replace environment validation for:

- real DGX Spark/vLLM/model benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/role setup;
- real Cloudflare hostname/tunnel routing;
- GitHub Copilot BYOK end-to-end;
- customer backup destination/encryption/retention;
- customer-specific PostgreSQL/Redis/observability HA and durable storage choices.

## License

Internal project. Licensing and external distribution terms will be defined before productization.
