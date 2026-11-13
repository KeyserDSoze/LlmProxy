#!/usr/bin/env bash
# Browser access (CORS) against the real gateway: site-wide origins, per-key origins, preflight, enforcement.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

PORT="${LLMPROXY_CORS_PORT:-18090}"
BASE="http://127.0.0.1:${PORT}"
COMPOSE=(docker compose -p llmproxy-cors-smoke -f docker/docker-compose.yml)

cleanup() { "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; }
trap cleanup EXIT

export LLMPROXY_PORT="$PORT"
export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-cors-pepper"
export LLMPROXY_UPSTREAM_CREDENTIAL_KEY="00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"

fail() { echo "FAIL: $*" >&2; "${COMPOSE[@]}" logs --no-color --tail 80 llmproxy >&2 || true; exit 1; }

"${COMPOSE[@]}" up -d --build
for _ in {1..60}; do
  curl --fail --silent "${BASE}/readyz" >/dev/null && ready=1 && break
  sleep 2
done
[[ "${ready:-0}" == "1" ]] || fail "gateway did not become ready"

# status + headers of one request: prints "<status>\n<lowercased headers>"
call() { curl --silent --output /dev/null --dump-header - --write-out '%{http_code}\n' "$@" | tr -d '\r' | tr 'A-Z' 'a-z' | awk 'BEGIN{h=""} /^[0-9]{3}$/{code=$0; next} {h=h $0 "\n"} END{print code; printf "%s", h}'; }
status_of() { head -n1; }
header_of() { grep -i "^$1:" | head -n1 | sed 's/^[^:]*: *//'; }

admin() { curl --fail --silent -H 'Content-Type: application/json' "$@"; }

SITE="https://console.example.com"
APP="https://app.example.com"
OTHER="https://evil.example.com"

# 1. Nothing is allowed by default.
out="$(call -X OPTIONS "${BASE}/v1/models" -H "Origin: ${SITE}" -H 'Access-Control-Request-Method: GET')"
[[ "$(status_of <<<"$out")" == "403" ]] || fail "preflight must be rejected before any origin is configured"

# 2. Site-wide origin.
admin -X PUT "${BASE}/api/admin/cors-settings" \
  -d "{\"allowedOrigins\":[\"${SITE}\"],\"maxAgeSeconds\":300}" | jq -e --arg s "$SITE" '.allowedOrigins == [$s] and .maxAgeSeconds == 300' >/dev/null
out="$(call -X OPTIONS "${BASE}/v1/chat/completions" -H "Origin: ${SITE}" \
  -H 'Access-Control-Request-Method: POST' -H 'Access-Control-Request-Headers: authorization, content-type')"
[[ "$(status_of <<<"$out")" == "204" ]] || fail "site-wide preflight expected 204, got: $out"
[[ "$(header_of access-control-allow-origin <<<"$out")" == "$SITE" ]] || fail "missing allow-origin for the site-wide origin"
[[ "$(header_of access-control-max-age <<<"$out")" == "300" ]] || fail "max-age not applied"
[[ "$(header_of access-control-allow-headers <<<"$out")" == "authorization, content-type" ]] || fail "allow-headers not echoed"

# 3. Invalid settings are explained, not stored.
code="$(curl --silent --output /tmp/cors-invalid.json --write-out '%{http_code}' -X PUT "${BASE}/api/admin/cors-settings" \
  -H 'Content-Type: application/json' -d '{"allowedOrigins":["https://x.example.com/path"],"maxAgeSeconds":300}')"
[[ "$code" == "400" ]] || fail "invalid origin expected 400, got $code"
jq -e '.message | contains("not a valid origin")' /tmp/cors-invalid.json >/dev/null || fail "missing explanation"

# 4. Per-key origins.
created="$(admin -X POST "${BASE}/api/admin/api-credentials" -d "{\"name\":\"cors-web\",\"allowedOrigins\":[\"${APP}\"]}")"
KEY="$(jq -r .secret <<<"$created")"
KEY_ID="$(jq -r .id <<<"$created")"
jq -e --arg a "$APP" '.allowedOrigins == [$a]' <<<"$created" >/dev/null || fail "created key lost its origins"

out="$(call -X OPTIONS "${BASE}/v1/models" -H "Origin: ${APP}" -H 'Access-Control-Request-Method: GET')"
[[ "$(status_of <<<"$out")" == "204" ]] || fail "preflight for a key origin expected 204"

out="$(call "${BASE}/v1/models" -H "Authorization: Bearer ${KEY}" -H "Origin: ${APP}")"
[[ "$(status_of <<<"$out")" == "200" ]] || fail "key from its own origin expected 200, got: $out"
[[ "$(header_of access-control-allow-origin <<<"$out")" == "$APP" ]] || fail "response lacks allow-origin for the key origin"

out="$(call "${BASE}/v1/models" -H "Authorization: Bearer ${KEY}" -H "Origin: ${SITE}")"
[[ "$(status_of <<<"$out")" == "200" ]] || fail "site-wide origin must also work for a restricted key"

out="$(call "${BASE}/v1/models" -H "Authorization: Bearer ${KEY}" -H "Origin: ${OTHER}")"
[[ "$(status_of <<<"$out")" == "403" ]] || fail "restricted key from another origin expected 403, got: $out"
[[ -z "$(header_of access-control-allow-origin <<<"$out")" ]] || fail "a rejected origin must not receive allow-origin"

out="$(call "${BASE}/v1/models" -H "Authorization: Bearer ${KEY}")"
[[ "$(status_of <<<"$out")" == "200" ]] || fail "server-side call without Origin expected 200"

# 5. A wrong key still returns a readable 401 to an origin some key lists.
out="$(call "${BASE}/v1/models" -H 'Authorization: Bearer lp_org_wrong' -H "Origin: ${APP}")"
[[ "$(status_of <<<"$out")" == "401" ]] || fail "wrong key expected 401"
[[ "$(header_of access-control-allow-origin <<<"$out")" == "$APP" ]] || fail "401 must be readable from a listed origin"

# 6. Editing and revoking take effect.
admin -X PUT "${BASE}/api/admin/api-credentials/${KEY_ID}/allowed-origins" -d "{\"allowedOrigins\":[\"${OTHER}\"]}" \
  | jq -e --arg o "$OTHER" '.allowedOrigins == [$o]' >/dev/null
out="$(call "${BASE}/v1/models" -H "Authorization: Bearer ${KEY}" -H "Origin: ${APP}")"
[[ "$(status_of <<<"$out")" == "403" ]] || fail "old origin must stop working after the edit"

curl --fail --silent -X POST "${BASE}/api/admin/api-credentials/${KEY_ID}/revoke" >/dev/null
out="$(call -X OPTIONS "${BASE}/v1/models" -H "Origin: ${OTHER}" -H 'Access-Control-Request-Method: GET')"
[[ "$(status_of <<<"$out")" == "403" ]] || fail "a revoked key must not keep opening preflights"

# 7. Wildcard and malformed origins.
admin -X PUT "${BASE}/api/admin/cors-settings" -d '{"allowedOrigins":["*"],"maxAgeSeconds":60}' >/dev/null
out="$(call -X OPTIONS "${BASE}/v1/models" -H 'Origin: http://localhost:5173' -H 'Access-Control-Request-Method: GET')"
[[ "$(status_of <<<"$out")" == "204" ]] || fail "wildcard must allow any well-formed origin"
out="$(call -X OPTIONS "${BASE}/v1/models" -H 'Origin: null' -H 'Access-Control-Request-Method: GET')"
[[ "$(status_of <<<"$out")" == "403" ]] || fail "the null origin must never pass"

# 8. The change is audited without secrets.
curl --fail --silent "${BASE}/api/admin/audit?take=50" | jq -e 'map(.action) | index("cors.settings.update") != null and index("credential.origins.update") != null' >/dev/null \
  || fail "CORS changes must be audited"

echo "CORS smoke passed."
