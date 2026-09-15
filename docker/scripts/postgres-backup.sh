#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE_FILE="${COMPOSE_FILE:-docker/docker-compose.full.yml}"
ENV_FILE="${ENV_FILE:-}"
BACKUP_FILE="${1:-backups/llmproxy-$(date -u +%Y%m%dT%H%M%SZ).dump}"

if [[ ! -f "$COMPOSE_FILE" ]]; then
  echo "Compose file not found: $COMPOSE_FILE" >&2
  exit 2
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
  echo "The postgres Compose service must be running before backup." >&2
  exit 3
fi

mkdir -p "$(dirname "$BACKUP_FILE")"
tmp_file="${BACKUP_FILE}.tmp.$$"
trap 'rm -f "$tmp_file"' EXIT

# PostgreSQL is the durable application authority. The dump intentionally contains
# schema + all durable rows, but raw API secrets are not present in the database.
"${COMPOSE[@]}" exec -T postgres sh -eu -c '
  exec pg_dump \
    -U "$POSTGRES_USER" \
    -d "$POSTGRES_DB" \
    --format=custom \
    --compress=6 \
    --no-owner \
    --no-acl
' > "$tmp_file"

if [[ ! -s "$tmp_file" ]]; then
  echo "Backup produced an empty file." >&2
  exit 4
fi

# Validate that pg_restore can read the archive before publishing it as a backup.
"${COMPOSE[@]}" exec -T postgres pg_restore --list < "$tmp_file" >/dev/null

mv "$tmp_file" "$BACKUP_FILE"
chmod 600 "$BACKUP_FILE" 2>/dev/null || true
checksum="$(sha256sum "$BACKUP_FILE" | awk '{print $1}')"
printf '%s  %s\n' "$checksum" "$(basename "$BACKUP_FILE")" > "${BACKUP_FILE}.sha256"
chmod 600 "${BACKUP_FILE}.sha256" 2>/dev/null || true

cat > "${BACKUP_FILE}.meta" <<EOF
created_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
format=postgres-custom
compose_file=$COMPOSE_FILE
note=Authentication__ApiKeyPepper and other deployment secrets are external to this database backup and must be preserved separately in the secret manager.
EOF
chmod 600 "${BACKUP_FILE}.meta" 2>/dev/null || true

echo "Backup created: $BACKUP_FILE"
echo "Checksum: ${BACKUP_FILE}.sha256"
echo "Metadata: ${BACKUP_FILE}.meta"
