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

Create `/opt/llmproxy/.env` with production values. Do not store this file in Git.

At minimum configure database credentials, the bootstrap inference API key, Entra values, DGX bootstrap endpoint and Cloudflare Tunnel token.

## Deploy sequence

```text
GitHub Actions
  -> build/test
  -> build immutable image
  -> push GHCR
  -> self-hosted production job
       -> docker login GHCR
       -> docker compose pull
       -> docker compose up -d --no-build
       -> /healthz check
```

## Rollback

Deployments use an explicit image tag. Rollback means redeploying the previous known-good tag; database schema changes must remain backward compatible across at least one deploy step or have an explicit migration rollback plan.
