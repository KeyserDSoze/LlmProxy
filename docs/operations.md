# Operations: Linux deployment, DGX health, safe maintenance, build identity and audit

## Current product baseline

```text
version        0.2.0-preview.4
source         58a80a60c2f3a049b279be6bf9583ffa4c1cc088
CI             35095161900 SUCCESS
Full Stack     35088765577 SUCCESS
Publish GHCR   35095620725 SUCCESS
image digest   sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
```

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
  --image-tag main
```

For controlled production updates prefer a validated immutable `sha-<7>` tag over mutable `main`.

The installer detects `/etc/os-release` and common package managers. Docker's official repository path is implemented for Debian, Ubuntu, Fedora, CentOS and RHEL. Common derivative/other hosts may use native `apt`, `dnf`/`yum`, `zypper`, `pacman` or `apk` Docker packages and a Docker Compose v2 CLI-plugin fallback. A host with an unrecognized package manager must preinstall Docker Engine + Compose v2 and rerun with `--skip-docker-install`.

Do not describe this as a guarantee that every Linux derivative/version has been package-tested. Target-host installation remains an external acceptance step.

The installer preserves an existing working Docker/Compose installation and existing `/opt/llmproxy/.env`. New hosts receive generated PostgreSQL/Redis/API-key/pepper/Grafana secrets; the values are not printed.

Production host contract:

```text
/opt/llmproxy/.env
/opt/llmproxy/runtime/
/opt/llmproxy/backups/
```

Preserve `LLM_PROXY_API_KEY_PEPPER` outside the host as a recovery dependency.

Installer modes:

```bash
bash docker/scripts/install-linux.sh --help
bash docker/scripts/install-linux.sh --validate-only
sudo -E bash docker/scripts/install-linux.sh --prepare-only ...
sudo -E bash docker/scripts/install-linux.sh --skip-docker-install ...
```

`--skip-dgx-check` is for deliberate staged provisioning only; normal first deployment should validate DGX `/health` and `/v1/models`.

## Production deploy/update

Manual and GitHub Actions deployment use the same implementation:

```text
docker/scripts/deploy.sh
```

Example:

```bash
LLMPROXY_DEPLOY_DIR=/opt/llmproxy \
LLMPROXY_ENV_FILE=/opt/llmproxy/.env \
  bash docker/scripts/deploy.sh sha-abcdef1
```

The deploy script:

1. rejects missing/unresolved production settings;
2. requires `ASPNETCORE_ENVIRONMENT=Production`;
3. requires Entra settings before enabling a public Cloudflare profile;
4. stages full-stack Compose and observability assets to `/opt/llmproxy/runtime`;
5. validates `docker compose config` before container changes;
6. pulls/starts PostgreSQL, Redis, observability and LlmProxy;
7. enables the Cloudflare profile automatically when a tunnel token is present;
8. requires `/healthz` and `/readyz` before reporting success.

The production template keeps Grafana loopback-only by default. Do not expose Admin publicly until Entra authentication/roles are configured and validated.

GitHub Actions production deployment uses a dedicated self-hosted Linux runner labelled:

```text
self-hosted
linux
x64
llmproxy-prod
```

Keep production secrets in `/opt/llmproxy/.env`, not workflow YAML.

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

### Begin drain

`POST .../maintenance/drain` establishes the new-admission block before committing the node to `Draining` and auditing `node.maintenance.drain`. In Redis mode the maintenance marker is checked inside atomic capacity admission, so a peer with stale local route state cannot admit new work. Existing work is allowed to finish.

Typical outcomes:

```text
202 drain started / existing work may remain
409 node_disabled
503 maintenance_coordination_unavailable
```

### Observe drain

Wait for:

```text
nodeStatus = Draining
admissionBlocked = true
activeRequests = 0
drained = true
```

Do not restart/replace vLLM before drain completion unless interruption is explicitly accepted. LlmProxy never fails over an already-started downstream stream.

### Perform external upgrade

Once drained, perform the DGX/vLLM/model/driver/container operation outside LlmProxy. The gateway owns traffic safety and validation around the operation; it does not execute host upgrades itself.

### Resume with validation

`POST .../maintenance/resume` requires zero distributed active work, then checks:

1. `GET <service-root>/health`;
2. `GET <service-root>/v1/models`;
3. one non-streaming Chat Completions warm-up with one output token for each enabled provider model on the node.

Only successful validation returns the node to `Healthy` and clears the maintenance block.

Failure responses include:

```text
409 node_not_draining
409 node_still_draining
503 maintenance_coordination_unavailable
503 node_validation_failed
```

The legacy direct endpoint `POST /api/admin/nodes/{nodeId}/drain` is deprecated and must not be restored as a maintenance bypass.

Current full-stack proof including maintenance is `35088765577 SUCCESS`.

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

The manual test is diagnostic; background health and maintenance resume have their own semantics.

## Verify a published image

Production images carry:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
```

