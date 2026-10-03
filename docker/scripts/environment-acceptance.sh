#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_DIR="${LLMPROXY_INSTALL_DIR:-/opt/llmproxy}"
ENV_FILE="${LLMPROXY_ENV_FILE:-$INSTALL_DIR/.env}"
GATEWAY_URL="${LLMPROXY_ACCEPTANCE_GATEWAY_URL:-}"
INFERENCE_NODE_URL="${LLMPROXY_ACCEPTANCE_INFERENCE_NODE_URL:-}"
PUBLIC_MODEL="${LLMPROXY_ACCEPTANCE_PUBLIC_MODEL:-}"
PROVIDER_MODEL="${LLMPROXY_ACCEPTANCE_PROVIDER_MODEL:-}"
EVIDENCE_DIR="${LLMPROXY_ACCEPTANCE_EVIDENCE_DIR:-}"
SKIP_DOCKER=false
VALIDATE_ONLY=false

usage() {
  cat <<'EOF'
Usage: bash docker/scripts/environment-acceptance.sh [options]

Runs target-host acceptance for the production Linux deployment and writes a
metadata-only evidence bundle. Response bodies, prompts, generated output and
API secrets are never copied into the evidence bundle.

Options:
  --env-file FILE        Production env file (default: /opt/llmproxy/.env)
  --gateway-url URL      LlmProxy service root (default from env, localhost)
  --node-url URL          inference node/vLLM service root (default: INFERENCE_NODE_BASE_ADDRESS)
  --public-model MODEL   Logical public model (default: PUBLIC_MODEL_NAME)
  --provider-model MODEL Provider/vLLM model id (default: PROVIDER_MODEL_NAME)
  --evidence-dir DIR     Evidence output directory
  --skip-docker          Skip Docker/Compose host checks
  --validate-only        Validate script/repository compatibility without probes
  -h, --help             Show this help

Secrets are intentionally not accepted as command-line flags. The gateway API
key is read from LLMPROXY_ACCEPTANCE_API_KEY or LLM_PROXY_API_KEY in the env
file. Optional inference node bearer auth is read from LLMPROXY_ACCEPTANCE_INFERENCE_NODE_API_KEY.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file)
      ENV_FILE="${2:?--env-file requires a value}"
      shift 2
      ;;
    --gateway-url)
      GATEWAY_URL="${2:?--gateway-url requires a value}"
      shift 2
      ;;
    --node-url)
      INFERENCE_NODE_URL="${2:?--node-url requires a value}"
      shift 2
      ;;
    --public-model)
      PUBLIC_MODEL="${2:?--public-model requires a value}"
      shift 2
      ;;
    --provider-model)
      PROVIDER_MODEL="${2:?--provider-model requires a value}"
      shift 2
      ;;
    --evidence-dir)
      EVIDENCE_DIR="${2:?--evidence-dir requires a value}"
      shift 2
      ;;
    --skip-docker)
      SKIP_DOCKER=true
      shift
      ;;
    --validate-only)
      VALIDATE_ONLY=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ "$VALIDATE_ONLY" == "true" ]]; then
  bash -n "$ROOT_DIR/docker/scripts/environment-acceptance.sh"
  test -f "$ROOT_DIR/docker/.env.production.example"
  printf 'Environment acceptance validation OK.\n'
  exit 0
fi

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command is missing: $1" >&2
    exit 3
  fi
}

require_command curl
require_command jq
require_command uname

if [[ ! -r "$ENV_FILE" ]]; then
  echo "Production env file is not readable: $ENV_FILE" >&2
  exit 4
fi

read_env_value() {
  local key="$1"
  awk -v key="$key" 'index($0, key "=") == 1 { sub(/^[^=]*=/, ""); print; exit }' "$ENV_FILE"
}

if [[ -z "$GATEWAY_URL" ]]; then
  gateway_port="$(read_env_value LLMPROXY_PORT)"
  GATEWAY_URL="http://127.0.0.1:${gateway_port:-8080}"
fi
if [[ -z "$INFERENCE_NODE_URL" ]]; then
  INFERENCE_NODE_URL="$(read_env_value INFERENCE_NODE_BASE_ADDRESS)"
fi
if [[ -z "$PUBLIC_MODEL" ]]; then
  PUBLIC_MODEL="$(read_env_value PUBLIC_MODEL_NAME)"
