#!/usr/bin/env bash
set -euo pipefail
version="${1:-}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid version." >&2; exit 2; }
[[ "$(id -u)" -eq 0 ]] || { echo "Root required." >&2; exit 2; }
state_file=/var/lib/llmproxy-node-agent/update-status.json
mkdir -p /var/lib/llmproxy-node-agent
chmod 0700 /var/lib/llmproxy-node-agent
# Forward only a fixed error code and the last meaningful stage to the Gateway.
current_stage=preparing
failure_code=preparation_failed
backup_taken=false
write_state() {
  local status="$1" stage="${2:-$current_stage}" percent="${3:-}" error_code="${4:-}"
  local status_tmp="${state_file}.tmp"
  if [[ "$percent" =~ ^[0-9]+$ ]] && (( percent <= 100 )); then
    printf '{"status":"%s","version":"%s","stage":"%s","percent":%s}\n' \
      "$status" "$version" "$stage" "$percent" > "$status_tmp"
  elif [[ -n "$error_code" ]]; then
    printf '{"status":"%s","version":"%s","stage":"%s","errorCode":"%s"}\n' \
      "$status" "$version" "$stage" "$error_code" > "$status_tmp"
  else
    printf '{"status":"%s","version":"%s","stage":"%s"}\n' \
      "$status" "$version" "$stage" > "$status_tmp"
  fi
  chmod 0600 "$status_tmp"
  mv -f "$status_tmp" "$state_file"
}
advance() {
  current_stage="$1"
  failure_code="$2"
  write_state running "$current_stage" "${3:-}"
}
cleanup() {
  local result=$?
  trap - EXIT
  if (( result != 0 )); then
    if [[ "$backup_taken" == true ]]; then
      # Recover the previous binary even if mv or daemon-reload failed mid-swap.
      systemctl stop llmproxy-node-agent || true
      if [[ -d /opt/llmproxy-node-agent.previous ]]; then
        rm -rf /opt/llmproxy-node-agent
        if mv /opt/llmproxy-node-agent.previous /opt/llmproxy-node-agent; then
          systemctl daemon-reload || true
          if ! systemctl start llmproxy-node-agent; then
            failure_code=rollback_start_failed
          fi
        else
          failure_code=rollback_restore_failed
        fi
      else
        failure_code=rollback_missing
      fi
    fi
    write_state failed "$current_stage" "" "$failure_code"
    echo "[llmproxy-agent-update] failed during $current_stage ($failure_code); exit=$result" >&2
  fi
  if [[ -n "${tmp:-}" && -d "$tmp" ]]; then rm -rf "$tmp"; fi
  if (( result != 0 )); then exit "$result"; fi
}
trap cleanup EXIT
advance preparing prerequisites_missing
for cmd in curl tar sha256sum systemctl; do command -v "$cmd" >/dev/null; done
case "$(uname -m)" in
  x86_64) rid="linux-x64";;
  aarch64|arm64) rid="linux-arm64";;
  *) exit 2;;
esac
tmp="$(mktemp -d)"
asset="llmproxy-node-agent-${version}-${rid}.tar.gz"
url="https://github.com/KeyserDSoze/LlmProxy/releases/download/v${version}/${asset}"
mkdir "$tmp/archive" "$tmp/new"
# Try to learn the immutable archive total from the final HTTP response headers.
# Some registries omit Content-Length: keep progress indeterminate in that case.
total_bytes="$(curl --retry 2 --connect-timeout 8 --max-time 20 -fsSIL "$url" 2>/dev/null |
  tr -d '\r' | awk 'tolower($1)=="content-length:" {n=$2} END {print n}' || true)"
[[ "$total_bytes" =~ ^[1-9][0-9]*$ ]] || total_bytes=''
advance downloading archive_download_failed
curl --retry 4 -fsSL "$url" -o "$tmp/archive/$asset" &
download_pid=$!
while kill -0 "$download_pid" 2>/dev/null; do
  if [[ -n "$total_bytes" && -f "$tmp/archive/$asset" ]]; then
    downloaded="$(stat -c '%s' "$tmp/archive/$asset" 2>/dev/null || echo 0)"
    if [[ "$downloaded" =~ ^[0-9]+$ ]]; then
      percent="$(( 100 * downloaded / total_bytes ))"
      (( percent > 100 )) && percent=100
      write_state running downloading "$percent"
    fi
  fi
  sleep 2
