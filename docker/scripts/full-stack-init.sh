#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="$ROOT_DIR/docker/.env.full"
EXAMPLE_FILE="$ROOT_DIR/docker/.env.full.example"
COMPOSE_FILE="$ROOT_DIR/docker/docker-compose.full.yml"
START=false

if [[ "${1:-}" == "--start" ]]; then
  START=true
elif [[ -n "${1:-}" ]]; then
  echo "Usage: $0 [--start]" >&2
  exit 2
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required. Install Docker Engine/Desktop and Compose v2 first." >&2
  exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required." >&2
  exit 1
fi

if [[ ! -f "$ENV_FILE" ]]; then
  cp "$EXAMPLE_FILE" "$ENV_FILE"
fi

hex_secret() {
  local bytes="$1"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$bytes"
  else
    head -c "$bytes" /dev/urandom | od -An -tx1 | tr -d ' \n'
  fi
}

replace_once() {
  local placeholder="$1"
  local value="$2"
  local tmp="${ENV_FILE}.tmp"
  sed "s|${placeholder}|${value}|g" "$ENV_FILE" > "$tmp"
  mv "$tmp" "$ENV_FILE"
}

replace_once CHANGE_ME_POSTGRES_PASSWORD "$(hex_secret 24)"
replace_once CHANGE_ME_REDIS_PASSWORD "$(hex_secret 24)"
replace_once CHANGE_ME_LLM_PROXY_API_KEY "llmp_$(hex_secret 24)"
replace_once CHANGE_ME_LLM_PROXY_API_KEY_PEPPER "$(hex_secret 32)"
replace_once CHANGE_ME_UPSTREAM_CREDENTIAL_KEY "$(hex_secret 32)"
replace_once CHANGE_ME_GRAFANA_ADMIN_PASSWORD "$(hex_secret 20)"

chmod 600 "$ENV_FILE" 2>/dev/null || true

echo "Full-stack environment prepared: docker/.env.full"
echo "Internal PostgreSQL, Redis and OpenTelemetry service URLs are injected automatically by Compose."
echo "Before startup, edit only operator-specific values such as INFERENCE_NODE_BASE_ADDRESS and PROVIDER_MODEL_NAME."
echo "If GHCR is private, authenticate first with: docker login ghcr.io"

if [[ "$START" == "true" ]]; then
  echo "Starting LlmProxy full stack..."
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" pull
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d
  echo "LlmProxy: http://localhost:8080/admin/ (or LLMPROXY_PORT)"
  echo "Grafana:  http://localhost:3000 (or GRAFANA_PORT)"
fi
