#!/usr/bin/env bash
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run this installer as root (for example: sudo bash install-node-agent.sh)." >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY="$SCRIPT_DIR/LlmProxy.NodeAgent"
SERVICE="$SCRIPT_DIR/llmproxy-node-agent.service"
ENV_EXAMPLE="$SCRIPT_DIR/node-agent.env.example"

for required in "$BINARY" "$SERVICE" "$ENV_EXAMPLE"; do
  [[ -f "$required" ]] || { echo "Missing release file: $required" >&2; exit 2; }
done

install -d -m 0755 /opt/llmproxy-node-agent /etc/llmproxy /var/lib/llmproxy-node-agent/huggingface
install -m 0755 "$BINARY" /opt/llmproxy-node-agent/LlmProxy.NodeAgent
install -m 0644 "$SERVICE" /etc/systemd/system/llmproxy-node-agent.service

if [[ ! -f /etc/llmproxy/node-agent.env ]]; then
  install -m 0600 "$ENV_EXAMPLE" /etc/llmproxy/node-agent.env
  echo "Created /etc/llmproxy/node-agent.env from the template."
else
  chmod 0600 /etc/llmproxy/node-agent.env
  echo "Preserved existing /etc/llmproxy/node-agent.env."
fi

systemctl daemon-reload

if grep -q 'CHANGE_ME_LONG_RANDOM_SECRET' /etc/llmproxy/node-agent.env || grep -q '^NodeAgent__AdvertiseHost=$' /etc/llmproxy/node-agent.env; then
  systemctl disable llmproxy-node-agent >/dev/null 2>&1 || true
  echo
  echo "Node agent installed but NOT started."
  echo "Edit /etc/llmproxy/node-agent.env: set a long random bearer and the address reachable from LlmProxy."
  echo "Then run: systemctl enable --now llmproxy-node-agent"
  exit 0
fi

systemctl enable --now llmproxy-node-agent
echo "LlmProxy Node Agent installed and started."
systemctl --no-pager --full status llmproxy-node-agent || true
