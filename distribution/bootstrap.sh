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
(`gh auth login`) or export a GitHub token with repository-read and package-read access.

Examples:
  bootstrap.sh --version 0.0.1 --dgx-url http://10.0.0.21:8000 --provider-model Qwen/model
  DGX_UPSTREAM_BEARER_TOKEN=llama-local bootstrap.sh --version 0.0.1 --skip-docker-install --dgx-url http://host.docker.internal:8080 --provider-model qwen3-next-80b-1m

Environment:
  GH_TOKEN / GITHUB_TOKEN          GitHub token for private release downloads
  GHCR_USER / GHCR_TOKEN           Optional explicit GHCR credentials; defaults can be derived from GitHub auth
  ENTRA_ENABLED / ENTRA_TENANT_ID / ENTRA_CLIENT_ID / ENTRA_CLIENT_SECRET
                                      Required for ASPNETCORE_ENVIRONMENT=Production
  DGX_UPSTREAM_BEARER_TOKEN        Optional one-time upstream llama.cpp/vLLM bearer for first install
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
    --install-dir)
      echo "Release installations use the canonical /opt/llmproxy layout; --install-dir is not supported." >&2
      exit 2
      ;;
    *)
      INSTALL_ARGS+=("$1")
      shift
      if [[ ${#INSTALL_ARGS[@]} -gt 0 ]]; then
        case "${INSTALL_ARGS[-1]}" in
          --ghcr-owner|--dgx-url|--provider-model)
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

TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"

if command -v gh >/dev/null 2>&1; then
  gh release download "$TAG" \
    --repo "$REPOSITORY" \
    --pattern "$ASSET" \
    --pattern "$CHECKSUM" \
    --dir "$TMP_DIR"

  if [[ -z "${GHCR_TOKEN:-}" ]]; then
    export GHCR_TOKEN="$(gh auth token 2>/dev/null || true)"
  fi
  if [[ -z "${GHCR_USER:-}" ]]; then
    export GHCR_USER="$(gh api user --jq .login 2>/dev/null || true)"
  fi
elif [[ -n "$TOKEN" ]]; then
  RELEASE_JSON="$TMP_DIR/release.json"
  curl --fail --silent --show-error \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $TOKEN" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    "https://api.github.com/repos/$REPOSITORY/releases/tags/$TAG" \
    -o "$RELEASE_JSON"

  resolve_asset_url() {
    local name="$1"
    if command -v jq >/dev/null 2>&1; then
      jq -r --arg name "$name" '.assets[] | select(.name == $name) | .url' "$RELEASE_JSON" | head -n 1
    elif command -v python3 >/dev/null 2>&1; then
      python3 - "$RELEASE_JSON" "$name" <<'PYASSET'
import json, sys
with open(sys.argv[1], encoding="utf-8") as handle:
    release = json.load(handle)
for asset in release.get("assets", []):
    if asset.get("name") == sys.argv[2]:
        print(asset.get("url", ""))
        break
PYASSET
    else
      echo "Private release download requires GitHub CLI, jq or python3 to resolve release assets." >&2
      return 1
    fi
  }

  download_private_asset() {
    local name="$1"
    local url
    url="$(resolve_asset_url "$name")"
    if [[ -z "$url" ]]; then
      echo "Release asset not found: $name in $TAG" >&2
      exit 3
    fi
    curl --fail --silent --show-error --location \
      -H "Accept: application/octet-stream" \
      -H "Authorization: Bearer $TOKEN" \
      -H "X-GitHub-Api-Version: 2022-11-28" \
      "$url" -o "$TMP_DIR/$name"
  }

  download_private_asset "$ASSET"
  download_private_asset "$CHECKSUM"

  if [[ -z "${GHCR_TOKEN:-}" ]]; then
    export GHCR_TOKEN="$TOKEN"
  fi
  if [[ -z "${GHCR_USER:-}" ]]; then
    if command -v jq >/dev/null 2>&1; then
      export GHCR_USER="$(curl --fail --silent --show-error -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" https://api.github.com/user | jq -r .login)"
    elif command -v python3 >/dev/null 2>&1; then
      export GHCR_USER="$(curl --fail --silent --show-error -H "Authorization: Bearer $TOKEN" -H "Accept: application/vnd.github+json" https://api.github.com/user | python3 -c 'import json,sys; print(json.load(sys.stdin).get("login", ""))')"
    fi
  fi
else
  BASE_URL="https://github.com/$REPOSITORY/releases/download/$TAG"
  curl --fail --silent --show-error --location "$BASE_URL/$ASSET" -o "$TMP_DIR/$ASSET"
  curl --fail --silent --show-error --location "$BASE_URL/$CHECKSUM" -o "$TMP_DIR/$CHECKSUM"
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

if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
  bash "$BUNDLE_DIR/distribution/install.sh" "${INSTALL_ARGS[@]}"
else
  if ! command -v sudo >/dev/null 2>&1; then
    echo "Root privileges are required to install LlmProxy and sudo is not available." >&2
    echo "Re-run the bootstrap as root." >&2
    exit 4
  fi
  sudo -E bash "$BUNDLE_DIR/distribution/install.sh" "${INSTALL_ARGS[@]}"
fi
