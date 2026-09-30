# Linux production deployment

This is the canonical operator runbook for deploying LlmProxy on a Linux host.

The supported production path is the Redis-enabled full stack. The smaller Compose files remain useful for development/local smoke tests, but production deployment and GitHub Actions use the topology documented here.

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

## 2. Preferred path: install a new Linux host automatically

The repository contains:

```text
docker/scripts/install-linux.sh
```

It is the preferred first-install path. Run it from a complete repository checkout.

### What it installs/prepares

```text
host prerequisites
Docker Engine
Docker Compose v2
/opt/llmproxy
production secrets/config
full-stack runtime assets
PostgreSQL
Redis
OTEL Collector
Prometheus
Tempo
Loki
Grafana
LlmProxy
optional Cloudflare Tunnel
```

The script is idempotent around the important host-owned state:

- an existing Docker installation is preserved when Engine + Compose v2 already work;
- an existing `/opt/llmproxy/.env` is preserved rather than regenerated;
- PostgreSQL/Redis/observability named volumes are not deleted;
- runtime Compose/config assets are refreshed by the same `deploy.sh` used by GitHub Actions.

### Distribution support

The installer detects `/etc/os-release` and the available package manager.

Docker official repository path:

```text
Debian
Ubuntu
Fedora
CentOS
RHEL
```

Derivative/other Linux path:

```text
apt
DNF / YUM
zypper
pacman
apk
```

For non-official Docker platforms the script uses the distribution's Docker package and, when `docker compose` is missing, installs the Docker Compose CLI plugin under `/usr/local/lib/docker/cli-plugins`.

This makes the installer usable across the common Debian/Ubuntu, RHEL/Fedora, SUSE, Arch and Alpine families without pretending that Docker itself officially validates every derivative. If the host has no recognized package manager, pre-install Docker Engine + Compose v2 and rerun with `--skip-docker-install`.

The current automatic Compose fallback defaults to the version pinned in the installer (`v5.5.0` at this product baseline); override `DOCKER_COMPOSE_VERSION` only when intentionally validating a different version.

### First command on a new host

Install `git` if the image is so minimal that it is not already available, then obtain the private repository using your normal GitHub credentials. For example:

```bash
git clone git@github.com:KeyserDSoze/LlmProxy.git
cd LlmProxy
```

Then run the installer. For a private GHCR package, pass registry credentials through environment variables; the token is used for `docker login` and is not written to `.env`:

```bash
export GHCR_USER='<github-user>'
export GHCR_TOKEN='<token-with-package-read-access>'

sudo -E bash docker/scripts/install-linux.sh \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>' \
  --image-tag main
```

For acceptance `main` is convenient. For controlled production changes prefer a validated immutable `sha-<7>` tag or an exact SemVer image once an exact release exists.

### What happens during that command

```text
detect distro + architecture
  -> install curl/git/openssl/etc. if needed
  -> install or validate Docker Engine
  -> install or validate Docker Compose v2
  -> enable/start Docker daemon when the init system supports it
  -> add invoking sudo user to docker group when available
  -> create /opt/llmproxy/{runtime,backups}
  -> create /opt/llmproxy/.env when absent
       -> random PostgreSQL password
       -> random Redis password
       -> random initial LlmProxy API credential
       -> random stable API-key HMAC pepper
       -> random Grafana admin password
  -> apply GHCR/image/DGX/model inputs
  -> optional docker login to GHCR
  -> call DGX /health
  -> call DGX /v1/models
  -> invoke docker/scripts/deploy.sh
       -> stage runtime assets
       -> docker compose config
       -> pull
       -> up -d
       -> /healthz
       -> /readyz
```

Generated secret values are not printed. They live in the root/operator-protected `/opt/llmproxy/.env`. Preserve `LLM_PROXY_API_KEY_PEPPER` in an external recovery/secret system before treating the host as production.

If the installer adds your account to the `docker` group, open a new login/session before expecting direct `docker` commands to work without `sudo`.

### Installer modes

Host/config preparation without starting containers:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --prepare-only \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>'
```

Require preinstalled Docker:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --skip-docker-install \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>'
```

Skip DGX precheck only for deliberate staged provisioning:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --skip-dgx-check \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>'
```

Do not use `--skip-dgx-check` as a normal production shortcut.

Non-interactive provisioning:

```bash
sudo -E bash docker/scripts/install-linux.sh \
  --non-interactive \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>' \
  --image-tag sha-abcdef1
```

No-change compatibility/syntax check:

```bash
bash docker/scripts/install-linux.sh --validate-only
```

Full help:

```bash
bash docker/scripts/install-linux.sh --help
```

## 3. Production filesystem contract

```text
/opt/llmproxy/
  .env                  operator-owned secrets/configuration
  runtime/              Compose + observability assets staged by deploy.sh
  backups/              PostgreSQL backup destination example
