#!/usr/bin/env bash
set -euo pipefail

BUMP="${1:-patch}"
TAGS_FILE="${2:-}"

case "$BUMP" in
  patch|minor|major) ;;
  *)
    echo "Usage: next-release-version.sh [patch|minor|major] [tags-file]" >&2
    exit 2
    ;;
esac

if [[ -n "$TAGS_FILE" ]]; then
  [[ -f "$TAGS_FILE" ]] || { echo "Tags file not found: $TAGS_FILE" >&2; exit 2; }
  TAG_SOURCE=(cat "$TAGS_FILE")
else
  TAG_SOURCE=(git tag --list)
fi

latest="$(
  "${TAG_SOURCE[@]}"     | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$'     | sed 's/^v//'     | sort -V     | tail -n 1     || true
)"

if [[ -z "$latest" ]]; then
  printf '0.0.1\n'
  exit 0
fi

IFS=. read -r major minor patch <<< "$latest"
case "$BUMP" in
  patch) patch=$((patch + 1)) ;;
  minor) minor=$((minor + 1)); patch=0 ;;
  major) major=$((major + 1)); minor=0; patch=0 ;;
esac

printf '%d.%d.%d\n' "$major" "$minor" "$patch"
