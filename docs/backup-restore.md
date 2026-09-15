# Backup and restore

PostgreSQL is the durable application authority for LlmProxy. This document defines the repository-supported backup/restore path and the boundaries that are intentionally outside the database artifact.

## What is backed up

The supported application-state backup is a PostgreSQL custom-format archive created with `pg_dump -Fc`.

It includes durable application schema/data such as:

- nodes, logical models and deployments;
- routing/routing-tuning policy;
- API credential HMACs and safe metadata;
- Usage Groups and credential membership;
- request-rate/output-token policy definitions;
- audit history and request metrics still inside retention;
- transactional runtime-state outbox rows, including pending work;
- EF migration history.

Raw API secrets are not stored in PostgreSQL and therefore cannot leak into or be reconstructed from the database backup.

## External secrets are a separate recovery dependency

The database archive is **not sufficient by itself** to recover authentication. The deployment secret material must be preserved through the organization's secret manager/backups, especially:

```text
Authentication__ApiKeyPepper
PostgreSQL credentials
Redis credentials
Entra client secret, if used
TLS/tunnel/domain secrets and configuration
```

`Authentication__ApiKeyPepper` is critical. Stored credentials are HMACs; restoring the database with a different pepper makes existing client secrets fail authentication even though the credential rows are intact.

Do not put these secrets into the database dump metadata file or Git.

## What is not treated as authoritative backup state

Redis is not the durable configuration authority. Redis runtime snapshots are rebuilt from PostgreSQL after restore.

Current Redis-only state includes transient/fixed-window coordination such as:

- request-rate counters;
- output-token current-window usage/reservations;
- physical-capacity leases;
- runtime snapshot/version/pubsub state.

A disaster restore therefore starts with clean LlmProxy Redis keys. Current request/token windows reset as part of DR. If a customer needs exact preservation of in-flight quota windows across disaster recovery, that is a separate requirement and must be designed explicitly; copying arbitrary Redis state from a different point in time is not safe.

Tempo/Loki/Prometheus/Grafana storage is also outside this application-state backup. Production observability retention/DR should follow the chosen production storage backend.

## Create a backup

Default full-stack deployment:

```bash
bash docker/scripts/postgres-backup.sh
```

Explicit file:

```bash
bash docker/scripts/postgres-backup.sh backups/llmproxy-prod.dump
```

Custom compose/env file:

```bash
COMPOSE_FILE=docker/docker-compose.full.yml \
ENV_FILE=docker/.env.full \
bash docker/scripts/postgres-backup.sh backups/llmproxy-prod.dump
```

The script requires the `postgres` Compose service to be running and writes:

```text
<file>.dump          PostgreSQL custom-format archive
<file>.dump.sha256   SHA-256 checksum
<file>.dump.meta     non-secret metadata + pepper recovery warning
```

Before publishing the artifact, the script asks `pg_restore --list` to parse it. Empty/unreadable archives are rejected.

Store the dump/checksum in access-controlled backup storage and store deployment secrets separately in the approved secret manager.

## Restore

Restore is intentionally destructive and requires an explicit confirmation flag:

```bash
bash docker/scripts/postgres-restore.sh backups/llmproxy-prod.dump --confirm-destructive
```

With explicit deployment files:

```bash
COMPOSE_FILE=docker/docker-compose.full.yml \
ENV_FILE=docker/.env.full \
REDIS_KEY_PREFIX=llmproxy \
bash docker/scripts/postgres-restore.sh backups/llmproxy-prod.dump --confirm-destructive
```

The script:

1. verifies SHA-256 when the checksum sidecar exists;
2. validates that `pg_restore` can read the archive before changing the DB;
3. stops the Compose-managed LlmProxy gateway;
4. terminates remaining sessions to the target database;
5. drops and recreates the target database instead of merging rows;
6. restores with `--no-owner --no-acl --exit-on-error`;
7. when a Redis service belongs to the same Compose shape, removes only keys matching `${REDIS_KEY_PREFIX}:*`;
8. restarts the Compose-managed gateway unless `RESTORE_START_GATEWAY=false`.

### Multi-replica requirement

All external gateway replicas/writers that use the same PostgreSQL database must be stopped before restore. The script can stop only the `llmproxy` service that belongs to the selected Compose project.

Do not run a point-in-time restore while other replicas continue mutating the same DB.

## Post-restore verification

A successful `pg_restore` command is not enough. Verify at minimum:

1. `/readyz` becomes healthy;
2. expected node/model/deployment catalog exists;
3. a pre-backup API credential still authenticates using the **same pepper**;
4. Usage Group membership is preserved;
5. request-rate and output-token policy definitions are preserved;
6. audit/history row counts are plausible for the selected backup point;
7. with Redis enabled, credential/route/policy snapshots are republished into clean Redis;
8. an authenticated inference request reaches the expected logical model/backend.

Pending transactional-outbox rows restored from PostgreSQL remain durable work and may be replayed after Redis/runtime recovery. Pending rows must not be deleted simply to make restore look clean.

## Automated restore proof

CI runs `tests/backend/integration/backup_restore_smoke.sh` against the same Linux operator scripts.

The smoke:

- creates non-bootstrap Usage Group, credential and request/token policy state;
- performs authenticated inference;
- creates a custom-format backup + checksum;
- destroys the PostgreSQL volume completely;
- starts a clean empty PostgreSQL target and proves the application schema is absent;
- runs the destructive restore script;
- boots the gateway from restored data and proves the preserved credential still authenticates;
- verifies Usage Group, policy and audit/history preservation;
- attaches a completely clean Redis + peer gateway;
- proves startup republishes credential/route/policy snapshots from restored PostgreSQL;
- performs authenticated inference through that Redis-enabled restored peer.

This test is the minimum evidence required before backup/restore is considered validated.

## Recovery caveats

- Keep PostgreSQL server/restore tooling version-compatible with the backup archive. The bundled scripts execute `pg_dump`/`pg_restore` inside the deployment's PostgreSQL image to reduce version mismatch risk.
- The restored application image may run newer EF migrations on startup. Test upgrades separately before relying on cross-version disaster recovery.
- A database backup does not recover a raw API secret that an operator/client lost. It recovers the HMAC that lets the *existing* secret continue to work when the same pepper is restored.
- Rotation after the backup means a restore to that older backup also restores the credential version that existed at that backup point. Coordinate client secret rollback/rotation as part of DR if restoring to an older point in time.
