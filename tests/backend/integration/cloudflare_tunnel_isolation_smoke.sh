#!/usr/bin/env bash
# Simulated deployment lifecycle; no real containers or tunnels are modified.
set -Eeuo pipefail
ROOT="$(pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/mockbin" "$work/runtime"
log="$work/docker-commands.log"

cat > "$work/mockbin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$DOCKER_COMMAND_LOG"
if [[ "$1" != compose ]]; then
  echo "Unexpected host-level Docker command: $*" >&2
  exit 10
fi
shift
if [[ "$1" == version ]]; then exit 0; fi
[[ "$1" == --project-name && "$2" == llmproxy-full ]] || {
  echo "Docker Compose targeted a foreign project: $*" >&2
  exit 11
}
for arg in "$@"; do
  case "$arg" in
    --remove-orphans|down|prune)
      echo "Unsafe lifecycle command: $*" >&2
      exit 12 ;;
  esac
done
case "$*" in
  *" rm -sf cloudflared")
    [[ "$EXPECT_CLOUDFLARE_REMOVAL" == yes ]] || exit 13 ;;
esac
MOCK
cat > "$work/mockbin/curl" <<'MOCK'
#!/usr/bin/env bash
# The gateway readiness probe succeeds without a real service.
exit 0
MOCK
chmod 0755 "$work/mockbin/docker" "$work/mockbin/curl"

cat > "$work/production.env" <<'ENV'
POSTGRES_PASSWORD=ci-postgres
REDIS_PASSWORD=ci-redis
LLM_PROXY_API_KEY=ci-api-key
LLM_PROXY_API_KEY_PEPPER=ci-pepper
LLMPROXY_UPSTREAM_CREDENTIAL_KEY=ci-upstream-key
GRAFANA_ADMIN_PASSWORD=ci-grafana
ASPNETCORE_ENVIRONMENT=Production
ENTRA_ENABLED=true
ENTRA_TENANT_ID=ci-tenant
ENTRA_CLIENT_ID=ci-client
ENTRA_CLIENT_SECRET=ci-secret
BOOTSTRAP_ENABLED=false
LLMPROXY_PORT=18885
CLOUDFLARE_TUNNEL_TOKEN=ci-cloudflare
ENV

export PATH="$work/mockbin:$PATH"
export DOCKER_COMMAND_LOG="$log"
export LLMPROXY_DEPLOY_DIR="$work/runtime"
export LLMPROXY_ENV_FILE="$work/production.env"
export COMPOSE_PROJECT_NAME=unrelated-cloudflare-project
export EXPECT_CLOUDFLARE_REMOVAL=no

# Even an ambient foreign project name cannot redirect the deployment.
bash "$ROOT/docker/scripts/deploy.sh" smoke-version
grep -F -- '--project-name llmproxy-full' "$log" >/dev/null
grep -F -- '--profile cloudflare up -d --no-build' "$log" >/dev/null
! grep -E -- '(^| )(stop|down|prune)( |$)|--remove-orphans|(^| )rm -sf cloudflared' "$log"

# Removing LLMProxy's own token removes only its explicitly scoped service.
sed -i 's/^CLOUDFLARE_TUNNEL_TOKEN=.*/CLOUDFLARE_TUNNEL_TOKEN=/' "$LLMPROXY_ENV_FILE"
export EXPECT_CLOUDFLARE_REMOVAL=yes
: > "$log"
bash "$ROOT/docker/scripts/deploy.sh" smoke-version
grep -F -- '--project-name llmproxy-full' "$log" >/dev/null
grep -F -- '--profile cloudflare rm -sf cloudflared' "$log" >/dev/null
! grep -E -- '(^| )(stop|down|prune)( |$)|--remove-orphans' "$log"

echo "Independent Cloudflare tunnels and other Compose projects remain untouched."