fi
if [[ -z "$PROVIDER_MODEL" ]]; then
  PROVIDER_MODEL="$(read_env_value PROVIDER_MODEL_NAME)"
fi

API_KEY="${LLMPROXY_ACCEPTANCE_API_KEY:-$(read_env_value LLM_PROXY_API_KEY)}"
INFERENCE_NODE_API_KEY="${LLMPROXY_ACCEPTANCE_INFERENCE_NODE_API_KEY:-}"

for pair in \
  "gateway URL:$GATEWAY_URL" \
  "inference node URL:$INFERENCE_NODE_URL" \
  "public model:$PUBLIC_MODEL" \
  "provider model:$PROVIDER_MODEL" \
  "gateway API key:$API_KEY"; do
  name="${pair%%:*}"
  value="${pair#*:}"
  if [[ -z "$value" || "$value" == CHANGE_ME* ]]; then
    echo "Acceptance requires a resolved $name." >&2
    exit 5
  fi
done

GATEWAY_URL="${GATEWAY_URL%/}"
INFERENCE_NODE_URL="${INFERENCE_NODE_URL%/}"

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
if [[ -z "$EVIDENCE_DIR" ]]; then
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  EVIDENCE_DIR="$INSTALL_DIR/acceptance/$stamp"
fi
mkdir -p "$EVIDENCE_DIR"
chmod 700 "$EVIDENCE_DIR"
SUMMARY_FILE="$EVIDENCE_DIR/summary.md"
CHECKS_FILE="$EVIDENCE_DIR/checks.tsv"
: > "$SUMMARY_FILE"
: > "$CHECKS_FILE"
chmod 600 "$SUMMARY_FILE" "$CHECKS_FILE"

