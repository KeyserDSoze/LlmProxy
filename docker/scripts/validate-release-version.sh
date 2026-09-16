#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

expected_version="${1:-}"

product_version="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>[[:space:]]*$/\1/p' Directory.Build.props | head -n 1)"
admin_version="$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)",[[:space:]]*$/\1/p' src/LlmProxy.Admin/package.json | head -n 1)"

if [[ -z "$product_version" ]]; then
  echo "Could not resolve <Version> from Directory.Build.props." >&2
  exit 1
fi

if [[ ! "$product_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "Product version '$product_version' is not valid SemVer." >&2
  exit 1
fi

if [[ -z "$admin_version" ]]; then
  echo "Could not resolve Admin package version." >&2
  exit 1
fi

if [[ "$admin_version" != "$product_version" ]]; then
  echo "Version mismatch: Directory.Build.props=$product_version, Admin package=$admin_version." >&2
  exit 1
fi

if ! grep -Fq "## [$product_version]" CHANGELOG.md; then
  echo "CHANGELOG.md does not contain a release section for $product_version." >&2
  exit 1
fi

if [[ -n "$expected_version" && "$expected_version" != "$product_version" ]]; then
  echo "Release tag/version mismatch: tag expects '$expected_version' but product compiles as '$product_version'." >&2
  exit 1
fi

printf '%s\n' "$product_version"
