# LlmProxy

Enterprise OpenAI-compatible gateway for routing GitHub Copilot and other AI clients to on-premises LLMs running on NVIDIA DGX infrastructure.

> Current source candidate: `0.2.0-preview.8` (release-based Linux distribution + multi-architecture publication). The last fully validated runtime baseline remains `0.2.0-preview.7` until the candidate gates pass.

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
- Entra-owned personal API keys with self-service lifecycle and per-key usage attribution.
- Usage Groups, per-credential request-rate governance, aggregate Entra-user request quotas and output-token budgets.
- Historical PostgreSQL usage rollups beyond raw-metric retention.
- Transactional PostgreSQL -> Redis runtime-state outbox.
- Metadata-only metrics/audit/OTEL; prompts/source/generated content are excluded by default.
- PostgreSQL backup/restore operators.
- SemVer/build identity, release notes, GHCR digest evidence, SPDX SBOM and SLSA provenance.
- Executable production environment acceptance for Linux host, direct DGX/vLLM and gateway Chat/Responses/SSE surfaces.

## Repository structure

```text
src/                    product code (.NET 10 + React/TypeScript)
tests/                  unit, frontend, integration and performance tests
docker/                 images, Compose, observability and operator scripts
docs/                   architecture/deployment/operations documentation
.github/workflows/       CI, publication, deployment and environment acceptance
AGENTS.md                mandatory engineering handover entry point
CHANGELOG.md             product-visible release history
```

## Development quickstart

For the distributed Redis/observability development/demo bundle use:

```bash
bash docker/scripts/full-stack-init.sh
# edit docker/.env.full, especially DGX_NODE_BASE_ADDRESS and PROVIDER_MODEL_NAME
docker compose --env-file docker/.env.full -f docker/docker-compose.full.yml up -d
```

The generic full-stack example is intended for development/demo/acceptance setup. Production has a separate operator-owned environment file and deployment path.

## Linux production deployment

For immutable GitHub Release installation/update and the `llmproxyctl` operator command, start with:

```text
docs/release-installation.md
```

The canonical production runbook is:

```text
docs/linux-production-deployment.md
```

Production uses the Redis-enabled full stack, not the legacy minimal overlay.

### Preferred first installation

From a repository checkout on a new Linux host:

```bash
export GHCR_USER='<github-user>'
export GHCR_TOKEN='<token-with-package-read-access>'

sudo -E bash docker/scripts/install-linux.sh \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>' \
  --image-tag sha-df3ecf7
```

`docker/scripts/install-linux.sh` detects the distro/package manager, installs or preserves Docker Engine, ensures Docker Compose v2, prepares `/opt/llmproxy`, generates initial production secrets, optionally logs into GHCR, checks DGX `/health` and `/v1/models`, then invokes the canonical full-stack deployment.

Docker official repositories are used for Debian, Ubuntu, Fedora, CentOS and RHEL. Common derivative/other distributions can use `apt`, `dnf`/`yum`, `zypper`, `pacman` or `apk`; when Compose v2 is missing the installer has a CLI-plugin fallback. Existing Docker installations are preserved.

For policy-controlled hosts use `--prepare-only` or pre-install Docker and rerun with `--skip-docker-install`. `--validate-only` performs a no-change installer/repository compatibility check.

Generated passwords/API credential/pepper are not printed. The protected operator-owned configuration is stored at:

```text
/opt/llmproxy/.env
```

Back up `LLM_PROXY_API_KEY_PEPPER` separately before treating the host as production.

### Manual/redeployment path

