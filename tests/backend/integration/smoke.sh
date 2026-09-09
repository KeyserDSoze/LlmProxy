#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail_with_diagnostics() {
  local message="$1"
  echo "$message" >&2
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color >&2 || true
  exit 1
}

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start."
fi

ready=false
for attempt in {1..30}; do
  if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done

if [[ "$ready" != "true" ]]; then
  fail_with_diagnostics "Gateway did not become ready."
fi

curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet '"status":"ok"'

unauthorized_models_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/v1/models)"
if [[ "$unauthorized_models_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/models without bearer token to return 401, got ${unauthorized_models_status}."
fi

unauthorized_responses_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello"}' http://127.0.0.1:8080/v1/responses)"
if [[ "$unauthorized_responses_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/responses without bearer token to return 401, got ${unauthorized_responses_status}."
fi

curl --fail --silent \
  -H 'Authorization: Bearer dev-change-me' \
  http://127.0.0.1:8080/v1/models \
  | grep --quiet 'agic-code-fast'

curl --fail --silent http://127.0.0.1:8080/api/admin/overview | grep --quiet 'activeRequests'

echo "Backend integration smoke suite passed."