```

`docker/scripts/deploy.sh` deliberately copies runtime assets out of the Git checkout before starting containers. A self-hosted runner workspace can therefore be cleaned without becoming the long-lived bind-mount source for the running stack.

## 4. Manual host preparation (fallback / controlled installations)

If host policy forbids package installation by the repository script, install these separately:

- Docker Engine;
- Docker Compose v2;
- `git`;
- `curl` or `wget`;
- `openssl` for local secret generation when needed;
- outbound HTTPS to GitHub/GHCR and, when used, Cloudflare;
- private-network reachability to every DGX/vLLM service root.

Verify:

```bash
docker version
docker compose version
```

Create the operator-owned deployment directories:

```bash
sudo install -d -m 0750 -o "$USER" -g "$USER" /opt/llmproxy
install -d -m 0750 /opt/llmproxy/runtime /opt/llmproxy/backups
```

## 5. Authenticate to GHCR manually

If the image package is private:

```bash
echo "$GITHUB_TOKEN" | docker login ghcr.io -u "$GITHUB_USER" --password-stdin
```

For GitHub Actions deployment, `.github/workflows/deploy.yml` performs the registry login with the workflow token.

## 6. Create production configuration manually

If you did not use the installer:

```bash
cp docker/.env.production.example /opt/llmproxy/.env
chmod 600 /opt/llmproxy/.env
```

Edit every required `CHANGE_ME` value:

```env
POSTGRES_PASSWORD=...
REDIS_PASSWORD=...
LLM_PROXY_API_KEY=...
LLM_PROXY_API_KEY_PEPPER=...
GRAFANA_ADMIN_PASSWORD=...
LLMPROXY_UPSTREAM_CREDENTIAL_KEY=<stable-32-byte-hex-key>
DGX_NODE_BASE_ADDRESS=http://10.0.0.21:8000
PROVIDER_MODEL_NAME=<exact-vllm-model-id>
```

`DGX_NODE_BASE_ADDRESS` is deliberately a `CHANGE_ME` placeholder in the production template. This prevents a copied template from accidentally passing deployment validation against an example IP.

The production template is intentionally incomplete until identity is configured. The application itself refuses `Production` startup without Entra, and `deploy.sh` now rejects that state before touching containers. Set:

```env
ASPNETCORE_ENVIRONMENT=Production
ENTRA_ENABLED=true
ENTRA_TENANT_ID=<tenant-id>
ENTRA_CLIENT_ID=<client-id>
ENTRA_CLIENT_SECRET=<client-secret>
CLOUDFLARE_TUNNEL_TOKEN=
GRAFANA_BIND_ADDRESS=127.0.0.1
```

For private no-Entra acceptance before the real tenant is available, use the Development/full-stack acceptance path rather than representing that host as production.

## 7. DGX/vLLM connectivity

From the Linux host verify the complete service root before deployment:

```bash
curl -f http://10.0.0.21:8000/health
curl -f http://10.0.0.21:8000/v1/models
```

Path-prefixed roots are supported:

```text
http://10.0.0.21:8000/vllm
```

LlmProxy derives `/health`, `/v1/models`, `/v1/chat/completions` and `/v1/responses` from that complete root.

### Same-host llama.cpp / DGX Spark

When llama.cpp runs on the same Linux host as Docker, do not leave it bound only to `127.0.0.1`. Bind it to the Docker bridge gateway so the LlmProxy container can reach it without exposing the runtime on all LAN interfaces:

```bash
DOCKER_HOST_GATEWAY="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')"
llama-server --host "$DOCKER_HOST_GATEWAY" --port 8080 --api-key llama-local ...
```

Configure `DGX_NODE_BASE_ADDRESS=http://host.docker.internal:8080`. For first installation, pass `DGX_UPSTREAM_BEARER_TOKEN=llama-local` in the installer environment; it is encrypted into the node and removed from the long-lived container environment after bootstrap.

## 8. Manual private-LAN deployment / later updates

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh main
```

The script performs:

```text
validate required production configuration
  -> stage docker-compose.full.yml + observability configs under /opt/llmproxy/runtime
  -> docker compose config
  -> pull images
  -> docker compose up -d
  -> wait for /healthz
  -> wait for /readyz
```

Check status:

```bash
docker compose \
  --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml \
  ps
