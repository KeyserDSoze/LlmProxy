#!/usr/bin/env bash
# Build per-release, password-encrypted first-install and update assets.
# GitHub Environments must supply all values as Secrets (never Variables).
set -Eeuo pipefail
set +x
umask 077

ENVIRONMENT="${1:?Usage: build-environment-installer.sh ENV VERSION OUTPUT_DIR}"
VERSION="${2:?Missing release version}"
OUTPUT_DIR="${3:?Missing output directory}"
[[ "$ENVIRONMENT" =~ ^[a-z][a-z0-9-]{0,30}$ ]] || { echo "Invalid environment name." >&2; exit 2; }
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid release version." >&2; exit 2; }

for required in ENTRA_TENANT_ID ENTRA_CLIENT_ID ENTRA_SUPER_ADMINS ENTRA_CLIENT_SECRET CLOUDFLARE_TUNNEL_TOKEN PASSWORD; do
  if [[ -z "${!required:-}" ]]; then
    echo "Missing GitHub environment Secret: $required" >&2
    exit 3
  fi
done
command -v gpg >/dev/null && command -v python3 >/dev/null && command -v sha256sum >/dev/null || {
  echo "Release runner requires gpg, python3 and sha256sum." >&2
  exit 2
}

mkdir -p "$OUTPUT_DIR"
OUT="$(cd "$OUTPUT_DIR" && pwd)"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

for ACTION in install update; do
  PLAINTEXT="$TEMP_DIR/$ACTION.sh"

  # shlex.quote prevents injected shell commands. Secrets are never passed
  # in argv, printed in workflow output, or published in plaintext.
  python3 - "$VERSION" "$ACTION" > "$PLAINTEXT" <<'PY'
import os
import shlex
import sys

version, action = sys.argv[1:]
keys = (
    "ENTRA_TENANT_ID", "ENTRA_CLIENT_ID", "ENTRA_SUPER_ADMINS",
    "ENTRA_CLIENT_SECRET", "CLOUDFLARE_TUNNEL_TOKEN",
)
print("#!/usr/bin/env bash")
print("set -Eeuo pipefail")
print("set +x")
print("umask 077")
print("export ENTRA_ENABLED=true")
for key in keys:
    value = os.environ[key]
    if any(char in value for char in (chr(0), chr(10), chr(13))):
        raise ValueError(f"Invalid line break in {key}")
    print(f"export {key}={shlex.quote(value)}")
print(f"VERSION={shlex.quote(version)}")
print(r'''
# sudo -E is not sufficient on hardened hosts: sudoers may discard custom
# credential variables. Re-create a permission-restricted export snapshot in
# the caller's private temporary directory, then source it *after* sudo.
# Bash's declare -p generates shell-escaped assignments, including backslashes
# and special characters, without putting any secrets on process argv.
persist_protected_environment() {
  local target="$1"
  (
    umask 077
    {
      declare -p ENTRA_ENABLED ENTRA_TENANT_ID ENTRA_CLIENT_ID \
        ENTRA_SUPER_ADMINS ENTRA_CLIENT_SECRET CLOUDFLARE_TUNNEL_TOKEN
      # Only the initial-install wizard exports these optional host port
      # overrides. Updates must not reset existing port selections.
      for optional in LLMPROXY_PORT GRAFANA_PORT; do
        if [[ "${!optional+x}" == x ]]; then
          declare -p "$optional"
        fi
      done
    } > "$target"
  )
  chmod 0600 "$target"
}
''')

