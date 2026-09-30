#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_DOCKER_DIR="$ROOT_DIR/docker"
DEPLOY_DIR="${LLMPROXY_DEPLOY_DIR:-/opt/llmproxy}"
ENV_FILE="${LLMPROXY_ENV_FILE:-$DEPLOY_DIR/.env}"
RUNTIME_DIR="$DEPLOY_DIR/runtime"
IMAGE_TAG="${1:-${LLMPROXY_IMAGE_TAG:-main}}"
VALIDATE_ONLY="${LLMPROXY_DEPLOY_VALIDATE_ONLY:-false}"

deploy_log() {
  printf '%s [llmproxy-deploy] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

deploy_log "Validating deployment prerequisites for image tag $IMAGE_TAG"

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker Engine is required on the deployment host." >&2
  exit 1
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required on the deployment host." >&2
  exit 1
fi
if [[ ! -f "$ENV_FILE" ]]; then
  echo "Production environment file not found: $ENV_FILE" >&2
  echo "Start from docker/.env.production.example and keep the resulting file outside Git." >&2
  exit 1
fi

read_env_value() {
  local key="$1"
  sed -n "s/^${key}=//p" "$ENV_FILE" | tail -n 1 | tr -d '\r'
}

require_env_value() {
  local key="$1"
  local value
  value="$(read_env_value "$key")"
  if [[ -z "$value" || "$value" == CHANGE_ME* ]]; then
    echo "Production setting $key is missing or still uses a CHANGE_ME placeholder in $ENV_FILE." >&2
    exit 2
  fi
}

for required in \
  POSTGRES_PASSWORD \
  REDIS_PASSWORD \
  LLM_PROXY_API_KEY \
  LLM_PROXY_API_KEY_PEPPER \
  LLMPROXY_UPSTREAM_CREDENTIAL_KEY \
  GRAFANA_ADMIN_PASSWORD \
  DGX_NODE_BASE_ADDRESS \
  PROVIDER_MODEL_NAME; do
  require_env_value "$required"
done

ASPNET_ENV="$(read_env_value ASPNETCORE_ENVIRONMENT)"
if [[ "$ASPNET_ENV" != "Production" ]]; then
  echo "ASPNETCORE_ENVIRONMENT must be Production for the supported production deployment path." >&2
  exit 2
fi

ENTRA_ENABLED_VALUE="$(read_env_value ENTRA_ENABLED | tr '[:upper:]' '[:lower:]')"
if [[ "$ASPNET_ENV" == "Production" && "$ENTRA_ENABLED_VALUE" != "true" ]]; then
  echo "Production requires ENTRA_ENABLED=true. Use the Development full-stack profile for private bootstrap/acceptance without Entra." >&2
  exit 2
fi
if [[ "$ENTRA_ENABLED_VALUE" == "true" ]]; then
  require_env_value ENTRA_TENANT_ID
  require_env_value ENTRA_CLIENT_ID
  require_env_value ENTRA_CLIENT_SECRET
fi

CLOUDFLARE_TOKEN="$(read_env_value CLOUDFLARE_TUNNEL_TOKEN)"
if [[ "$CLOUDFLARE_TOKEN" == CHANGE_ME* ]]; then
  echo "CLOUDFLARE_TUNNEL_TOKEN still uses a CHANGE_ME placeholder." >&2
  exit 2
fi
if [[ -n "$CLOUDFLARE_TOKEN" && "$ENTRA_ENABLED_VALUE" != "true" ]]; then
  echo "Refusing public Cloudflare deployment while ENTRA_ENABLED is not true." >&2
  echo "Validate Entra administration first, then configure CLOUDFLARE_TUNNEL_TOKEN." >&2
  exit 2
fi

mkdir -p "$RUNTIME_DIR/observability"
install -m 0644 "$SOURCE_DOCKER_DIR/docker-compose.full.yml" "$RUNTIME_DIR/docker-compose.full.yml"
cp -a "$SOURCE_DOCKER_DIR/observability/." "$RUNTIME_DIR/observability/"

export LLMPROXY_IMAGE_TAG="$IMAGE_TAG"
COMPOSE=(docker compose --env-file "$ENV_FILE" -f "$RUNTIME_DIR/docker-compose.full.yml")
if [[ -n "$CLOUDFLARE_TOKEN" ]]; then
  COMPOSE+=(--profile cloudflare)
fi

# Fail before touching running containers if interpolation, required settings, profiles,
# bind mounts or Compose syntax are invalid.
deploy_log "Validating Docker Compose configuration"
"${COMPOSE[@]}" config >/dev/null
deploy_log "Docker Compose configuration: OK"

if [[ "$VALIDATE_ONLY" == "true" ]]; then
  echo "Production deployment configuration validated successfully."
  echo "Runtime assets staged under: $RUNTIME_DIR"
  exit 0
fi

deploy_log "Pulling immutable container image(s)"
"${COMPOSE[@]}" pull

deploy_log "Starting/updating containers"
"${COMPOSE[@]}" up -d --no-build --remove-orphans
deploy_log "Containers started; waiting for gateway health"

if [[ -z "$CLOUDFLARE_TOKEN" ]]; then
  # If a previous deployment enabled the profile and the token was deliberately removed,
  # make sure the public tunnel does not stay running.
  docker compose --env-file "$ENV_FILE" -f "$RUNTIME_DIR/docker-compose.full.yml" --profile cloudflare rm -sf cloudflared >/dev/null 2>&1 || true
fi

PORT="$(read_env_value LLMPROXY_PORT)"
PORT="${PORT:-8080}"

probe() {
  local url="$1"
  if command -v curl >/dev/null 2>&1; then
    curl --fail --silent --show-error --max-time 5 "$url" >/dev/null
  elif command -v wget >/dev/null 2>&1; then
    wget -qO- --timeout=5 "$url" >/dev/null
  else
    echo "Either curl or wget is required for deployment health checks." >&2
    return 1
  fi
}

wait_for_endpoint() {
  local name="$1"
  local url="$2"
  local attempt=0
  until probe "$url"; do
    attempt=$((attempt + 1))
    if (( attempt == 1 || attempt % 5 == 0 )); then
      deploy_log "Waiting for $name ($attempt/30): $url"
    fi
    if [[ "$attempt" -ge 30 ]]; then
      echo "$name check failed: $url" >&2
      "${COMPOSE[@]}" ps >&2 || true
      "${COMPOSE[@]}" logs --tail=100 llmproxy >&2 || true
      exit 1
    fi
    sleep 2
  done
}

wait_for_endpoint "Liveness" "http://127.0.0.1:$PORT/healthz"
deploy_log "Liveness: OK"
wait_for_endpoint "Readiness" "http://127.0.0.1:$PORT/readyz"
deploy_log "Readiness: OK"

printf 'LlmProxy production deployment succeeded.\n'
printf 'Image tag: %s\n' "$IMAGE_TAG"
printf 'Environment: %s\n' "$ENV_FILE"
printf 'Runtime assets: %s\n' "$RUNTIME_DIR"
if [[ -n "$CLOUDFLARE_TOKEN" ]]; then
  printf 'Cloudflare profile: enabled\n'
else
  printf 'Cloudflare profile: disabled (private/LAN deployment)\n'
fi
