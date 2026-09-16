# Linux production deployment

This is the canonical operator runbook for deploying LlmProxy on a Linux host.

The supported production path is the Redis-enabled full stack. The smaller Compose files remain useful for development and local smoke tests, but production deployment and GitHub Actions use the topology documented here.

## 1. Target topology

```text
GitHub Copilot / OpenAI-compatible clients
                  |
          optional Cloudflare Tunnel
                  |
+-------------------------------------------------------+
| Linux production host                                 |
|                                                       |
|  LlmProxy                                             |
|  PostgreSQL        durable configuration/history      |
|  Redis             shared runtime coordination        |
|  OTEL Collector                                      |
|  Prometheus / Tempo / Loki / Grafana                 |
+-------------------------+-----------------------------+
                          |
                          | private LAN
                          v
                    DGX Spark / vLLM
```

The DGX nodes do not run LlmProxy. They expose OpenAI-compatible vLLM service roots reachable from the Linux host.

## 2. Host prerequisites

Recommended baseline:

- supported 64-bit Linux distribution;
- Docker Engine;
- Docker Compose v2;
- `git`;
- `curl` or `wget` for health checks;
- outbound HTTPS to GitHub/GHCR and, when used, Cloudflare;
- private-network reachability from the host to every DGX/vLLM service root.

Verify Docker before continuing:

```bash
docker version
docker compose version
```

Create the operator-owned deployment directories. The user that runs deployment must be able to write these directories and use Docker:

```bash
sudo install -d -m 0750 -o "$USER" -g "$USER" /opt/llmproxy
install -d -m 0750 /opt/llmproxy/runtime /opt/llmproxy/backups
```

The production layout is:

```text
/opt/llmproxy/
  .env                  operator-owned secrets/configuration
  runtime/              Compose + observability assets staged by deploy.sh
  backups/              PostgreSQL backup destination example
```

`docker/scripts/deploy.sh` deliberately copies runtime assets out of the Git checkout before starting containers. A self-hosted runner workspace can therefore be cleaned without becoming the long-lived bind-mount source for the running stack.

## 3. Obtain the repository for manual deployment

For a manual first deployment, clone the repository anywhere convenient, for example:

```bash
git clone git@github.com:KeyserDSoze/LlmProxy.git
cd LlmProxy
```

A self-hosted GitHub Actions deployment does not require this permanent clone; the workflow checks out the repository and stages the required runtime assets automatically.

## 4. Authenticate to GHCR

The image is private unless package visibility is changed. Authenticate Docker with a token/user that can read the package:

```bash
echo "$GITHUB_TOKEN" | docker login ghcr.io -u "$GITHUB_USER" --password-stdin
```

For GitHub Actions deployment, `.github/workflows/deploy.yml` performs the registry login with the workflow token.

## 5. Create production configuration

Start from the dedicated production template:

```bash
cp docker/.env.production.example /opt/llmproxy/.env
chmod 600 /opt/llmproxy/.env
```

Edit it:

```bash
nano /opt/llmproxy/.env
```

At minimum replace:

```env
POSTGRES_PASSWORD=...
REDIS_PASSWORD=...
LLM_PROXY_API_KEY=...
LLM_PROXY_API_KEY_PEPPER=...
GRAFANA_ADMIN_PASSWORD=...
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
PROVIDER_MODEL_NAME=<exact-vllm-model-id>
```

Use long random values. `LLM_PROXY_API_KEY_PEPPER` is a recovery dependency: losing or changing it invalidates existing persisted credential hashes. Store it separately from the VM in the production secret/recovery system.

The default production template keeps:

```env
ASPNETCORE_ENVIRONMENT=Production
ENTRA_ENABLED=false
CLOUDFLARE_TUNNEL_TOKEN=
GRAFANA_BIND_ADDRESS=127.0.0.1
```

This is intentional for the first private-LAN acceptance. Do not enable public Cloudflare exposure while Entra administration is disabled; the deploy script refuses that combination.

## 6. Check DGX/vLLM connectivity before deployment

From the Linux host, verify the configured service root. For a root such as `http://10.0.0.21:8000`:

```bash
curl -f http://10.0.0.21:8000/health
curl -f http://10.0.0.21:8000/v1/models
```

Path-prefixed service roots are supported, for example:

```text
http://10.0.0.21:8000/vllm
```

LlmProxy will derive `/health`, `/v1/models`, `/v1/chat/completions` and `/v1/responses` from the complete service root.

## 7. Choose the image to deploy

For controlled production changes prefer an immutable source alias or an exact SemVer release when one exists:

```text
sha-abcdef1
0.2.0-preview.N
```

`main` is supported for acceptance environments but is mutable.

The container publication workflow verifies CI provenance, digest, SPDX SBOM and SLSA provenance before a published build is considered valid.

## 8. First private-LAN deployment

From the repository checkout:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh main
```

The script performs this sequence:

```text
validate required production configuration
  -> stage docker-compose.full.yml + observability configs under /opt/llmproxy/runtime
  -> docker compose config
  -> pull images
  -> docker compose up -d
  -> wait for /healthz
  -> wait for /readyz
```

The deployed stack includes PostgreSQL, Redis and the observability bundle. Cloudflare is not started while `CLOUDFLARE_TUNNEL_TOKEN` is empty.

Check status:

```bash
docker compose \
  --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml \
  ps
