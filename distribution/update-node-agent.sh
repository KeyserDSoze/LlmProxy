#!/usr/bin/env bash
set -euo pipefail
version="${1:-}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid version." >&2; exit 2; }
[[ "$(id -u)" -eq 0 ]] || { echo "Root required." >&2; exit 2; }
state_file=/var/lib/llmproxy-node-agent/update-status.json
mkdir -p /var/lib/llmproxy-node-agent
chmod 0700 /var/lib/llmproxy-node-agent
write_state() {
  local stage="${2:-unknown}" percent="${3:-}"
  if [[ "$percent" =~ ^[0-9]+$ ]] && (( percent <= 100 )); then
    printf '{"status":"%s","version":"%s","stage":"%s","percent":%s}\n' "$1" "$version" "$stage" "$percent" > "${state_file}.tmp"
  else
    printf '{"status":"%s","version":"%s","stage":"%s"}\n' "$1" "$version" "$stage" > "${state_file}.tmp"
  fi
  chmod 0600 "${state_file}.tmp"
  mv -f "${state_file}.tmp" "$state_file"
}
write_state running preparing
cleanup() {
  result=$?
  if [[ "$result" -ne 0 ]]; then write_state failed failed; fi
  if [[ -n "${tmp:-}" && -d "$tmp" ]]; then rm -rf "$tmp"; fi
}
trap cleanup EXIT
for cmd in curl tar sha256sum systemctl; do command -v "$cmd" >/dev/null; done
case "$(uname -m)" in
  x86_64) rid="linux-x64";;
  aarch64|arm64) rid="linux-arm64";;
  *) exit 2;;
esac
tmp="$(mktemp -d)"
asset="llmproxy-node-agent-${version}-${rid}.tar.gz"
url="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${version}/${asset}"
mkdir "$tmp/archive" "$tmp/new"
# Try to learn the immutable archive total from the final HTTP response headers.
# Some registries omit Content-Length: keep progress indeterminate in that case.
total_bytes="$(curl --retry 2 --connect-timeout 8 --max-time 20 -fsSIL "$url" 2>/dev/null |
  tr -d '\r' | awk 'tolower($1)=="content-length:" {n=$2} END {print n}' || true)"
[[ "$total_bytes" =~ ^[1-9][0-9]*$ ]] || total_bytes=''
write_state running downloading
curl --retry 4 -fsSL "$url" -o "$tmp/archive/$asset" &
download_pid=$!
while kill -0 "$download_pid" 2>/dev/null; do
  if [[ -n "$total_bytes" && -f "$tmp/archive/$asset" ]]; then
    downloaded="$(stat -c '%s' "$tmp/archive/$asset" 2>/dev/null || echo 0)"
    if [[ "$downloaded" =~ ^[0-9]+$ ]]; then
      percent="$(( 100 * downloaded / total_bytes ))"
      (( percent > 100 )) && percent=100
      write_state running downloading "$percent"
    fi
  fi
  sleep 2
done
wait "$download_pid"
write_state running verifying
curl --retry 4 -fsSL "$url.sha256" -o "$tmp/archive/$asset.sha256"
# Release checksums contain the dist/ asset prefix; verify with the same layout.
mkdir -p "$tmp/dist"
cp "$tmp/archive/$asset" "$tmp/dist/$asset"
cp "$tmp/archive/$asset.sha256" "$tmp/dist/$asset.sha256"
(cd "$tmp" && sha256sum -c "dist/$asset.sha256")
write_state running extracting
tar -xzf "$tmp/archive/$asset" -C "$tmp/new"
test -x "$tmp/new/LlmProxy.NodeAgent"
test -f "$tmp/new/update-node-agent.sh"
install -m 0755 "$tmp/new/update-node-agent.sh" "$tmp/new/update-node-agent.sh.tmp"
mv "$tmp/new/update-node-agent.sh.tmp" "$tmp/new/update-node-agent.sh"
service=llmproxy-node-agent
old=/opt/llmproxy-node-agent
backup="/opt/llmproxy-node-agent.previous"
staging="/opt/llmproxy-node-agent.upgrade"
test -d "$old"
test ! -e "$staging"
cp -a "$tmp/new" "$staging"
write_state running switching
systemctl stop "$service"
rm -rf "$backup"
mv "$old" "$backup"
mv "$staging" "$old"
systemctl daemon-reload
write_state running healthcheck
if ! systemctl start "$service"; then
  systemctl stop "$service" || true
  rm -rf "$old"
  mv "$backup" "$old"
  systemctl start "$service"
  exit 1
fi
# Require both a live service and its local HTTP listener for several consecutive polls.
healthy=0
for attempt in $(seq 1 20); do
  status="$(curl --silent --output /dev/null --max-time 2 --write-out '%{http_code}' http://127.0.0.1:9900/health || true)"
  if systemctl is-active --quiet "$service" && [[ "$status" == "200" || "$status" == "401" ]]; then
    healthy=$((healthy + 1))
    if [[ "$healthy" -ge 3 ]]; then
      write_state succeeded completed 100
      echo "Node Agent upgraded to $version"
      exit 0
    fi
  else
    healthy=0
  fi
  sleep 2
done
systemctl stop "$service" || true
rm -rf "$old"
mv "$backup" "$old"
systemctl start "$service"
echo "New agent failed health check, previous release restored." >&2
exit 1
