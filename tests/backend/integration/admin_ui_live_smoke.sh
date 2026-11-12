#!/usr/bin/env bash
# Admin UI against the real gateway: Compose stack (LlmProxy + PostgreSQL) + Playwright `live` project.
# Needs Docker, Node 22+ and a Chrome/Chromium for Playwright (PLAYWRIGHT_USE_SYSTEM_CHROME=1 uses system Chrome).
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

PORT="${LLMPROXY_LIVE_PORT:-18080}"
COMPOSE=(docker compose -p llmproxy-admin-live -f docker/docker-compose.yml)

cleanup() { "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

export LLMPROXY_PORT="$PORT"
export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-admin-live-pepper"
export LLMPROXY_UPSTREAM_CREDENTIAL_KEY="00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"

"${COMPOSE[@]}" up -d --build

for _ in {1..60}; do
  if curl --fail --silent "http://127.0.0.1:${PORT}/readyz" >/dev/null; then ready=1; break; fi
  sleep 2
done
if [[ "${ready:-0}" != "1" ]]; then
  echo "Gateway did not become ready." >&2
  "${COMPOSE[@]}" logs --no-color >&2 || true
  exit 1
fi

cd tests/frontend
npm install --no-audit --no-fund
LLMPROXY_LIVE_BASE_URL="http://127.0.0.1:${PORT}" npm run test:live