```

Check the gateway locally:

```bash
curl -f http://127.0.0.1:8080/healthz
curl -f http://127.0.0.1:8080/readyz
```

With the default bind address the gateway is also available on the host LAN address at port 8080. Restrict that port with host/network firewall rules appropriate to the environment.

Grafana is loopback-only by default in the production template:

```text
http://127.0.0.1:3000
```

Use SSH port forwarding or a separately protected reverse-proxy path if remote operator access is needed.

## 9. Validate the application

Open the Admin UI on the private network:

```text
http://<linux-host>:8080/admin/
```

Validate:

1. the initial DGX node exists;
2. `Test connection` succeeds;
3. `/health` and `/v1/models` are reachable through the configured service root;
4. the logical model points to the intended provider model;
5. a gateway inference credential can call `/v1/models` and inference;
6. Grafana receives metrics/traces/logs;
7. `GET /api/admin/runtime-sync` shows a healthy Redis/outbox state.

Do not calibrate production concurrency from defaults. Apply real Capacity Profiles only after benchmark evidence from the intended model/DGX combination.

## 10. Enable Entra before public administration

Configure the production Entra application/roles, then update `/opt/llmproxy/.env`:

```env
ENTRA_ENABLED=true
ENTRA_TENANT_ID=<tenant-id>
ENTRA_CLIENT_ID=<client-id>
ENTRA_CLIENT_SECRET=<client-secret>
```

Redeploy and validate Admin authentication privately before adding a public tunnel:

```bash
bash docker/scripts/deploy.sh <same-or-new-image-tag>
```

The supported roles are documented elsewhere in the repository as `LlmProxy.Admin` and `LlmProxy.Reader`.

## 11. Enable Cloudflare Tunnel

Create/configure the Cloudflare Tunnel externally and route the intended public hostname to the Compose service origin:

```text
http://llmproxy:8080
```

Then set only the tunnel token in the production environment:

```env
CLOUDFLARE_TUNNEL_TOKEN=<token>
```

On the next deployment the script detects the token and automatically enables the `cloudflare` Compose profile:

```bash
bash docker/scripts/deploy.sh <image-tag>
```

The script refuses to enable the public profile unless `ENTRA_ENABLED=true` and the required Entra settings are populated.

Whether the tunnel publishes only inference paths, Admin paths, or separate hostnames is a customer/tenant routing decision. Preserve bearer authentication on inference and Entra protection on administration.

## 12. GitHub Actions production deployment

The repository contains `.github/workflows/deploy.yml` as the supported automated deployment path.

Install a dedicated GitHub Actions runner on the Linux host and give it these labels:

```text
self-hosted
linux
x64
llmproxy-prod
```

The runner account must:

- be allowed to use Docker;
- be able to write `/opt/llmproxy/runtime`;
- be able to read `/opt/llmproxy/.env`;
- have outbound access to GitHub/GHCR.

Keep `/opt/llmproxy/.env` on the host. Do not copy production secrets into the repository or workflow YAML.

The workflow is manually triggered and accepts `image_tag`. It serializes production deployments, logs into GHCR, runs the same `docker/scripts/deploy.sh` used manually, and therefore exercises the same full-stack preflight and health/readiness checks.

For a controlled deployment select an already published `sha-<7>` tag or an exact SemVer tag rather than `main`.

## 13. Logs and diagnostics

Status:

```bash
docker compose --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml ps
```

Gateway/Redis/collector logs:

```bash
docker compose --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml \
  logs -f llmproxy redis otel-collector
```

If Cloudflare is enabled:

```bash
docker compose --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml \
  --profile cloudflare logs -f cloudflared
```

## 14. PostgreSQL backup

PostgreSQL is the durable recovery authority. Redis is rebuildable runtime state.

From a repository checkout, create a backup using the production runtime Compose file:

```bash
ENV_FILE=/opt/llmproxy/.env \
COMPOSE_FILE=/opt/llmproxy/runtime/docker-compose.full.yml \
  bash docker/scripts/postgres-backup.sh \
  /opt/llmproxy/backups/llmproxy-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Preserve together:

- the PostgreSQL dump + checksum + metadata;
- `LLM_PROXY_API_KEY_PEPPER`;
- Entra/Cloudflare/deployment secrets in the external secret manager.

Read `docs/backup-restore.md` before a restore.

## 15. Update

Publish/choose a validated image first, then deploy its tag:

```bash
bash docker/scripts/deploy.sh sha-abcdef1
```

The script pulls the requested image, recreates services as needed, applies EF migrations during application startup and waits for liveness/readiness.

Take a PostgreSQL backup before any migration that is not known to be backward compatible.

## 16. Rollback

Application rollback is another deployment using the previous known-good image tag:

```bash
bash docker/scripts/deploy.sh sha-previous
```

A container rollback does not reverse destructive database migrations. Any future destructive migration requires an explicit compatibility/restore plan before deployment.

## 17. Stop and destructive reset

Stop while preserving volumes:

```bash
docker compose --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml down
```

Do not use `down -v` in production unless permanent deletion of PostgreSQL, Redis and observability volumes is explicitly intended and a tested backup exists.

## 18. Production acceptance still external

Repository CI validates Compose rendering, deployment preflight, image identity, PostgreSQL/Redis behavior and integration smokes. The following remain environment-specific acceptance activities:

- real DGX Spark/vLLM/model benchmark calibration;
- representative multi-DGX coding load;
- real Entra application and role assignment;
- real Cloudflare hostname/tunnel routing;
- GitHub Copilot BYOK end-to-end;
- self-hosted runner permissions/reboot behavior;
- customer backup destination, encryption and retention policy;
- customer-specific PostgreSQL/Redis/observability HA and durable object-storage choices.
