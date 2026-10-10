#!/usr/bin/env bash
# Used exclusively on the GitHub release runner. Never store plaintext assets.
set -Eeuo pipefail
set +x
umask 077

ENVIRONMENT="${1:?Usage: build-environment-installer.sh ENV VERSION OUTPUT_DIR}"
VERSION="${2:?Missing release version}"
OUTPUT_DIR="${3:?Missing output directory}"
[[ "$ENVIRONMENT" =~ ^[a-z][a-z0-9-]{0,30}$ ]] || { echo "Invalid environment name." >&2; exit 2; }
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid release version." >&2; exit 2; }
for required in ENTRA_TENANT_ID ENTRA_CLIENT_ID ENTRA_SUPER_ADMINS ENTRA_CLIENT_SECRET CLOUDFLARE_TUNNEL_TOKEN INSTALL_PASSWORD; do
  if [[ -z "${!required:-}" ]]; then
    echo "Missing GitHub environment setting: $required" >&2
    exit 3
  fi
done
if [[ "${#INSTALL_PASSWORD}" -lt 20 ]]; then
  echo "GitHub environment secret password must be at least 20 characters (use a long random passphrase)." >&2
  exit 3
fi
command -v gpg >/dev/null && command -v python3 >/dev/null && command -v sha256sum >/dev/null || {
  echo "Release runner requires gpg, python3 and sha256sum." >&2
  exit 2
}
mkdir -p "$OUTPUT_DIR"
OUT="$(cd "$OUTPUT_DIR" && pwd)"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT
PLAINTEXT="$TEMP_DIR/install.sh"

# shlex.quote prevents environment input from injecting shell commands.
# No values are passed on argv and the plaintext never enters the release assets.
python3 - "$VERSION" > "$PLAINTEXT" <<'PY'
import os
import shlex
import sys

version = sys.argv[1]
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
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
cd "$TMP_DIR"
BASE_URL="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${VERSION}"
curl -fsSL --retry 8 --retry-delay 2 --retry-all-errors "$BASE_URL/llmproxy-bootstrap.sh" -o llmproxy-bootstrap.sh
curl -fsSL --retry 8 --retry-delay 2 --retry-all-errors "$BASE_URL/llmproxy-bootstrap.sh.sha256" -o llmproxy-bootstrap.sh.sha256
sha256sum -c llmproxy-bootstrap.sh.sha256
if [[ "$(id -u)" -eq 0 ]]; then
  bash llmproxy-bootstrap.sh --version "$VERSION" --non-interactive
else
  sudo -E bash llmproxy-bootstrap.sh --version "$VERSION" --non-interactive
fi
''')
PY

LAUNCHER="$OUT/$ENVIRONMENT.sh"
sed -e "s/@ENVIRONMENT@/$ENVIRONMENT/g" -e "s/@VERSION@/$VERSION/g" \
  distribution/environment-install-launcher.sh > "$LAUNCHER"
chmod 0755 "$LAUNCHER"
bash -n "$LAUNCHER"
bash -n "$PLAINTEXT"

ENCRYPTED="$OUT/$ENVIRONMENT.install.sh.gpg"
gpg --batch --yes --quiet --pinentry-mode loopback \
  --cipher-algo AES256 --s2k-mode 3 --s2k-digest-algo SHA512 \
  --s2k-count 65011712 --compress-algo none --force-mdc \
  --passphrase-fd 3 --symmetric --output "$ENCRYPTED" "$PLAINTEXT" \
  3<<<"$INSTALL_PASSWORD"

(
  cd "$OUT"
  sha256sum "$ENVIRONMENT.sh" > "$ENVIRONMENT.sh.sha256"
  sha256sum "$ENVIRONMENT.install.sh.gpg" > "$ENVIRONMENT.install.sh.gpg.sha256"
  sha256sum --check --status "$ENVIRONMENT.sh.sha256"
  sha256sum --check --status "$ENVIRONMENT.install.sh.gpg.sha256"
)
echo "Prepared authenticated encrypted installation assets for $ENVIRONMENT release $VERSION."
