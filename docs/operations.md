# Operations: Linux deployment, environment acceptance, DGX health, safe maintenance, build identity and audit

## Current product baseline

```text
version                 0.2.0-preview.7
implementation          df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                      35592623906 SUCCESS
Full stack              35592624282 SUCCESS
Publish GHCR            35593081824 SUCCESS
immutable image alias   sha-df3ecf7
image digest            sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

For controlled production use, prefer immutable `sha-df3ecf7` over mutable `main` until an explicit exact SemVer tag/release is created.

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
  --image-tag sha-df3ecf7
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
  bash docker/scripts/deploy.sh sha-df3ecf7
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

Per-key request/output-token governance remains available. Aggregate Entra-user request-count quotas are also supported across all personal keys with atomic user+credential admission. Aggregate user output-token budgets and monetary budgets remain out of scope until their reservation/pricing semantics are explicitly defined.

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

Validated `preview.7` registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version               0.2.0-preview.7
source                df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
validating CI         35592623906
Full Stack            35592624282
Publish GHCR          35593081824
image alias           sha-df3ecf7
digest                sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest  sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
release artifact      10635322261
artifact digest       sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

No exact `v0.2.0-preview.7` Git tag or GitHub Release has been created.

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
