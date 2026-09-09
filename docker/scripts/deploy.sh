#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
DOCKER_DIR=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)
ENV_FILE=${LLMPROXY_ENV_FILE:-/opt/llmproxy/.env}
IMAGE_TAG=${1:-${LLMPROXY_IMAGE_TAG:-main}}

if [ ! -f "$ENV_FILE" ]; then
  echo "Environment file not found: $ENV_FILE" >&2
  exit 1
fi

export LLMPROXY_IMAGE_TAG="$IMAGE_TAG"

COMPOSE="docker compose --env-file $ENV_FILE -f $DOCKER_DIR/docker-compose.yml -f $DOCKER_DIR/docker-compose.prod.yml"

$COMPOSE pull llmproxy cloudflared postgres
$COMPOSE up -d --no-build --remove-orphans

PORT=$(grep '^LLMPROXY_PORT=' "$ENV_FILE" | tail -1 | cut -d= -f2 || true)
PORT=${PORT:-8080}

attempt=0
until wget -qO- "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; do
  attempt=$((attempt + 1))
  if [ "$attempt" -ge 30 ]; then
    echo "Deployment health check failed." >&2
    $COMPOSE ps
    exit 1
  fi
  sleep 2
done

echo "LlmProxy deployed successfully with image tag: $IMAGE_TAG"
