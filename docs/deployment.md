# Deployment

## Target VM

The first production topology uses a Linux VM with Docker Engine and Docker Compose v2. The VM must be able to reach the DGX nodes over the private network and reach GitHub/GHCR/Cloudflare over outbound HTTPS.

Suggested filesystem layout:

```text
/opt/llmproxy/
  .env
  backups/
```

Docker owns application/container state while PostgreSQL data lives in a named persistent volume.

## GitHub Actions runner

Install a dedicated self-hosted GitHub Actions runner on the VM and assign the labels used by `.github/workflows/deploy.yml`, initially `self-hosted`, `linux`, `x64`, `llmproxy-prod`.

The deployment workflow is manual until the production VM is ready. Once stabilized it can be triggered automatically after successful container publication.

## Production `.env`

Create `/opt/llmproxy/.env` with production values. Do not store this file in Git. Start from `docker/.env.example`.

The API-key pepper is part of credential validation and must be backed up securely. Losing or changing it invalidates existing stored API-key hashes.

## Database migrations

LlmProxy uses EF Core migrations. On application startup, pending migrations are applied before bootstrap data is evaluated and before the application begins serving traffic.

This makes a normal container replacement sufficient for schema updates. Migration changes must be reviewed carefully: production migrations should remain compatible with the previous application version whenever rollback of the application image is expected.

PostgreSQL backups are mandatory before destructive schema migrations. The initial release only contains additive/bootstrap schema creation.

## Deploy sequence

```text
GitHub Actions
  -> build/test
  -> build immutable image
  -> push GHCR
  -> self-hosted production job
       -> docker login GHCR
       -> docker compose pull
       -> docker compose up -d
       -> application applies pending EF migrations
       -> /healthz check
```

## Rollback

Deployments use an explicit image tag. Rollback means redeploying the previous known-good tag. A previous image cannot necessarily reverse a destructive database migration, therefore destructive migrations require an explicit compatibility and restore plan.
