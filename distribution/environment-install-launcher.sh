#!/usr/bin/env bash
# Pinned to the immutable release at publication time. No credentials live here.
set -Eeuo pipefail
set +x
umask 077

ENVIRONMENT="@ENVIRONMENT@"
VERSION="@VERSION@"
BASE_URL="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${VERSION}"
ASSET="${ENVIRONMENT}.install.sh.gpg"
TEMP_DIR="$(mktemp -d)"
PASSWORD=""
cleanup() {
  PASSWORD=""
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT HUP INT TERM

download() {
  curl --fail --silent --show-error --location --retry 8 --retry-delay 2 \
    --retry-all-errors --retry-max-time 300 --connect-timeout 15 \
    "$BASE_URL/$1" -o "$TEMP_DIR/$1"
}

if ! command -v curl >/dev/null 2>&1 || ! command -v sha256sum >/dev/null 2>&1; then
  echo "curl and sha256sum are required. Install curl and coreutils first." >&2
  exit 2
fi

if ! command -v gpg >/dev/null 2>&1; then
  echo "Installing the GnuPG dependency needed to decrypt the installer." >&2
  if command -v apt-get >/dev/null 2>&1; then
    if [[ "$(id -u)" -eq 0 ]]; then
      apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y gnupg
    else
      sudo apt-get update -qq && sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y gnupg
    fi
  elif command -v dnf >/dev/null 2>&1; then
    if [[ "$(id -u)" -eq 0 ]]; then dnf install -y gnupg2; else sudo dnf install -y gnupg2; fi
  elif command -v yum >/dev/null 2>&1; then
    if [[ "$(id -u)" -eq 0 ]]; then yum install -y gnupg2; else sudo yum install -y gnupg2; fi
  else
    echo "Install GnuPG (gpg) on this distribution and retry." >&2
    exit 2
  fi
fi

echo "LLMProxy ${VERSION} – protected ${ENVIRONMENT} installer"
download "$ASSET"
download "$ASSET.sha256"
(
  cd "$TEMP_DIR"
  sha256sum --check --status "$ASSET.sha256"
) || { echo "Release download integrity check failed." >&2; exit 3; }

if [[ ! -r /dev/tty ]]; then
  echo "An interactive terminal is required to enter the install password." >&2
  exit 4
fi
printf 'Password for environment %s: ' "$ENVIRONMENT" > /dev/tty
IFS= read -r -s PASSWORD < /dev/tty
printf '\n' > /dev/tty
if [[ -z "$PASSWORD" ]]; then
  echo "Empty password rejected." >&2
  exit 4
fi

# GPG completes integrity verification before we run any decrypted bytes.
if ! gpg --batch --yes --quiet --pinentry-mode loopback \
    --passphrase-fd 3 --output "$TEMP_DIR/install.sh" \
    --decrypt "$TEMP_DIR/$ASSET" 3<<<"$PASSWORD"; then
  echo "Wrong password or corrupted protected installer." >&2
  exit 5
fi
PASSWORD=""
chmod 0700 "$TEMP_DIR/install.sh"
bash -n "$TEMP_DIR/install.sh"
echo "Validated encrypted installer; starting LLMProxy ${VERSION} ..."
bash "$TEMP_DIR/install.sh"
