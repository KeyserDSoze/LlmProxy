#!/usr/bin/env bash
set -euo pipefail
# Only HTTPS GitHub releases + a SHA256-verified immutable agent archive are installed.
: "${LLMPROXY_GATEWAY_URL:?Set LLMPROXY_GATEWAY_URL to the HTTPS LlmProxy address}"
: "${LLMPROXY_ENROLLMENT_TOKEN:?Set LLMPROXY_ENROLLMENT_TOKEN from Admin pairing invitation}"
if [[ "$(id -u)" != 0 ]]; then echo "This installer requires sudo." >&2; exit 1; fi
for tool in curl sha256sum tar systemctl openssl sed; do command -v "$tool" >/dev/null || { echo "Missing $tool" >&2; exit 1; }; done
case "$(uname -m)" in
  x86_64) rid="linux-x64";;
  aarch64|arm64) rid="linux-arm64";;
  *) echo "Only Linux x86_64 and arm64 are supported." >&2; exit 2;;
esac
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cd "$tmp"
release="$(curl --retry 3 -fsSL https://api.github.com/repos/KeyserDSoze/LlmProxy/releases/latest)"
tag="$(printf '%s\n' "$release" | sed -n 's/^[[:space:]]*"tag_name":[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
if [[ ! "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "No verified stable release found." >&2; exit 3
fi
version="${tag#v}"
archive="llmproxy-node-agent-${version}-${rid}.tar.gz"
base="https://github.com/KeyserDSoze/LlmProxy/releases/download/${tag}/${archive}"
mkdir -p dist unpacked
curl --retry 3 -fsSL "$base" -o "dist/$archive"
curl --retry 3 -fsSL "$base.sha256" -o "dist/$archive.sha256"
sha256sum -c "dist/$archive.sha256"
tar -xzf "dist/$archive" -C unpacked
cd unpacked
bash install-node-agent.sh
echo "Node Agent installed and paired with LlmProxy if its registration request succeeds."
