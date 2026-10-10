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

if [[ -f "$SCRIPT_DIR/prepare-node-host.sh" ]]; then
  if ! bash "$SCRIPT_DIR/prepare-node-host.sh"; then
    # Enrollment/visibility must still work when the host cannot yet serve models.
    # The Admin inventory will show the outstanding prerequisites and can retry.
    echo "[llmproxy-agent] Host prerequisite preparation incomplete; installing the Agent for remote diagnosis anyway." >&2
  fi
fi

install -d -m 0755 /opt/llmproxy-node-agent /etc/llmproxy /var/lib/llmproxy-node-agent/huggingface
# Replace the executable atomically: overwriting a running Linux binary in-place
# fails with ETXTBSY and must never reset the stored node identity.
install -m 0755 "$BINARY" /opt/llmproxy-node-agent/LlmProxy.NodeAgent.staging
mv -f /opt/llmproxy-node-agent/LlmProxy.NodeAgent.staging /opt/llmproxy-node-agent/LlmProxy.NodeAgent
if [[ -f "$SCRIPT_DIR/airllm-runtime/Dockerfile" ]]; then
  install -d -m 0755 /opt/llmproxy-node-agent/airllm-runtime
  cp -a "$SCRIPT_DIR/airllm-runtime/." /opt/llmproxy-node-agent/airllm-runtime/
fi
if [[ -f "$SCRIPT_DIR/prepare-node-host.sh" ]]; then
  install -m 0755 "$SCRIPT_DIR/prepare-node-host.sh" /opt/llmproxy-node-agent/prepare-node-host.sh
fi
if [[ -f "$SCRIPT_DIR/update-node-agent.sh" ]]; then
  install -m 0755 "$SCRIPT_DIR/update-node-agent.sh" /opt/llmproxy-node-agent/update-node-agent.sh
fi
install -m 0644 "$SERVICE" /etc/systemd/system/llmproxy-node-agent.service

if [[ ! -f /etc/llmproxy/node-agent.env ]]; then
  install -m 0600 "$ENV_EXAMPLE" /etc/llmproxy/node-agent.env
  echo "Created /etc/llmproxy/node-agent.env from the template."
else
  chmod 0600 /etc/llmproxy/node-agent.env
  echo "Preserved existing /etc/llmproxy/node-agent.env."
fi

# Per-node recovery never deletes gateway-connection.json before the gateway
# accepts the recovery key. The Agent switches secrets only after success.
if [[ -n "${LLMPROXY_RECOVERY_TOKEN:-}" || -n "${LLMPROXY_RECOVERY_NODE_ID:-}" ]]; then
  [[ "${LLMPROXY_RECOVERY_TOKEN:-}" =~ ^lpr_[A-Za-z0-9_-]{20,160}$ &&
     "${LLMPROXY_RECOVERY_NODE_ID:-}" =~ ^[0-9a-fA-F-]{36}$ ]] || {
    echo "Recovery needs a valid server-specific node ID and recovery key." >&2; exit 3;
  }
  [[ -z "${LLMPROXY_ENROLLMENT_TOKEN:-}" ]] || {
    echo "Use either recovery or a new pairing invitation, not both." >&2; exit 3;
  }
  [[ -n "${LLMPROXY_GATEWAY_URL:-}" ]] || { echo "LLMPROXY_GATEWAY_URL is required." >&2; exit 3; }
  if ! grep -q '^NodeAgent__RecoveryToken=' /etc/llmproxy/node-agent.env; then
    printf 'NodeAgent__RecoveryToken=\\nNodeAgent__RecoveryNodeId=\\nNodeAgent__ForceRecovery=false\\n' >> /etc/llmproxy/node-agent.env
  fi
  sed -i "s|^NodeAgent__GatewayBaseAddress=.*|NodeAgent__GatewayBaseAddress=${LLMPROXY_GATEWAY_URL}|" /etc/llmproxy/node-agent.env
  sed -i "s|^NodeAgent__RecoveryNodeId=.*|NodeAgent__RecoveryNodeId=${LLMPROXY_RECOVERY_NODE_ID}|" /etc/llmproxy/node-agent.env
  sed -i "s|^NodeAgent__RecoveryToken=.*|NodeAgent__RecoveryToken=${LLMPROXY_RECOVERY_TOKEN}|" /etc/llmproxy/node-agent.env
  sed -i 's|^NodeAgent__ForceRecovery=.*|NodeAgent__ForceRecovery=true|' /etc/llmproxy/node-agent.env
  sed -i "s|^NodeAgent__ConnectionMode=.*|NodeAgent__ConnectionMode=${LLMPROXY_CONNECTION_MODE:-outbound}|" /etc/llmproxy/node-agent.env
  if [[ "${LLMPROXY_CONNECTION_MODE:-outbound}" == "outbound" ]]; then
    sed -i 's|^ASPNETCORE_URLS=.*|ASPNETCORE_URLS=http://127.0.0.1:9900|' /etc/llmproxy/node-agent.env
  fi
  if grep -q 'CHANGE_ME_LONG_RANDOM_SECRET' /etc/llmproxy/node-agent.env; then
    generated_bearer="$(openssl rand -hex 32)"
    sed -i "s/CHANGE_ME_LONG_RANDOM_SECRET/${generated_bearer}/" /etc/llmproxy/node-agent.env
  fi
  chmod 0600 /etc/llmproxy/node-agent.env
