#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE_FILE="${COMPOSE_FILE:-docker/docker-compose.full.yml}"
ENV_FILE="${ENV_FILE:-}"
REDIS_KEY_PREFIX="${REDIS_KEY_PREFIX:-llmproxy}"
RESTORE_START_GATEWAY="${RESTORE_START_GATEWAY:-true}"
BACKUP_FILE="${1:-}"
CONFIRM="${2:-}"

if [[ -z "$BACKUP_FILE" || "$CONFIRM" != "--confirm-destructive" ]]; then
  echo "Usage: COMPOSE_FILE=<compose.yml> [ENV_FILE=<env>] $0 <backup.dump> --confirm-destructive" >&2
  echo "Restore replaces the target PostgreSQL database. All gateway writers must be stopped." >&2
  exit 2
fi

if [[ ! -f "$BACKUP_FILE" ]]; then
  echo "Backup file not found: $BACKUP_FILE" >&2
  exit 2
fi

if [[ ! -f "$COMPOSE_FILE" ]]; then
  echo "Compose file not found: $COMPOSE_FILE" >&2
  exit 2
fi

if [[ -f "${BACKUP_FILE}.sha256" ]]; then
  expected="$(awk 'NR==1 {print $1}' "${BACKUP_FILE}.sha256")"
  actual="$(sha256sum "$BACKUP_FILE" | awk '{print $1}')"
  if [[ -z "$expected" || "$expected" != "$actual" ]]; then
    echo "Backup checksum verification failed." >&2
    exit 3
  fi
fi

COMPOSE=(docker compose)
if [[ -n "$ENV_FILE" ]]; then
  if [[ ! -f "$ENV_FILE" ]]; then
    echo "Environment file not found: $ENV_FILE" >&2
    exit 2
  fi
  COMPOSE+=(--env-file "$ENV_FILE")
fi
COMPOSE+=(-f "$COMPOSE_FILE")

if ! "${COMPOSE[@]}" ps --status running --services | grep -qx postgres; then
  echo "The postgres Compose service must be running before restore." >&2
  exit 4
fi

# Refuse an unreadable/corrupt archive before touching the target database.
"${COMPOSE[@]}" exec -T postgres pg_restore --list < "$BACKUP_FILE" >/dev/null

# Stop the Compose-managed gateway when present. Multi-replica/external writers must
# also be stopped by the operator before invoking this script.
if "${COMPOSE[@]}" config --services | grep -qx llmproxy; then
  "${COMPOSE[@]}" stop llmproxy >/dev/null 2>&1 || true
fi

# PostgreSQL is the durable source of truth. Recreate the target DB so a restore
# cannot accidentally merge old/new rows. psql variables quote identifiers safely.
"${COMPOSE[@]}" exec -T postgres sh -eu -c '
  psql -v ON_ERROR_STOP=1 \
    -U "$POSTGRES_USER" \
    -d postgres \
    --set=db="$POSTGRES_DB" \
    --set=owner="$POSTGRES_USER"
' <<'SQL'
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE datname = :'db' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS :"db";
CREATE DATABASE :"db" OWNER :"owner";
SQL

"${COMPOSE[@]}" exec -T postgres sh -eu -c '
  exec pg_restore \
    -U "$POSTGRES_USER" \
    -d "$POSTGRES_DB" \
    --no-owner \
    --no-acl \
    --exit-on-error
' < "$BACKUP_FILE"

# If this Compose shape owns Redis, discard only LlmProxy-prefixed runtime keys.
# Runtime snapshots/counters/leases must be rebuilt from the restored PostgreSQL
# authority instead of being mixed with newer pre-restore Redis state.
if "${COMPOSE[@]}" config --services | grep -qx redis && \
   "${COMPOSE[@]}" ps --status running --services | grep -qx redis; then
  "${COMPOSE[@]}" exec -T -e LLM_RESTORE_PREFIX="$REDIS_KEY_PREFIX" redis sh -eu -c '
    redis-cli -a "$REDIS_PASSWORD" --scan --pattern "${LLM_RESTORE_PREFIX}:*" 2>/dev/null |
    while IFS= read -r key; do
      [ -n "$key" ] || continue
      redis-cli -a "$REDIS_PASSWORD" UNLINK "$key" >/dev/null 2>&1
    done
  '
fi

if [[ "$RESTORE_START_GATEWAY" == "true" ]] && "${COMPOSE[@]}" config --services | grep -qx llmproxy; then
  "${COMPOSE[@]}" up -d llmproxy
fi

echo "Restore completed from: $BACKUP_FILE"
echo "Important: Authentication__ApiKeyPepper and all external deployment secrets must match the backed-up environment."
