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
  GRAFANA_ADMIN_PASSWORD; do
  require_env_value "$required"
done

# Pairing from Admin is the preferred flow for new installations.
# Never silently create an unreachable placeholder inference deployment.
BOOTSTRAP_MODE="$(read_env_value BOOTSTRAP_ENABLED | tr '[:upper:]' '[:lower:]')"
if [[ "$BOOTSTRAP_MODE" != "false" ]]; then
  require_env_value INFERENCE_NODE_BASE_ADDRESS
  require_env_value PROVIDER_MODEL_NAME
fi

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
# Explicitly select the product-owned Compose namespace. Ambient
# COMPOSE_PROJECT_NAME must never redirect this deployment to another app.
COMPOSE=(docker compose --project-name llmproxy-full --env-file "$ENV_FILE" -f "$RUNTIME_DIR/docker-compose.full.yml")
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

# A full-stack Compose pull starts many independent image downloads at once.
# On constrained/unstable links this can repeatedly reset large CDN transfers.
# Pull only LLMProxy's active services, one at a time, with bounded retries.
# Docker retains successfully downloaded layers across attempts and reruns.
PULL_MAX_ATTEMPTS="${LLMPROXY_PULL_MAX_ATTEMPTS:-8}"
if [[ ! "$PULL_MAX_ATTEMPTS" =~ ^[0-9]+$ ]] || (( 10#$PULL_MAX_ATTEMPTS < 1 || 10#$PULL_MAX_ATTEMPTS > 12 )); then
  echo "LLMPROXY_PULL_MAX_ATTEMPTS must be an integer between 1 and 12." >&2
  exit 2
fi
mapfile -t ACTIVE_SERVICES < <("${COMPOSE[@]}" config --services)
if (( ${#ACTIVE_SERVICES[@]} == 0 )); then
  echo "No active Compose services were resolved; refusing deployment." >&2
  exit 2
fi

pull_one_service() {
  local service="$1" attempt=1 delay=3
  while true; do
    deploy_log "Pulling image for service $service (attempt $attempt/$PULL_MAX_ATTEMPTS)"
    # This only limits Compose parallelism. Do not modify daemon.json or
    # restart the shared Docker daemon: other apps/tunnels must stay running.
    if COMPOSE_PARALLEL_LIMIT=1 "${COMPOSE[@]}" pull "$service"; then
      return 0
    fi
    if (( attempt >= PULL_MAX_ATTEMPTS )); then
      echo "Image download failed for $service after $PULL_MAX_ATTEMPTS attempts." >&2
      echo "Check network/DNS/firewall/CDN access to the Docker registry; cached layers are preserved." >&2
      return 1
    fi
    deploy_log "Image download interrupted for $service; retrying in ${delay}s (cached layers will be reused)"
    sleep "$delay"
    attempt=$((attempt + 1))
    delay=$((delay * 2))
    (( delay > 30 )) && delay=30
  done
}

deploy_log "Downloading ${#ACTIVE_SERVICES[@]} service images sequentially; existing Docker services are untouched"
for service in "${ACTIVE_SERVICES[@]}"; do
  pull_one_service "$service"
done

deploy_log "Starting/updating containers"
# Do not --remove-orphans: an unrelated tunnel in a reused Compose namespace
# must never be stopped or removed as a side effect of deploying LLMProxy.
"${COMPOSE[@]}" up -d --no-build
deploy_log "Containers started; waiting for gateway health"

if [[ -z "$CLOUDFLARE_TOKEN" ]]; then
  # If a previous deployment enabled the profile and the token was deliberately removed,
  # make sure the public tunnel does not stay running.
  # Only our explicitly named Compose service; never touch host cloudflared
  # or any other project's tunnel, service, restart policy, or container.
  "${COMPOSE[@]}" --profile cloudflare rm -sf cloudflared >/dev/null 2>&1 || true
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