fi

# A single installer invocation can carry the invitation without a subsequent manual edit.
if [[ -n "${LLMPROXY_GATEWAY_URL:-}" && -n "${LLMPROXY_ENROLLMENT_TOKEN:-}" ]]; then
  sed -i "s|^NodeAgent__GatewayBaseAddress=.*|NodeAgent__GatewayBaseAddress=${LLMPROXY_GATEWAY_URL}|" /etc/llmproxy/node-agent.env
  sed -i "s|^NodeAgent__EnrollmentToken=.*|NodeAgent__EnrollmentToken=${LLMPROXY_ENROLLMENT_TOKEN}|" /etc/llmproxy/node-agent.env
  sed -i "s|^NodeAgent__ConnectionMode=.*|NodeAgent__ConnectionMode=${LLMPROXY_CONNECTION_MODE:-outbound}|" /etc/llmproxy/node-agent.env
  if [[ "${LLMPROXY_CONNECTION_MODE:-outbound}" == "outbound" ]]; then
    # The reverse channel needs loopback Agent HTTP only. Never expose management on the WAN.
    sed -i 's|^ASPNETCORE_URLS=.*|ASPNETCORE_URLS=http://127.0.0.1:9900|' /etc/llmproxy/node-agent.env
  else
    sed -i 's|^ASPNETCORE_URLS=.*|ASPNETCORE_URLS=http://0.0.0.0:9900|' /etc/llmproxy/node-agent.env
  fi
  # Runtime GPU selection is decided at launch from the live driver/toolkit state.
  # Keep the configured preference so a later successful Admin host repair takes effect without env edits.
  # Explicit new invitation means intentional (re)pairing. Preserve cache and installations,
  # but invalidate only the local gateway identity so a revoked node can re-enroll.
  if [[ -f /var/lib/llmproxy-node-agent/gateway-connection.json ]]; then
    systemctl stop llmproxy-node-agent >/dev/null 2>&1 || true
    rm -f /var/lib/llmproxy-node-agent/gateway-connection.json
  fi
  if grep -q 'CHANGE_ME_LONG_RANDOM_SECRET' /etc/llmproxy/node-agent.env; then
    generated_bearer="$(openssl rand -hex 32)"
    sed -i "s/CHANGE_ME_LONG_RANDOM_SECRET/${generated_bearer}/" /etc/llmproxy/node-agent.env
  fi
  if grep -q '^NodeAgent__AdvertiseHost=' /etc/llmproxy/node-agent.env; then
    advertise_host="${LLMPROXY_ADVERTISE_HOST:-$(hostname -f 2>/dev/null || hostname)}"
    if [[ ! "$advertise_host" =~ ^[a-zA-Z0-9.:-]+$ ]]; then
      echo "Invalid advertised address; use a DNS name or IP address." >&2
      exit 2
    fi
    sed -i "s/^NodeAgent__AdvertiseHost=.*/NodeAgent__AdvertiseHost=${advertise_host}/" /etc/llmproxy/node-agent.env
  fi
fi

systemctl daemon-reload

if grep -q 'CHANGE_ME_LONG_RANDOM_SECRET' /etc/llmproxy/node-agent.env || grep -q '^NodeAgent__AdvertiseHost=$' /etc/llmproxy/node-agent.env; then
  systemctl disable llmproxy-node-agent >/dev/null 2>&1 || true
  echo
  echo "Node agent installed but NOT started."
  echo "Edit /etc/llmproxy/node-agent.env: set a long random bearer and the address reachable from LlmProxy."
  echo "Then run: systemctl enable llmproxy-node-agent
# Reload an already-active Agent so repair/bootstrap configuration is applied.
systemctl restart llmproxy-node-agent"
  exit 0
fi

systemctl enable --now llmproxy-node-agent
echo "LlmProxy Node Agent installed and started."
systemctl --no-pager --full status llmproxy-node-agent || true