TMP_DIR="$(mktemp -d)"
chmod 700 "$TMP_DIR"
cleanup() {
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  DISTRO_NAME="${PRETTY_NAME:-${ID:-unknown}}"
else
  DISTRO_NAME="unknown"
fi
KERNEL="$(uname -srmo 2>/dev/null || uname -a)"
ARCH="$(uname -m)"
DOCKER_SERVER="skipped"
COMPOSE_VERSION="skipped"
FAILURES=0

record_check() {
  local outcome="$1"
  local name="$2"
  local target="$3"
  local http_status="$4"
  local content_type="$5"
  local ttfb="$6"
  local total="$7"
  local detail="$8"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$outcome" "$name" "$target" "$http_status" "$content_type" "$ttfb" "$total" "$detail" >> "$CHECKS_FILE"
  if [[ "$outcome" != "PASS" ]]; then
    FAILURES=$((FAILURES + 1))
  fi
}

if [[ "$SKIP_DOCKER" == "false" ]]; then
  require_command docker
  if DOCKER_SERVER="$(docker version --format '{{.Server.Version}}' 2>/dev/null)"; then
    record_check PASS "docker-engine" "local" "-" "-" "-" "-" "server=$DOCKER_SERVER"
  else
    DOCKER_SERVER="unavailable"
    record_check FAIL "docker-engine" "local" "-" "-" "-" "-" "Docker daemon unavailable"
  fi
  if COMPOSE_VERSION="$(docker compose version --short 2>/dev/null)"; then
    record_check PASS "docker-compose" "local" "-" "-" "-" "-" "version=$COMPOSE_VERSION"
  else
    COMPOSE_VERSION="unavailable"
    record_check FAIL "docker-compose" "local" "-" "-" "-" "-" "Docker Compose v2 unavailable"
  fi
fi

write_auth_header() {
  local path="$1"
  local value="$2"
  : > "$path"
  chmod 600 "$path"
  if [[ -n "$value" ]]; then
    printf 'Authorization: Bearer %s\n' "$value" > "$path"
  fi
}

http_probe() {
  local name="$1"
  local method="$2"
  local url="$3"
  local auth_value="$4"
  local request_file="$5"
  local expected_type="$6"
  local expected_model="$7"
  local headers="$TMP_DIR/${name}.headers"
  local body="$TMP_DIR/${name}.body"
  local auth_file="$TMP_DIR/${name}.auth"
  local metrics curl_exit http_status content_type ttfb total outcome detail

  write_auth_header "$auth_file" "$auth_value"
  local -a args=(
    --silent --show-error --location
    --connect-timeout 10 --max-time 120
    --dump-header "$headers"
    --output "$body"
    --write-out $'%{http_code}\t%{content_type}\t%{time_starttransfer}\t%{time_total}'
    --request "$method"
  )
  if [[ -s "$auth_file" ]]; then
    args+=(--header "@$auth_file")
  fi
  if [[ -n "$request_file" ]]; then
    args+=(--header 'Content-Type: application/json' --data-binary "@$request_file")
  fi

  set +e
  metrics="$(curl "${args[@]}" "$url" 2>"$TMP_DIR/${name}.stderr")"
  curl_exit=$?
  set -e

  IFS=$'\t' read -r http_status content_type ttfb total <<< "$metrics"
  http_status="${http_status:-000}"
  content_type="${content_type:-unknown}"
  ttfb="${ttfb:-unknown}"
  total="${total:-unknown}"
  outcome=PASS
  detail="ok"

  if [[ "$curl_exit" -ne 0 ]]; then
    outcome=FAIL
    detail="curl_exit=$curl_exit"
  elif [[ ! "$http_status" =~ ^2[0-9][0-9]$ ]]; then
    outcome=FAIL
    detail="unexpected_http_status"
  elif [[ -n "$expected_type" && "$content_type" != "$expected_type"* ]]; then
    outcome=FAIL
    detail="unexpected_content_type"
  elif [[ -n "$expected_model" ]] && ! jq -e --arg expected "$expected_model" '(.data // []) | any(.id == $expected)' "$body" >/dev/null 2>&1; then
    outcome=FAIL
    detail="expected_model_not_advertised"
  fi

  record_check "$outcome" "$name" "$url" "$http_status" "$content_type" "$ttfb" "$total" "$detail"
}

CHAT_INFERENCE_NODE="$TMP_DIR/chat-inference-node.json"
CHAT_INFERENCE_NODE_STREAM="$TMP_DIR/chat-inference-stream.json"
RESP_INFERENCE_NODE="$TMP_DIR/resp-inference-node.json"
RESP_INFERENCE_NODE_STREAM="$TMP_DIR/resp-inference-stream.json"
CHAT_GATEWAY="$TMP_DIR/chat-gateway.json"
CHAT_GATEWAY_STREAM="$TMP_DIR/chat-gateway-stream.json"
RESP_GATEWAY="$TMP_DIR/resp-gateway.json"
RESP_GATEWAY_STREAM="$TMP_DIR/resp-gateway-stream.json"

jq -n --arg model "$PROVIDER_MODEL" '{model:$model,messages:[{role:"user",content:"Reply with exactly OK."}],max_tokens:8,stream:false}' > "$CHAT_INFERENCE_NODE"
jq -n --arg model "$PROVIDER_MODEL" '{model:$model,messages:[{role:"user",content:"Reply with exactly OK."}],max_tokens:8,stream:true}' > "$CHAT_INFERENCE_NODE_STREAM"
jq -n --arg model "$PROVIDER_MODEL" '{model:$model,input:"Reply with exactly OK.",max_output_tokens:8,stream:false}' > "$RESP_INFERENCE_NODE"
jq -n --arg model "$PROVIDER_MODEL" '{model:$model,input:"Reply with exactly OK.",max_output_tokens:8,stream:true}' > "$RESP_INFERENCE_NODE_STREAM"
jq -n --arg model "$PUBLIC_MODEL" '{model:$model,messages:[{role:"user",content:"Reply with exactly OK."}],max_tokens:8,stream:false}' > "$CHAT_GATEWAY"
jq -n --arg model "$PUBLIC_MODEL" '{model:$model,messages:[{role:"user",content:"Reply with exactly OK."}],max_tokens:8,stream:true}' > "$CHAT_GATEWAY_STREAM"
jq -n --arg model "$PUBLIC_MODEL" '{model:$model,input:"Reply with exactly OK.",max_output_tokens:8,stream:false}' > "$RESP_GATEWAY"
jq -n --arg model "$PUBLIC_MODEL" '{model:$model,input:"Reply with exactly OK.",max_output_tokens:8,stream:true}' > "$RESP_GATEWAY_STREAM"
chmod 600 "$TMP_DIR"/*.json

# vLLM's canonical /health endpoint intentionally returns an empty Response on
# success, so acceptance validates its HTTP status only rather than requiring JSON.
http_probe "inference-health" GET "$INFERENCE_NODE_URL/health" "$INFERENCE_NODE_API_KEY" "" "" ""
http_probe "inference-models" GET "$INFERENCE_NODE_URL/v1/models" "$INFERENCE_NODE_API_KEY" "" "application/json" "$PROVIDER_MODEL"
http_probe "inference-chat" POST "$INFERENCE_NODE_URL/v1/chat/completions" "$INFERENCE_NODE_API_KEY" "$CHAT_INFERENCE_NODE" "application/json" ""
http_probe "inference-chat-stream" POST "$INFERENCE_NODE_URL/v1/chat/completions" "$INFERENCE_NODE_API_KEY" "$CHAT_INFERENCE_NODE_STREAM" "text/event-stream" ""
http_probe "inference-responses" POST "$INFERENCE_NODE_URL/v1/responses" "$INFERENCE_NODE_API_KEY" "$RESP_INFERENCE_NODE" "application/json" ""
http_probe "inference-responses-stream" POST "$INFERENCE_NODE_URL/v1/responses" "$INFERENCE_NODE_API_KEY" "$RESP_INFERENCE_NODE_STREAM" "text/event-stream" ""

http_probe "gateway-health" GET "$GATEWAY_URL/healthz" "" "" "application/json" ""
http_probe "gateway-readiness" GET "$GATEWAY_URL/readyz" "" "" "application/json" ""
http_probe "gateway-models" GET "$GATEWAY_URL/v1/models" "$API_KEY" "" "application/json" "$PUBLIC_MODEL"
http_probe "gateway-chat" POST "$GATEWAY_URL/v1/chat/completions" "$API_KEY" "$CHAT_GATEWAY" "application/json" ""
http_probe "gateway-chat-stream" POST "$GATEWAY_URL/v1/chat/completions" "$API_KEY" "$CHAT_GATEWAY_STREAM" "text/event-stream" ""
http_probe "gateway-responses" POST "$GATEWAY_URL/v1/responses" "$API_KEY" "$RESP_GATEWAY" "application/json" ""
http_probe "gateway-responses-stream" POST "$GATEWAY_URL/v1/responses" "$API_KEY" "$RESP_GATEWAY_STREAM" "text/event-stream" ""

finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
{
  echo "# LlmProxy environment acceptance"
  echo
  echo "- Started UTC: $started_at"
  echo "- Finished UTC: $finished_at"
  echo "- Host distro: $DISTRO_NAME"
  echo "- Kernel: $KERNEL"
  echo "- Architecture: $ARCH"
  echo "- Docker server: $DOCKER_SERVER"
  echo "- Docker Compose: $COMPOSE_VERSION"
  echo "- Gateway root: $GATEWAY_URL"
  echo "- inference node root: $INFERENCE_NODE_URL"
  echo "- Logical model: $PUBLIC_MODEL"
  echo "- Provider model: $PROVIDER_MODEL"
  echo "- Result: $([[ "$FAILURES" -eq 0 ]] && echo PASS || echo FAIL)"
  echo
  echo "The bundle intentionally excludes request/response bodies, prompts, generated output and API secrets."
  echo
  echo "## Checks"
  echo
  echo '| Result | Check | HTTP | Content-Type | TTFB (s) | Total (s) | Detail |'
  echo '| --- | --- | ---: | --- | ---: | ---: | --- |'
  while IFS=$'\t' read -r outcome name target status content_type ttfb total detail; do
    printf '| %s | %s | %s | %s | %s | %s | %s |\n' \
      "$outcome" "$name" "$status" "$content_type" "$ttfb" "$total" "$detail"
  done < "$CHECKS_FILE"
} > "$SUMMARY_FILE"

if [[ -n "$API_KEY" ]] && grep -Fq "$API_KEY" "$SUMMARY_FILE" "$CHECKS_FILE"; then
  echo "Acceptance evidence unexpectedly contains the gateway API key." >&2
  exit 6
fi
if [[ -n "$INFERENCE_NODE_API_KEY" ]] && grep -Fq "$INFERENCE_NODE_API_KEY" "$SUMMARY_FILE" "$CHECKS_FILE"; then
  echo "Acceptance evidence unexpectedly contains the inference node API key." >&2
  exit 6
fi

printf 'Acceptance evidence: %s\n' "$SUMMARY_FILE"
if [[ "$FAILURES" -ne 0 ]]; then
  printf 'Environment acceptance failed: %s check(s) failed.\n' "$FAILURES" >&2
  exit 10
fi
printf 'Environment acceptance passed.\n'
