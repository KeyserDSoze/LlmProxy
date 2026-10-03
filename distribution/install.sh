#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION_FILE="$ROOT_DIR/VERSION"
INSTALL_DIR="/opt/llmproxy"

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "LlmProxy installation requires root privileges." >&2
  echo "Run: sudo -E bash distribution/install.sh ..." >&2
  exit 2
fi

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "Release bundle is missing VERSION: $VERSION_FILE" >&2
  exit 3
fi

for arg in "$@"; do
  case "$arg" in
    --image-tag)
      echo "--image-tag is controlled by the immutable release bundle and cannot be overridden." >&2
      exit 2
      ;;
    --install-dir)
      echo "Release installations use the canonical /opt/llmproxy layout; --install-dir is not supported." >&2
      exit 2
      ;;
  esac
done

VERSION="$(tr -d '[:space:]' < "$VERSION_FILE")"
release_log() {
  printf '%s [llmproxy-release] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}
if [[ -z "$VERSION" ]]; then
  echo "Release bundle VERSION is empty." >&2
  exit 3
fi

RELEASE_DIR="$INSTALL_DIR/releases/$VERSION"
release_log "Installing immutable operator bundle $VERSION"
release_log "Release directory: $RELEASE_DIR"
mkdir -p "$INSTALL_DIR/releases"
rm -rf "$RELEASE_DIR.tmp"
mkdir -p "$RELEASE_DIR.tmp"
cp -a "$ROOT_DIR/." "$RELEASE_DIR.tmp/"
rm -rf "$RELEASE_DIR"
mv "$RELEASE_DIR.tmp" "$RELEASE_DIR"

# The existing installer owns host preparation, secret generation, inference node checks and deployment.
# Pin the application image to the exact release version represented by this bundle.
release_log "Starting host preparation and deployment"
LLMPROXY_INSTALL_DIR="$INSTALL_DIR" \
  bash "$RELEASE_DIR/docker/scripts/install-linux.sh" \
    --image-tag "$VERSION" \
    "$@"

release_log "Activating release $VERSION"
ln -sfn "$RELEASE_DIR" "$INSTALL_DIR/current"
install -m 0755 "$RELEASE_DIR/distribution/llmproxyctl" /usr/local/bin/llmproxyctl
install -d -m 0755 /usr/local/lib/llmproxy
install -m 0755 "$RELEASE_DIR/distribution/bootstrap.sh" /usr/local/lib/llmproxy/bootstrap.sh

printf '\nLlmProxy %s installed successfully.\n' "$VERSION"
printf 'Control command: llmproxyctl\n'
printf 'Persistent configuration: %s/.env\n' "$INSTALL_DIR"
printf 'Installed release: %s\n' "$RELEASE_DIR"
printf 'Install log: /var/log/llmproxy/latest-install.log\n'
