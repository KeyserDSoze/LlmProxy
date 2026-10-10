#!/usr/bin/env bash
set -euo pipefail
# Public gateway downloads (without authentication); pairing requires a separate short-lived token.
: "${LLMPROXY_GATEWAY_URL:?Set LLMPROXY_GATEWAY_URL to the HTTPS LlmProxy address}"
# New nodes use a one-time invitation; known nodes use an admin-managed
# server-specific recovery code that reclaims their existing node identity.
if [[ -z "${LLMPROXY_ENROLLMENT_TOKEN:-}" &&
      ( -z "${LLMPROXY_RECOVERY_TOKEN:-}" || -z "${LLMPROXY_RECOVERY_NODE_ID:-}" ) ]]; then
  echo "Supply a new pairing invitation OR an existing node ID + recovery code." >&2
  exit 1
fi
if [[ -n "${LLMPROXY_ENROLLMENT_TOKEN:-}" && -n "${LLMPROXY_RECOVERY_TOKEN:-}" ]]; then
  echo "Use invitation or recovery, not both." >&2
  exit 1
fi
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
gateway="${LLMPROXY_GATEWAY_URL%/}"
if [[ ! "$gateway" =~ ^https://[a-zA-Z0-9._:-]+$ &&
      ! "$gateway" =~ ^http://(localhost|127\.0\.0\.1)(:[0-9]+)?$ ]]; then
  echo "An HTTPS LlmProxy domain is required." >&2; exit 3
fi
version="$(curl --retry 3 -fsSL "$gateway/downloads/agent/version")"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "No verified stable release found on LlmProxy." >&2; exit 3
fi
archive="llmproxy-node-agent-${version}-${rid}.tar.gz"
base="$gateway/downloads/agent/$version/$rid.tar.gz"
mkdir -p dist unpacked
curl --retry 3 -fLsS "$base" -o "dist/$archive"
curl --retry 3 -fLsS "$base.sha256" -o "dist/$archive.sha256"
sha256sum -c "dist/$archive.sha256"
tar -xzf "dist/$archive" -C unpacked
cd unpacked
bash install-node-agent.sh
echo "Node Agent installed and paired with LlmProxy if its registration request succeeds."
