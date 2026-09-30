#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${LLMPROXY_GITHUB_REPOSITORY:-KeyserDSoze/LlmProxy}"
VERSION=""
KEEP_TEMP=false
INSTALL_ARGS=()

usage() {
  cat <<'USAGE'
Usage: bootstrap.sh --version VERSION [installer options]

Downloads the immutable LlmProxy Linux release bundle, verifies its SHA-256,
and runs its installer. For the private repository authenticate with GitHub CLI
(`gh auth login`) or export GH_TOKEN/GITHUB_TOKEN.

Examples:
  bootstrap.sh --version 0.2.0-preview.8 --dgx-url http://10.0.0.21:8000 --provider-model Qwen/model
  bootstrap.sh --version 0.2.0-preview.8 --skip-docker-install --dgx-url http://host.docker.internal:8080 --provider-model qwen3-next-80b-1m

Environment:
  GH_TOKEN / GITHUB_TOKEN          GitHub token for private release downloads
  LLMPROXY_GITHUB_REPOSITORY       Repository override (default KeyserDSoze/LlmProxy)
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      VERSION="${2:?--version requires a value}"
      shift 2
      ;;
    --keep-temp)
      KEEP_TEMP=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      INSTALL_ARGS+=("$1")
      shift
      if [[ ${#INSTALL_ARGS[@]} -gt 0 ]]; then
        case "${INSTALL_ARGS[-1]}" in
          --install-dir|--ghcr-owner|--dgx-url|--provider-model)
            if [[ $# -eq 0 ]]; then
              echo "${INSTALL_ARGS[-1]} requires a value" >&2
              exit 2
            fi
            INSTALL_ARGS+=("$1")
            shift
            ;;
        esac
      fi
      ;;
  esac
done

if [[ -z "$VERSION" ]]; then
  echo "--version is required. Releases are intentionally explicit/immutable." >&2
  usage >&2
  exit 2
fi

TAG="v$VERSION"
ASSET="llmproxy-$VERSION-linux.tar.gz"
CHECKSUM="$ASSET.sha256"
TMP_DIR="$(mktemp -d)"
cleanup() {
  if [[ "$KEEP_TEMP" != true ]]; then
    rm -rf "$TMP_DIR"
  else
    echo "Temporary release files kept at: $TMP_DIR"
  fi
}
trap cleanup EXIT

if command -v gh >/dev/null 2>&1; then
  gh release download "$TAG" \
    --repo "$REPOSITORY" \
    --pattern "$ASSET" \
    --pattern "$CHECKSUM" \
    --dir "$TMP_DIR"
else
  TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
  BASE_URL="https://github.com/$REPOSITORY/releases/download/$TAG"
  CURL_ARGS=(--fail --silent --show-error --location)
  if [[ -n "$TOKEN" ]]; then
    CURL_ARGS+=(--header "Authorization: Bearer $TOKEN")
  fi
  curl "${CURL_ARGS[@]}" "$BASE_URL/$ASSET" -o "$TMP_DIR/$ASSET"
  curl "${CURL_ARGS[@]}" "$BASE_URL/$CHECKSUM" -o "$TMP_DIR/$CHECKSUM"
fi

(
  cd "$TMP_DIR"
  sha256sum -c "$CHECKSUM"
)

tar -xzf "$TMP_DIR/$ASSET" -C "$TMP_DIR"
BUNDLE_DIR="$TMP_DIR/llmproxy-$VERSION"
if [[ ! -x "$BUNDLE_DIR/distribution/install.sh" ]]; then
  chmod +x "$BUNDLE_DIR/distribution/install.sh" 2>/dev/null || true
fi

sudo -E bash "$BUNDLE_DIR/distribution/install.sh" "${INSTALL_ARGS[@]}"