Publishing rules:

```text
validated main SHA      -> main + sha-<7>
validated matching tag  -> exact SemVer + sha-<7>
stable Git tag          -> may also publish major.minor alias
prerelease Git tag      -> never updates stable-looking alias
```

Before GHCR login, every publication queries GitHub Actions for successful `CI` from a push to `main` on the exact source SHA. Exact tags also have to match compiled version metadata.

Validated `preview.4` registry example:

```text
image          ghcr.io/keyserdsoze/llmproxy
version        0.2.0-preview.4
source         58a80a60c2f3a049b279be6bf9583ffa4c1cc088
validating CI  35095161900
digest         sha256:12f6e615d3b5460247c9f0aec7081c8b98b1bf4264d86e30cbe890ad7bcfb40a
```

Post-push verification reads the OCI index/attestation manifests back from GHCR and requires:

```text
attestation manifest  sha256:cd92f248e73e58fca570a687ca0002d10cfc8e5b308e60ce31351454b4933b0b
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
```

Release manifest artifact:

```text
artifact id      10445034650
artifact digest  sha256:cb2bf6b6d8d34a545c080b866866d7098cedbab66f66f475aa168caf6a93c977
```

No exact `v0.2.0-preview.4` Git tag or GitHub Release has been created.

## Usage retention operational note

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
```

Retention compacts complete expired UTC days before deleting raw rows. Reporting merges rollups with newer raw metrics. Pending runtime outbox rows are never retention-deleted.

Read `docs/data-retention.md` before changing retention/reporting behavior.

## PostgreSQL backup

PostgreSQL is durable recovery authority; Redis is rebuildable.

Example on the production host:

```bash
ENV_FILE=/opt/llmproxy/.env \
COMPOSE_FILE=/opt/llmproxy/runtime/docker-compose.full.yml \
  bash docker/scripts/postgres-backup.sh \
  /opt/llmproxy/backups/llmproxy-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Preserve the DB dump/checksum/metadata plus the API-key pepper and deployment secrets in the appropriate external secret/recovery system. Read `docs/backup-restore.md` before restore.

## Administrative audit trail

Administrative changes are stored in `audit_events`:

```http
GET /api/admin/audit?take=100
```

Representative audited actions include routing/node/model/deployment changes, connection tests, maintenance drain/resume, credential creation/rotation/revoke, governance changes and manual retention cleanup.

With Entra enabled the actor comes from the authenticated principal. Development mode without Entra records the local administrator identity.

Audit must never contain raw inference API secrets, Entra client secrets, Cloudflare tokens, prompts, source code, generated code or model responses.

## Integration-test behavior

Final `preview.4` CI `35095161900` covers installer validation, production private/public deploy rendering, product/backend/frontend tests, image identity, PostgreSQL/runtime smokes, governance, retention and restore. Full Stack `35088765577` separately proves Redis/OTEL wiring, outbox recovery, shared token budgets, cross-replica rotation and safe maintenance. Publish `35095620725` proves pre-GHCR source-CI validation plus immutable digest/SPDX/SLSA registry verification.
