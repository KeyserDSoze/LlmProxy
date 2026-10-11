#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
awk '/^node_connection_complete\(\) \{/ {inside=1} inside {print} inside && /^\}/ {exit}' \
  "$root/distribution/connect-node.sh" > "$tmp/check.sh"
# shellcheck source=/dev/null
source "$tmp/check.sh"
state="$tmp/gateway-connection.json"
env_file="$tmp/node-agent.env"
cat > "$env_file" <<'ENV'
NodeAgent__EnrollmentToken=expired
NodeAgent__RecoveryToken=old
NodeAgent__RecoveryNodeId=
NodeAgent__ForceRecovery=false
ENV
! node_connection_complete "$env_file" "$state" enrollment
printf '{"NodeId":"f00","AgentSecret":"<redacted>"}\n' > "$state"
! node_connection_complete "$env_file" "$state" enrollment
sed -i 's/^NodeAgent__EnrollmentToken=.*/NodeAgent__EnrollmentToken=/' "$env_file"
node_connection_complete "$env_file" "$state" enrollment
! node_connection_complete "$env_file" "$state" recovery
sed -i 's/^NodeAgent__RecoveryToken=.*/NodeAgent__RecoveryToken=/' "$env_file"
node_connection_complete "$env_file" "$state" recovery
sed -i 's/^NodeAgent__ForceRecovery=.*/NodeAgent__ForceRecovery=true/' "$env_file"
! node_connection_complete "$env_file" "$state" recovery
echo "Connect-node pairing status regression: OK"
