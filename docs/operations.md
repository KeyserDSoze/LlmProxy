# Operations: Linux deployment, environment acceptance, DGX health, safe maintenance, build identity and audit

## Current product baseline

```text
version                 0.2.0-preview.6
implementation          7da5682f043eeb7e0d0b684eabb0ab6a6b659b35
CI                      35569885810 SUCCESS
Full stack              35569885843 SUCCESS
Publish GHCR            35570238079 SUCCESS
immutable image alias   sha-7da5682
image digest            sha256:c28c60e004496ae0d3949f616b36cf4ba67ed523f8567218e904bd80730f5a81
attestation manifest    sha256:1530fd684b90668743974ce3d00f8cdd49ca4116d8126619f9e768648e42642a
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10625781303
artifact digest         sha256:6ac1a5ebdceaae1e77108a1631f83ac993b997fd01d1fbc6a163f7b4cb7593b7
```

For controlled production use, prefer immutable `sha-7da5682` over mutable `main` until an explicit exact SemVer tag/release is created.

Operators can inspect runtime identity through:

```http
GET /healthz
GET /api/admin/product
```

and Admin release notes at `/admin/releases`.

## Linux production installation

The canonical production runbook is `docs/linux-production-deployment.md`.

Preferred first-install command from a complete repository checkout:

```bash
export GHCR_USER='<github-user>'
export GHCR_TOKEN='<package-read-token>'

sudo -E bash docker/scripts/install-linux.sh \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-vllm-model-id>' \
  --image-tag sha-7da5682
```

The installer detects `/etc/os-release` and common package managers. Docker's official repository path is implemented for Debian, Ubuntu, Fedora, CentOS and RHEL. Common derivative/other hosts may use native `apt`, `dnf`/`yum`, `zypper`, `pacman` or `apk` packages and a Compose v2 CLI-plugin fallback. Unknown hosts can preinstall Docker Engine + Compose v2 and rerun with `--skip-docker-install`.

Do not describe this as a guarantee that every Linux derivative/version has been package-tested. Target-host installation remains an external acceptance step.

The installer preserves a working Docker/Compose installation and existing `/opt/llmproxy/.env`. New hosts receive generated PostgreSQL/Redis/API-key/pepper/Grafana secrets; values are not printed.

Production host contract:

```text
/opt/llmproxy/.env
/opt/llmproxy/runtime/
/opt/llmproxy/backups/
/opt/llmproxy/acceptance/
```

Preserve `LLM_PROXY_API_KEY_PEPPER` outside the host as a recovery dependency.

## Production deploy/update

Manual and GitHub Actions deployment use the same implementation:

```text
docker/scripts/deploy.sh
```