After first host preparation, or when provisioning manually, deploy a published image with:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh sha-df3ecf7
```

For controlled production changes prefer an immutable `sha-<7>` alias or an exact SemVer tag rather than mutable `main`.

`deploy.sh` validates production settings, refuses public Cloudflare exposure until Entra is configured, stages runtime assets under `/opt/llmproxy/runtime`, validates Compose, starts the full stack and requires both `/healthz` and `/readyz`.

Cloudflare is optional. Leave `CLOUDFLARE_TUNNEL_TOKEN` blank for private-LAN bootstrap.

## Automated production deployment

`.github/workflows/deploy.yml` is the supported GitHub Actions deployment path after the host exists. It runs on a dedicated Linux self-hosted runner labelled:

```text
self-hosted
linux
x64
llmproxy-prod
```

The runner keeps production secrets in `/opt/llmproxy/.env`; secrets are not committed to Git. The workflow uses the same `docker/scripts/deploy.sh` as manual deployment.

## Production environment acceptance

`0.2.0-preview.5` adds an executable target-host acceptance harness:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

It validates:

- Linux/Docker/Compose host prerequisites;
- direct VM -> DGX/vLLM `/health`, `/v1/models`, Chat, Responses and SSE;
- LlmProxy `/healthz`, `/readyz`, `/v1/models`, Chat, Responses and SSE;
- exact provider model and logical public model visibility.

The evidence bundle contains only metadata in `summary.md` and `checks.tsv`. Request bodies, prompts, source, generated output, response bodies and bearer/API secrets are not persisted. Canonical vLLM `/health` is treated as a status-only endpoint because a healthy vLLM server may return HTTP 200 with an empty body.

Read:

```text
docs/environment-acceptance.md
```

After the `llmproxy-prod` self-hosted runner is installed, the same acceptance is manually launchable through:

```text
.github/workflows/environment-acceptance.yml
```

The workflow accepts no API-key inputs. It reads the protected host configuration, uploads only `summary.md` and `checks.tsv` as a short-lived Actions artifact, and removes the runner-local evidence afterward.

This proves connectivity and functional compatibility. It does **not** establish production concurrency; real DGX/model Capacity Profiles still require benchmark evidence.

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

## Public API

```http
GET  /v1/models
POST /v1/chat/completions
POST /v1/responses
GET  /healthz
GET  /readyz
```

Inference uses bearer credentials. Raw credential secrets are returned only at creation/rotation time and are not stored in PostgreSQL.

## Administration

The React control plane manages nodes, models, deployments, routing, credentials, governance, usage, maintenance, runtime synchronization and audit.

Production administration is designed for Entra ID with roles:

```text
LlmProxy.Admin
LlmProxy.User
LlmProxy.Reader
```

`LlmProxy.User` uses `/admin/me` to create, rotate and revoke personal API keys, inspect own usage and see read-only aggregate user request limits. Administrators configure aggregate user request quotas from Usage & Governance. `LlmProxy.Reader` remains an operational read-only role.

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

Published images include source/version/build identity. The publish workflow records the immutable image digest and verifies registry-native SPDX SBOM and SLSA/BuildKit provenance attestations.

Validated `0.2.0-preview.7` runtime checkpoint:

```text
version                0.2.0-preview.7
source                 df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                     35592623906 SUCCESS
Full Stack             35592624282 SUCCESS
Publish GHCR           35593081824 SUCCESS
image alias            sha-df3ecf7
image digest           sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest   sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
release artifact       10635322261
artifact digest        sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

No immutable `v0.2.0-preview.7` Git tag or GitHub Release has been created.

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
LLMPROXY_ACCEPTANCE_DGX_API_KEY
```

Use `docker/.env.production.example` as the manual production template; the Linux installer creates the equivalent host-owned file automatically when it does not already exist.

## Documentation map

Start with:

- `docs/release-installation.md` — immutable GitHub Release bundle, bootstrap, `llmproxyctl`, update and rollback.
- `docs/linux-production-deployment.md` — canonical zero-to-running Linux production runbook.
- `docs/environment-acceptance.md` — production host/DGX/gateway acceptance and evidence rules.
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

- actual package/repository behavior on the chosen Linux distro/version;
- real DGX Spark/vLLM/model acceptance and benchmark sweeps;
- representative multi-DGX coding load;
- real Entra app/role setup;
- real Cloudflare hostname/tunnel routing;
- GitHub Copilot BYOK end-to-end;
- self-hosted runner permissions/reboot behavior;
- customer backup destination/encryption/retention;
- customer-specific PostgreSQL/Redis/observability HA and durable storage choices.

## License

Internal project. Licensing and external distribution terms will be defined before productization.


Identity and personal API-key ownership are defined in `docs/identity-api-keys.md`.
