#!/usr/bin/env bash
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run this installer as root." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${LLMPROXY_ENV_FILE:-/opt/llmproxy/.env}"
AGENT_ENV=/etc/llmproxy/update-agent.env
SERVICE_SRC="$SCRIPT_DIR/llmproxy-update-agent.service"
ENV_EXAMPLE="$SCRIPT_DIR/update-agent.env.example"

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) echo "Unsupported update-agent architecture: $(uname -m)" >&2; exit 2 ;;
esac

BINARY="$SCRIPT_DIR/update-agent/$RID/LlmProxy.UpdateAgent"
for required in "$BINARY" "$SERVICE_SRC" "$ENV_EXAMPLE"; do
  [[ -f "$required" ]] || { echo "Missing release file: $required" >&2; exit 2; }
done

install -d -m 0755 /opt/llmproxy-update-agent /etc/llmproxy /var/lib/llmproxy-update-agent
install -m 0755 "$BINARY" /opt/llmproxy-update-agent/LlmProxy.UpdateAgent
install -m 0644 "$SERVICE_SRC" /etc/systemd/system/llmproxy-update-agent.service

set_env_value() {
  local file="$1" key="$2" value="$3" tmp
  tmp="$(mktemp)"
  awk -v key="$key" -v value="$value" '
    BEGIN { found = 0 }
    index($0, key "=") == 1 { print key "=" value; found = 1; next }
    { print }
    END { if (!found) print key "=" value }
  ' "$file" > "$tmp"
  cat "$tmp" > "$file"
  rm -f "$tmp"
}

if [[ ! -f "$AGENT_ENV" ]]; then
  install -m 0600 "$ENV_EXAMPLE" "$AGENT_ENV"
fi
chmod 0600 "$AGENT_ENV"

TOKEN="$(sed -n 's/^UpdateAgent__BearerToken=//p' "$AGENT_ENV" | tail -n1)"
if [[ -z "$TOKEN" || "$TOKEN" == CHANGE_ME* ]]; then
  TOKEN="$(openssl rand -hex 32)"
  set_env_value "$AGENT_ENV" UpdateAgent__BearerToken "$TOKEN"
fi

BRIDGE_GATEWAY="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}' 2>/dev/null || true)"
BRIDGE_GATEWAY="${BRIDGE_GATEWAY:-172.17.0.1}"
set_env_value "$AGENT_ENV" ASPNETCORE_URLS "http://$BRIDGE_GATEWAY:9910"

[[ -f "$ENV_FILE" ]] || { echo "LlmProxy environment not found: $ENV_FILE" >&2; exit 3; }
set_env_value "$ENV_FILE" UPDATE_AGENT_BASE_ADDRESS "http://host.docker.internal:9910"
set_env_value "$ENV_FILE" UPDATE_AGENT_BEARER_TOKEN "$TOKEN"
chmod 0600 "$ENV_FILE"

systemctl daemon-reload
if [[ "${LLMPROXY_UPDATE_AGENT_ACTIVE:-}" == "1" ]] && systemctl is-active --quiet llmproxy-update-agent; then
  echo "Updated LlmProxy Update Agent files; restart deferred until the active update completes or the service restarts."
else
  systemctl enable --now llmproxy-update-agent
fi