```

Check locally:

```bash
curl -f http://127.0.0.1:8080/healthz
curl -f http://127.0.0.1:8080/readyz
```

Grafana is loopback-only by default:

```text
http://127.0.0.1:3000
```

Use SSH port forwarding or a separately protected reverse proxy for remote operator access.

## 9. Application acceptance on the LAN

Open:

```text
http://<linux-host>:8080/admin/
```

Validate:

1. initial DGX node exists;
2. `Test connection` succeeds;
3. `/health` and `/v1/models` are reachable;
4. logical model points to the intended provider model;
5. an inference credential can call `/v1/models` and inference;
6. Grafana receives metrics/traces/logs;
7. `GET /api/admin/runtime-sync` reports healthy Redis/outbox state.

Do not calibrate production concurrency from defaults. Apply Capacity Profiles only after benchmark evidence from the intended model/DGX combination.

## 10. Enable Entra before public administration

Update `/opt/llmproxy/.env`:

```env
ENTRA_ENABLED=true
ENTRA_TENANT_ID=<tenant-id>
ENTRA_CLIENT_ID=<client-id>
ENTRA_CLIENT_SECRET=<client-secret>
```

Redeploy and validate Admin authentication privately:

```bash
bash docker/scripts/deploy.sh <same-or-new-image-tag>
```

Supported roles are `LlmProxy.Admin`, `LlmProxy.User` and `LlmProxy.Reader`. `LlmProxy.User` is the normal inference-consumer role and uses `/admin/me` for personal API-key self-service; `LlmProxy.Reader` remains read-only operational access.

The installer can also write these values on first provisioning when they are supplied as environment variables through `sudo -E`.

## 11. Enable Cloudflare Tunnel

Create/configure the tunnel externally and route the intended hostname to the Compose origin:

```text
http://llmproxy:8080
```

Then set:

```env
CLOUDFLARE_TUNNEL_TOKEN=<token>
```

On the next deployment the `cloudflare` Compose profile is enabled automatically. Deployment refuses a non-empty Cloudflare token unless Entra is enabled and its required settings are populated.

Whether the tunnel publishes inference only, Admin paths, or separate hostnames is a customer/tenant routing decision. Preserve bearer authentication on inference and Entra protection on administration.

## 12. GitHub Actions production deployment

`.github/workflows/deploy.yml` is the supported automated update/deployment path after the host exists.

Install a dedicated GitHub Actions runner on the Linux host with labels:

```text
self-hosted
linux
x64
llmproxy-prod
```

The runner account must:

- use Docker;
- write `/opt/llmproxy/runtime`;
- read `/opt/llmproxy/.env`;
- access GitHub/GHCR outbound.

Keep `/opt/llmproxy/.env` on the host. Do not copy production secrets into repository/workflow YAML.

The workflow is manually triggered, accepts `image_tag`, serializes production deployment, logs into GHCR and runs the same `docker/scripts/deploy.sh` as manual operations.

The host installer adds the invoking sudo user to the Docker group when the group exists. A newly installed self-hosted runner should run under the intended deployment account after that account has started a fresh login/session.

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

PostgreSQL is durable recovery authority; Redis is rebuildable runtime state.

```bash
ENV_FILE=/opt/llmproxy/.env \
COMPOSE_FILE=/opt/llmproxy/runtime/docker-compose.full.yml \
  bash docker/scripts/postgres-backup.sh \
  /opt/llmproxy/backups/llmproxy-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Preserve together:

- PostgreSQL dump + checksum + metadata;
- `LLM_PROXY_API_KEY_PEPPER`;
- `LLMPROXY_UPSTREAM_CREDENTIAL_KEY`;
- Entra/Cloudflare/deployment secrets in the external secret manager.

Read `docs/backup-restore.md` before restore.

## 15. Update

Select a validated image and deploy its tag:

```bash
bash docker/scripts/deploy.sh sha-abcdef1
```

The script pulls the requested image, recreates services as needed, applies EF migrations during startup and requires liveness/readiness.

Take a PostgreSQL backup before any migration that is not known to be backward compatible.

## 16. Rollback

```bash
bash docker/scripts/deploy.sh sha-previous
```

A container rollback does not reverse destructive database migrations. Any future destructive migration requires an explicit compatibility/restore plan.

## 17. Stop and destructive reset

Stop while preserving volumes:

```bash
docker compose --env-file /opt/llmproxy/.env \
  -f /opt/llmproxy/runtime/docker-compose.full.yml down
```

Never use `down -v` in production unless permanent deletion of PostgreSQL, Redis and observability volumes is explicitly intended and a tested backup exists.

## 18. Repository validation versus external acceptance

Repository CI validates:

- installer syntax/compatibility mode;
- production template placeholders;
- private/public production Compose preflight;
- full-stack Compose rendering;
- image identity;
- PostgreSQL/Redis/runtime integration;
- full-stack distributed smokes.

Environment-specific acceptance still includes:

- actual distro/version package-repository behavior on the target host;
- real DGX Spark/vLLM/model benchmark calibration;
- representative multi-DGX coding load;
- real Entra app/role assignment;
- real Cloudflare hostname/tunnel routing;
- GitHub Copilot BYOK end-to-end;
- self-hosted runner permissions/reboot behavior;
- customer backup destination/encryption/retention;
- customer-specific PostgreSQL/Redis/observability HA and durable storage.
