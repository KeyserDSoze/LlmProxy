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
cp "$ROOT_DIR/distribution/install-node-agent.sh" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/llmproxy-node-agent.service" "$BUNDLE/distribution/"
cp "$ROOT_DIR/distribution/node-agent.env.example" "$BUNDLE/distribution/"
chmod 0755 "$BUNDLE/distribution/"*.sh "$BUNDLE/distribution/llmproxyctl"

mkdir -p "$OUT_DIR"
ARCHIVE="$OUT_DIR/llmproxy-$VERSION-linux.tar.gz"
tar -C "$STAGE" -czf "$ARCHIVE" "llmproxy-$VERSION"
(
  cd "$OUT_DIR"
  sha256sum "$(basename "$ARCHIVE")" > "$(basename "$ARCHIVE").sha256"
)
cp "$ROOT_DIR/distribution/bootstrap.sh" "$OUT_DIR/llmproxy-bootstrap.sh"
(
  cd "$OUT_DIR"
  sha256sum llmproxy-bootstrap.sh > llmproxy-bootstrap.sh.sha256
)

printf 'Created release assets in %s\n' "$OUT_DIR"