if action == "install":
    # The first-install wizard must be included inside the encrypted payload.
    # The unauthenticated launcher and the update artifact never prompt for ports.
    from pathlib import Path
    print(Path("distribution/first-install-port-selection.sh").read_text(encoding="utf-8"))
    print(r'''
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
cd "$TMP_DIR"
BASE_URL="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${VERSION}"
curl -fsSL --retry 8 --retry-delay 2 --retry-all-errors "$BASE_URL/llmproxy-bootstrap.sh" -o llmproxy-bootstrap.sh
curl -fsSL --retry 8 --retry-delay 2 --retry-all-errors "$BASE_URL/llmproxy-bootstrap.sh.sha256" -o llmproxy-bootstrap.sh.sha256
sha256sum -c llmproxy-bootstrap.sh.sha256
persist_protected_environment "$TMP_DIR/privileged-environment.sh"
if [[ "$(id -u)" -eq 0 ]]; then
  bash -c 'set -Eeuo pipefail; source "$1"; shift; exec bash "$@"' \
    llmproxy-protected-bootstrap "$TMP_DIR/privileged-environment.sh" \
    "$TMP_DIR/llmproxy-bootstrap.sh" --version "$VERSION" --non-interactive
else
  sudo -E bash -c 'set -Eeuo pipefail; source "$1"; shift; exec bash "$@"' \
    llmproxy-protected-bootstrap "$TMP_DIR/privileged-environment.sh" \
    "$TMP_DIR/llmproxy-bootstrap.sh" --version "$VERSION" --non-interactive
fi
''')
elif action == "update":
    print(r'''
# llmproxyctl resolves every intermediate immutable release in order,
# preserves the database/volumes, and re-applies exported environment values
# through the normal production installer, recreating changed containers.
# Updating to the already-installed version re-applies the latest encrypted
# configuration from this release as well.
if ! command -v llmproxyctl >/dev/null 2>&1; then
  echo "LLMProxy is not installed; use the test.sh first-install launcher." >&2
  exit 4
fi
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
persist_protected_environment "$TMP_DIR/privileged-environment.sh"
LLMPROXYCTL="$(command -v llmproxyctl)"
if [[ "$(id -u)" -eq 0 ]]; then
  bash -c 'set -Eeuo pipefail; source "$1"; shift; exec "$@"' \
    llmproxy-protected-update "$TMP_DIR/privileged-environment.sh" \
    "$LLMPROXYCTL" update "$VERSION"
else
  sudo -E bash -c 'set -Eeuo pipefail; source "$1"; shift; exec "$@"' \
    llmproxy-protected-update "$TMP_DIR/privileged-environment.sh" \
    "$LLMPROXYCTL" update "$VERSION"
fi
''')
else:
    raise ValueError("Unknown installer action")
PY

  [[ "$ACTION" == install ]] && BASE="$ENVIRONMENT" || BASE="$ENVIRONMENT-update"
  LAUNCHER="$OUT/$BASE.sh"
  sed -e "s/@ENVIRONMENT@/$ENVIRONMENT/g" \
      -e "s/@VERSION@/$VERSION/g" \
      -e "s/@ACTION@/$ACTION/g" \
      distribution/environment-install-launcher.sh > "$LAUNCHER"
  chmod 0755 "$LAUNCHER"
  bash -n "$LAUNCHER"
  bash -n "$PLAINTEXT"

  ENCRYPTED="$OUT/$BASE.install.sh.gpg"
  gpg --batch --yes --quiet --pinentry-mode loopback \
    --cipher-algo AES256 --s2k-mode 3 --s2k-digest-algo SHA512 \
    --s2k-count 65011712 --compress-algo none --force-mdc \
    --passphrase-fd 3 --symmetric --output "$ENCRYPTED" "$PLAINTEXT" \
    3<<<"$PASSWORD"

  (
    cd "$OUT"
    sha256sum "$BASE.sh" > "$BASE.sh.sha256"
    sha256sum "$BASE.install.sh.gpg" > "$BASE.install.sh.gpg.sha256"
    sha256sum --check --status "$BASE.sh.sha256"
    sha256sum --check --status "$BASE.install.sh.gpg.sha256"
  )
done

echo "Prepared encrypted first-install and update assets for $ENVIRONMENT release $VERSION."