Example:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh sha-7da5682
```

The deploy script rejects unresolved production settings, requires `ASPNETCORE_ENVIRONMENT=Production`, requires Entra before public Cloudflare exposure, stages assets under `/opt/llmproxy/runtime`, validates Compose before container changes, starts the Redis-enabled full stack and requires both `/healthz` and `/readyz`.

GitHub Actions deployment uses a self-hosted Linux runner labelled:

```text
self-hosted
linux
x64
llmproxy-prod
```

Keep production secrets in `/opt/llmproxy/.env`, not workflow YAML.

## Entra user self-service

Production Entra roles are `LlmProxy.Admin`, `LlmProxy.User` and `LlmProxy.Reader`. Normal users open `/admin/me`; the self-service API is under `/api/me`. Personal keys are permanently attributed to Entra `tid+oid`; shared/unattended integrations may continue using administrator-created service credentials.

Per-key request/output-token governance remains available. Aggregated per-user and monetary budgets are not enabled until their precedence and pricing semantics are explicitly defined.

See `docs/identity-api-keys.md`.

## Production environment acceptance

The canonical manual acceptance is:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

Focused runbook:

```text
docs/environment-acceptance.md
```

Default evidence:

```text
/opt/llmproxy/acceptance/<UTC timestamp>/summary.md
/opt/llmproxy/acceptance/<UTC timestamp>/checks.tsv
```

The harness validates:

1. Linux distribution/kernel/architecture metadata;
2. Docker Engine + Docker Compose v2;
3. direct VM -> DGX/vLLM `/health` and `/v1/models`;
4. direct vLLM Chat + Responses, streaming and non-streaming;
5. LlmProxy `/healthz`, `/readyz`, `/v1/models`;
6. gateway Chat + Responses, streaming and non-streaming;
7. exact provider-model and public logical-model visibility.

Canonical vLLM `/health` is treated as an HTTP-status-only probe. A healthy vLLM server may return `200` with an empty body and no JSON content type.

The evidence bundle is metadata-only. Do not persist request bodies, prompts, source, generated output, response bodies, bearer tokens or API secrets. The script performs a final secret scan before success.

### Self-hosted Actions acceptance

Once the production runner exists, launch manually through:

```text
.github/workflows/environment-acceptance.yml
```

The workflow:

- runs on `self-hosted, linux, x64, llmproxy-prod`;
- uses GitHub `environment: production`;
- accepts no API-key workflow inputs;
- reads `/opt/llmproxy/.env` through the root-owned acceptance process;
- requires non-interactive `sudo` for the dedicated runner;
- uploads only `summary.md` and `checks.tsv`;
- rejects unexpected files before artifact upload;
- keeps the Actions artifact for 14 days;
- uploads metadata evidence even when functional acceptance fails, then preserves the run failure;
- removes runner-local evidence after the run.

If the DGX/vLLM endpoint itself requires bearer authentication, use the manual path with `LLMPROXY_ACCEPTANCE_DGX_API_KEY` in the acceptance process environment until an approved host-secret injection mechanism is configured.

The workflow being present in the repository does not prove the production environment. A green real run requires the actual self-hosted runner, target Linux host and DGX/vLLM service.

## Health state model

Defaults:

```text
HEALTH_INTERVAL_SECONDS=10
HEALTH_HEALTHY_AFTER_SUCCESSES=2
HEALTH_UNHEALTHY_AFTER_FAILURES=3
```

Administrative `Draining` and `Disabled` states are not overwritten by background health probes. `Healthy`, `Degraded` and `Unknown` may remain routing-eligible subject to capacity; `Unhealthy`, `Draining` and `Disabled` are excluded.

## Safe model/runtime maintenance

Supported workflow:

```http
GET  /api/admin/nodes/{nodeId}/maintenance
POST /api/admin/nodes/{nodeId}/maintenance/drain
POST /api/admin/nodes/{nodeId}/maintenance/resume
```

Drain establishes the new-admission block before committing `Draining`; existing streams finish. In Redis mode the maintenance marker participates in atomic capacity admission across replicas.

Resume requires zero distributed active work and validates:

1. `GET <service-root>/health`;
2. `GET <service-root>/v1/models`;
3. one non-streaming Chat Completions warm-up with one output token for each enabled provider model on the node.

The legacy direct `POST /api/admin/nodes/{nodeId}/drain` remains deprecated and must not become a maintenance bypass.

## Manual DGX connection test

Admin `Test` calls:

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

Service roots may include host, port and path prefix:

```text
http://localhost:3450/primopath
http://10.0.0.25:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

The manual Admin test is diagnostic; production environment acceptance and maintenance resume have stricter, separate semantics.

## Verify a published image

Production images carry source/version/build identity. Before GHCR login, every publication queries GitHub Actions for successful `CI` from a push to `main` on the exact source SHA. Exact tags must also match compiled version metadata.

Validated `preview.6` registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version               0.2.0-preview.6
source                7da5682f043eeb7e0d0b684eabb0ab6a6b659b35
validating CI         35569885810
Full Stack            35569885843
Publish GHCR          35570238079
image alias           sha-7da5682
digest                sha256:c28c60e004496ae0d3949f616b36cf4ba67ed523f8567218e904bd80730f5a81
attestation manifest  sha256:1530fd684b90668743974ce3d00f8cdd49ca4116d8126619f9e768648e42642a
release artifact      10625781303
artifact digest       sha256:6ac1a5ebdceaae1e77108a1631f83ac993b997fd01d1fbc6a163f7b4cb7593b7
```

No exact `v0.2.0-preview.6` Git tag or GitHub Release has been created.

## Usage retention operational note

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
```

Retention compacts complete expired UTC days before deleting raw rows. Reporting merges rollups with newer raw metrics. Pending runtime outbox rows are never retention-deleted.

## PostgreSQL backup

PostgreSQL is durable recovery authority; Redis is rebuildable.

```bash
ENV_FILE=/opt/llmproxy/.env \
COMPOSE_FILE=/opt/llmproxy/runtime/docker-compose.full.yml \
  bash docker/scripts/postgres-backup.sh \
  /opt/llmproxy/backups/llmproxy-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Preserve the DB dump/checksum/metadata plus the API-key pepper and deployment secrets in the appropriate external recovery system. Read `docs/backup-restore.md` before restore.

## Administrative audit trail

Administrative changes are stored in `audit_events` and available through:

```http
GET /api/admin/audit?take=100
```

Audit must never contain raw inference API secrets, Entra client secrets, Cloudflare tokens, prompts, source code, generated code or model responses.

## Validation boundary

Repository CI can validate scripts, Compose rendering, mocks and regression behavior. It cannot prove:

- package/service behavior on the actual target Linux distribution;
- VM/DGX network policy;
- real vLLM/model behavior and production capacity;
- real Entra roles;
- real Cloudflare/public DNS;
- GitHub Copilot BYOK end-to-end;
- self-hosted runner permissions/reboot behavior;
- customer HA/storage/backup policy.

Those become environment acceptance evidence on the real installation.
