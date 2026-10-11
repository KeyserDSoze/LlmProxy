#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$root/distribution/install-node-agent.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
# Load only the environment updater, not the root/systemd installer.
awk '/^set_node_env\(\) \{/ {inside=1} inside {print} inside && /^\}/ {exit}' "$source_file" |
  sed "s|/etc/llmproxy/node-agent.env|$tmp/node-agent.env|g" > "$tmp/helper.sh"
# shellcheck source=/dev/null
source "$tmp/helper.sh"
cat > "$tmp/node-agent.env" <<'ENV'
NodeAgent__EnrollmentToken=expired
NodeAgent__RecoveryToken=old
NodeAgent__GatewayBaseAddress=https://old.example
NodeAgent__ConnectionMode=direct
ENV
# Regression: token exists, but node ID and force flag are absent.
set_node_env NodeAgent__EnrollmentToken ""
set_node_env NodeAgent__RecoveryNodeId "3ddee41b-1ea3-4d76-b548-61ab9a95bb06"
set_node_env NodeAgent__RecoveryToken "lpr_new12345678901234567890"
set_node_env NodeAgent__ForceRecovery true
set_node_env NodeAgent__GatewayBaseAddress "https://llmproxy.opencode.zone"
set_node_env NodeAgent__ConnectionMode outbound
for expected in \
  'NodeAgent__EnrollmentToken=' \
  'NodeAgent__RecoveryNodeId=3ddee41b-1ea3-4d76-b548-61ab9a95bb06' \
  'NodeAgent__RecoveryToken=lpr_new12345678901234567890' \
  'NodeAgent__ForceRecovery=true' \
  'NodeAgent__GatewayBaseAddress=https://llmproxy.opencode.zone' \
  'NodeAgent__ConnectionMode=outbound'; do
  grep -Fxq "$expected" "$tmp/node-agent.env"
done
# Repeat recovery: never create duplicate settings.
set_node_env NodeAgent__ForceRecovery true
[[ "$(grep -c '^NodeAgent__ForceRecovery=' "$tmp/node-agent.env")" -eq 1 ]]
# Switching to a new invitation must clear stale recovery values.
set_node_env NodeAgent__EnrollmentToken "fresh"
set_node_env NodeAgent__RecoveryNodeId ""
set_node_env NodeAgent__RecoveryToken ""
set_node_env NodeAgent__ForceRecovery false
grep -Fxq 'NodeAgent__RecoveryNodeId=' "$tmp/node-agent.env"
grep -Fxq 'NodeAgent__RecoveryToken=' "$tmp/node-agent.env"
grep -Fxq 'NodeAgent__ForceRecovery=false' "$tmp/node-agent.env"
grep -Fxq 'NodeAgent__EnrollmentToken=fresh' "$tmp/node-agent.env"
echo "Node Agent existing-env recovery regression: OK"