done
wait "$download_pid"
advance verifying checksum_download_failed
curl --retry 4 -fsSL "$url.sha256" -o "$tmp/archive/$asset.sha256"
failure_code=checksum_mismatch
# Release checksums contain the dist/ asset prefix; verify with the same layout.
mkdir -p "$tmp/dist"
cp "$tmp/archive/$asset" "$tmp/dist/$asset"
cp "$tmp/archive/$asset.sha256" "$tmp/dist/$asset.sha256"
(cd "$tmp" && sha256sum -c "dist/$asset.sha256")
advance extracting archive_invalid
tar -xzf "$tmp/archive/$asset" -C "$tmp/new"
test -x "$tmp/new/LlmProxy.NodeAgent"
test -f "$tmp/new/update-node-agent.sh"
install -m 0755 "$tmp/new/update-node-agent.sh" "$tmp/new/update-node-agent.sh.tmp"
mv "$tmp/new/update-node-agent.sh.tmp" "$tmp/new/update-node-agent.sh"
service=llmproxy-node-agent
old=/opt/llmproxy-node-agent
backup="/opt/llmproxy-node-agent.previous"
staging="/opt/llmproxy-node-agent.upgrade"
test -d "$old"
# An aborted prior update can leave staging behind; the update unit is
# serialized, so clear only this dedicated transient staging directory.
if [[ -e "$staging" ]]; then rm -rf "$staging"; fi
failure_code=staging_failed
cp -a "$tmp/new" "$staging"
advance switching agent_stop_failed
systemctl stop "$service"
failure_code=backup_failed
rm -rf "$backup"
mv "$old" "$backup"
backup_taken=true
failure_code=activate_binary_failed
mv "$staging" "$old"
failure_code=daemon_reload_failed
systemctl daemon-reload
advance healthcheck service_start_failed
systemctl start "$service"
failure_code=healthcheck_failed

# A successful outbound heartbeat or 200 from an old runtime is NOT
# proof that the new local Agent binary has started successfully.
health_header="$tmp/agent-health.header"
agent_bearer="$(sed -n 's/^NodeAgent__BearerToken=//p' /etc/llmproxy/node-agent.env | tail -n 1)"
if [[ -n "$agent_bearer" ]]; then
  # BearerTokenMiddleware accepts non-empty credentials including punctuation;
  # do not reject valid passwords using an unrelated restricted alphabet.
  # Control characters are forbidden in HTTP headers.
  if [[ "$agent_bearer" == *$'\r'* || "$agent_bearer" == *$'\n'* ||
        "$agent_bearer" =~ [[:cntrl:]] ]]; then
    failure_code=invalid_health_bearer
    exit 1
  fi
  printf 'Authorization: Bearer %s\n' "$agent_bearer" > "$health_header"
  chmod 0600 "$health_header"
  health_auth=(--header "@$health_header")
else
  health_auth=()
fi
unset agent_bearer

# Respect the configured listener instead of assuming that every Agent binds
# :9900. All checks target loopback; never probe arbitrary remote addresses.
listen_url="$(sed -n 's/^ASPNETCORE_URLS=//p' /etc/llmproxy/node-agent.env | tail -n 1)"
health_port=9900
if [[ -n "$listen_url" ]]; then
  case "$listen_url" in
    http://127.0.0.1:*|http://localhost:*|http://0.0.0.0:*|http://+:*|http://\*:*)
      health_port="$(echo "$listen_url" | sed -n 's/.*://p')"
      ;;
    *)
      failure_code=health_listen_address_invalid
      echo "[llmproxy-agent-update] unsupported listen URL: expected a single HTTP listener" >&2
      exit 1
      ;;
  esac
fi
if ! [[ "$health_port" =~ ^[0-9]{1,5}$ ]] || (( 10#$health_port < 1 || 10#$health_port > 65535 )); then
  failure_code=health_listen_address_invalid
  exit 1
fi

healthy=0
for attempt in $(seq 1 60); do
  status="$(curl --silent --show-error "${health_auth[@]}" --output "$tmp/health.json" \
    --max-time 3 --write-out '%{http_code}' "http://127.0.0.1:$health_port/health" || true)"
  if ! systemctl is-active --quiet "$service"; then
    failure_code=health_service_inactive
    healthy=0
  elif [[ "$status" == "000" || -z "$status" ]]; then
    failure_code=health_unreachable
    healthy=0
  elif [[ "$status" == "401" || "$status" == "403" ]]; then
    failure_code=health_unauthorized
    healthy=0
  elif [[ "$status" != "200" ]]; then
    failure_code=health_http_error
    healthy=0
  elif ! grep -Eq '"service"[[:space:]]*:[[:space:]]*"llmproxy-node-agent"' "$tmp/health.json"; then
    failure_code=health_wrong_service
    healthy=0
  elif ! grep -Eq '"status"[[:space:]]*:[[:space:]]*"ok"' "$tmp/health.json"; then
    failure_code=health_not_ready
    healthy=0
  elif ! grep -Fq "\"version\":\"$version\"" "$tmp/health.json" &&
       ! grep -Eq '"version"[[:space:]]*:[[:space:]]*"'"${version//./\\.}"'"[[:space:]]*[,}]' "$tmp/health.json"; then
    failure_code=health_version_mismatch
    healthy=0
  else
    healthy=$((healthy + 1))
    if (( healthy >= 3 )); then
      backup_taken=false
      write_state succeeded completed 100
      echo "Node Agent upgraded and verified to $version"
      exit 0
    fi
  fi
  if (( attempt == 1 || attempt % 10 == 0 )); then
    echo "[llmproxy-agent-update] health attempt $attempt/60, http=$status, cause=$failure_code" >&2
  fi
  sleep 2
done
echo "New Node Agent did not pass local authenticated /health (last cause=$failure_code); restoring previous installation." >&2
exit 1
