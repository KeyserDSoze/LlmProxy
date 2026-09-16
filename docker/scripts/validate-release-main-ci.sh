#!/usr/bin/env bash
set -euo pipefail

EXPECTED_SHA="${1:-}"
RUNS_JSON="${2:-/dev/stdin}"

if [[ ! "$EXPECTED_SHA" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Expected source SHA must be a 40-character lowercase hexadecimal commit id." >&2
  exit 2
fi

if [[ "$RUNS_JSON" != "/dev/stdin" && ! -f "$RUNS_JSON" ]]; then
  echo "Workflow-runs JSON file not found: $RUNS_JSON" >&2
  exit 2
fi

jq -er --arg sha "$EXPECTED_SHA" '
  [
    .workflow_runs[]?
    | select(
        .name == "CI"
        and .event == "push"
        and .head_branch == "main"
        and .head_sha == $sha
        and .conclusion == "success"
      )
    | { id: .id }
  ] as $matches
  | if ($matches | length) == 0 then
      error("no successful main CI run validates source SHA " + $sha)
    else
      ($matches | max_by(.id) | .id | tostring)
    end
' "$RUNS_JSON"
