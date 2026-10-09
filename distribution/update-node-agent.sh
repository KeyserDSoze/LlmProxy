#!/usr/bin/env bash
set -euo pipefail
version="${1:-}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid version." >&2; exit 2; }
[[ "$(id -u)" -eq 0 ]] || { echo "Root required." >&2; exit 2; }
for cmd in curl tar sha256sum systemctl; do command -v "$cmd" >/dev/null; done
case "$(uname -m)" in
  x86_64) rid="linux-x64";;
  aarch64|arm64) rid="linux-arm64";;
  *) exit 2;;
esac
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
asset="llmproxy-node-agent-${version}-${rid}.tar.gz"
url="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${version}/${asset}"
mkdir "$tmp/archive" "$tmp/new"
curl --retry 4 -fsSL "$url" -o "$tmp/archive/$asset"
curl --retry 4 -fsSL "$url.sha256" -o "$tmp/archive/$asset.sha256"
(cd "$tmp" && sha256sum -c "archive/$asset.sha256") || {
  # Published checksum filenames are prefixed dist/; verify in that exact layout.
  mkdir -p "$tmp/dist"
  cp "$tmp/archive/$asset" "$tmp/dist/$asset"
  cp "$tmp/archive/$asset.sha256" "$tmp/dist/$asset.sha256"
  (cd "$tmp" && sha256sum -c "dist/$asset.sha256")
}
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
systemctl stop "$service"
rm -rf "$backup"
mv "$old" "$backup"
mv "$staging" "$old"
systemctl daemon-reload
if ! systemctl start "$service"; then
  systemctl stop "$service" || true
  rm -rf "$old"
  mv "$backup" "$old"
  systemctl start "$service"
  exit 1
fi
for attempt in $(seq 1 20); do
  if systemctl is-active --quiet "$service"; then
    echo "Node Agent upgraded to $version"
    exit 0
  fi
  sleep 2
done
systemctl stop "$service" || true
rm -rf "$old"
mv "$backup" "$old"
systemctl start "$service"
echo "New agent failed health check, previous release restored." >&2
exit 1
