# Deployment

For Linux production, the canonical runbook is:

```text
docs/linux-production-deployment.md
```

The supported production topology is the Redis-enabled full stack:

```text
LlmProxy + PostgreSQL + Redis
+ OpenTelemetry Collector
+ Prometheus + Tempo + Loki + Grafana
+ optional Cloudflare Tunnel profile
```

The smaller quickstart/minimal Compose paths remain for development and local smoke testing. Production deployment and `.github/workflows/deploy.yml` use `docker/docker-compose.full.yml` through `docker/scripts/deploy.sh`.

## Production filesystem contract

```text
/opt/llmproxy/
  .env                  secrets and operator configuration
  runtime/              staged Compose + observability assets
  backups/              example PostgreSQL backup destination
```

Start from:

```text
docker/.env.production.example
```

`deploy.sh` copies the runtime Compose/configuration assets from the checked-out repository into `/opt/llmproxy/runtime` before running Docker Compose. Running services therefore do not depend on the lifetime of a GitHub Actions runner workspace.

## Deploy sequence

```text
validated/published image tag
  -> production preflight
       -> required settings present
       -> ASPNETCORE_ENVIRONMENT=Production
       -> Entra required before public Cloudflare profile
       -> docker compose config
  -> stage full-stack runtime assets
  -> pull image/services
  -> docker compose up -d
  -> EF migrations on LlmProxy startup
  -> /healthz
  -> /readyz
```

Manual deployment:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh sha-abcdef1
```

`main` is allowed for acceptance, but production changes should prefer an immutable `sha-<7>` alias or an exact SemVer tag when available.

## Cloudflare

Cloudflare is an optional Compose profile. Keep `CLOUDFLARE_TUNNEL_TOKEN` empty for private-LAN bootstrap. When the token is populated, `deploy.sh` enables the profile automatically and requires Entra administration to be enabled/configured first.

The remotely configured tunnel origin should target the Compose service, not the host port:

```text
http://llmproxy:8080
```

## GitHub Actions runner

`.github/workflows/deploy.yml` is manually triggered and runs on a dedicated Linux self-hosted runner with labels:

```text
self-hosted
linux
x64
llmproxy-prod
```

The workflow uses the same `docker/scripts/deploy.sh` path as manual operation, so production automation does not maintain a second deployment implementation.

## Database migrations and rollback

EF Core applies pending migrations during application startup. Container rollback is performed by redeploying the prior known-good image tag. A prior image cannot reverse a destructive schema migration, so any destructive migration requires a backup and explicit compatibility/restore plan first.

Read `docs/linux-production-deployment.md` for the complete host setup, DGX validation, Entra/Cloudflare enablement, backup, logs, update and rollback procedure.
