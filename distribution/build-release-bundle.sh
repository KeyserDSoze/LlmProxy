#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:?Usage: build-release-bundle.sh VERSION [output-dir]}"
OUT_DIR="${2:-$ROOT_DIR/dist}"

VALIDATED_VERSION="$(bash "$ROOT_DIR/docker/scripts/validate-release-version.sh" "$VERSION")"
if [[ "$VALIDATED_VERSION" != "$VERSION" ]]; then
  echo "Validated version mismatch: $VALIDATED_VERSION != $VERSION" >&2
  exit 2
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
BUNDLE="$STAGE/llmproxy-$VERSION"
mkdir -p "$BUNDLE/docker" "$BUNDLE/distribution"
printf '%s\n' "$VERSION" > "$BUNDLE/VERSION"

cp "$ROOT_DIR/docker/docker-compose.full.yml" "$BUNDLE/docker/"
cp "$ROOT_DIR/docker/.env.production.example" "$BUNDLE/docker/"
cp -a "$ROOT_DIR/docker/observability" "$BUNDLE/docker/"
cp -a "$ROOT_DIR/docker/scripts" "$BUNDLE/docker/"
cp "$ROOT_DIR/distribution/install.sh" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/bootstrap.sh" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/llmproxyctl" "$BUNDLE/distribution/"
# systemd is installed by distribution/install.sh after the containers are
# healthy; the service unit MUST be inside the immutable release archive.
cp "$ROOT_DIR/distribution/llmproxy.service" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/install-node-agent.sh" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/llmproxy-node-agent.service" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/node-agent.env.example" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/update-plan.json" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/install-update-agent.sh" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/llmproxy-update-agent.service" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/update-agent.env.example" "$BUNDLE/distribution/"
if [[ -f "$ROOT_DIR/distribution/update.sh" ]]; then
  cp "$ROOT_DIR/distribution/update.sh" "$BUNDLE/distribution/"
fi
if [[ -n "${UPDATE_AGENT_X64_BINARY:-}" && -n "${UPDATE_AGENT_ARM64_BINARY:-}" ]]; then
  mkdir -p "$BUNDLE/distribution/update-agent/linux-x64" "$BUNDLE/distribution/update-agent/linux-arm64"
  cp "$UPDATE_AGENT_X64_BINARY" "$BUNDLE/distribution/update-agent/linux-x64/LlmProxy.UpdateAgent"
  cp "$UPDATE_AGENT_ARM64_BINARY" "$BUNDLE/distribution/update-agent/linux-arm64/LlmProxy.UpdateAgent"
  chmod 0755 "$BUNDLE/distribution/update-agent/"*/LlmProxy.UpdateAgent
fi
chmod 0755 "$BUNDLE/distribution/"*.sh "$BUNDLE/distribution/llmproxyctl"

mkdir -p "$OUT_DIR"
ARCHIVE="$OUT_DIR/llmproxy-$VERSION-linux.tar.gz"
tar -C "$STAGE" -czf "$ARCHIVE" "llmproxy-$VERSION"
# Read the entire archive before checking its contents. Under pipefail,
# grep -q exits as soon as it finds a match and can SIGPIPE tar; a correct
# archive would then be incorrectly rejected. Keep both corruption and
# missing-systemd-unit checks fail-closed.
MANIFEST="$STAGE/archive-contents.txt"
if ! tar -tzf "$ARCHIVE" > "$MANIFEST"; then
  echo "Generated Linux release archive is corrupt or incomplete." >&2
  exit 4
fi
if ! grep -Fx 'llmproxy-'"$VERSION"'/distribution/llmproxy.service' "$MANIFEST" >/dev/null; then
  echo "Release archive is missing the LLMProxy systemd unit." >&2
  exit 4
fi
(
  cd "$OUT_DIR"
  sha256sum "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256"
)
cp "$ROOT_DIR/distribution/bootstrap.sh" "$OUT_DIR/llmproxy-bootstrap.sh"
cp "$ROOT_DIR/distribution/update-plan.json" "$OUT_DIR/llmproxy-update-plan.json"
(
  cd "$OUT_DIR"
  sha256sum llmproxy-bootstrap.sh > llmproxy-bootstrap.sh.sha256
  sha256sum llmproxy-update-plan.json > llmproxy-update-plan.json.sha256
)

printf 'Created release assets in %s\n' "$OUT_DIR"
