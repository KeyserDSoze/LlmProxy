#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"

"${COMPOSE[@]}" up -d --build

ready=false
for attempt in {1..30}; do
  if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done

if [[ "$ready" != "true" ]]; then
  echo "Gateway did not become ready." >&2
  "${COMPOSE[@]}" logs --no-color >&2
  exit 1
fi

curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet '"status":"ok"'

unauthorized_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/v1/models)"
if [[ "$unauthorized_status" != "401" ]]; then
  echo "Expected /v1/models without bearer token to return 401, got ${unauthorized_status}." >&2
  exit 1
fi

curl --fail --silent \
  -H 'Authorization: Bearer dev-change-me' \
  http://127.0.0.1:8080/v1/models \
  | grep --quiet 'agic-code-fast'

curl --fail --silent http://127.0.0.1:8080/api/admin/overview | grep --quiet 'activeRequests'

echo "Backend integration smoke suite passed."
