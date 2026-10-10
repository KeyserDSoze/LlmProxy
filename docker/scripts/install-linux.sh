#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_DIR="${LLMPROXY_INSTALL_DIR:-/opt/llmproxy}"
ENV_FILE=""
IMAGE_TAG="${LLMPROXY_IMAGE_TAG:-main}"
GHCR_OWNER_VALUE="${GHCR_OWNER:-keyserdsoze}"
INFERENCE_NODE_URL_VALUE="${INFERENCE_NODE_BASE_ADDRESS:-}"
PROVIDER_MODEL_VALUE="${PROVIDER_MODEL_NAME:-}"
SUPER_ADMINS_VALUE=""
SUPER_ADMINS_PROVIDED=false
if [[ "${ENTRA_SUPER_ADMINS+x}" == "x" ]]; then
  SUPER_ADMINS_VALUE="${ENTRA_SUPER_ADMINS}"
  SUPER_ADMINS_PROVIDED=true
fi
COMPOSE_VERSION="${DOCKER_COMPOSE_VERSION:-v5.5.0}"
PREPARE_ONLY=false
SKIP_NODE_CHECK=false
SKIP_DOCKER_INSTALL=false
NON_INTERACTIVE=false
VALIDATE_ONLY=false

usage() {
  cat <<'EOF'
Usage: sudo -E bash docker/scripts/install-linux.sh [options]

Installs/prepares a Linux production host and deploys the supported LlmProxy full stack.
Run this script from a LlmProxy repository checkout.

Options:
  --install-dir DIR       Persistent deployment directory (default: /opt/llmproxy)
  --image-tag TAG         GHCR image tag to deploy (default: main)
  --ghcr-owner OWNER      GHCR owner (default: keyserdsoze)
  --node-url URL           Initial inference node/vLLM service root
  --dgx-url URL            Deprecated alias for --node-url (upgrade compatibility)
  --provider-model MODEL  Exact provider model id exposed by vLLM
  --super-admins USERS    Comma/semicolon-separated Entra principals granted LlmProxy.Admin
  --prepare-only          Install host prerequisites and create config, but do not deploy
  --skip-node-check        Skip /health and /v1/models checks against the initial inference node
  --skip-dgx-check         Deprecated alias for --skip-node-check (upgrade compatibility)
  --skip-docker-install   Require Docker + Compose to already be installed
  --non-interactive       Never prompt; required values must be supplied by flags/env/file
  --validate-only         Validate installer/repository compatibility without changing host
  -h, --help              Show this help

Optional environment variables:
  GHCR_USER / GHCR_TOKEN            Login to a private GHCR package without storing the token
  ENTRA_ENABLED / ENTRA_TENANT_ID / ENTRA_CLIENT_ID / ENTRA_CLIENT_SECRET
  ENTRA_SUPER_ADMINS                  Optional admin principals; same semantics as --super-admins
  INFERENCE_NODE_UPSTREAM_BEARER_TOKEN        Optional one-time llama.cpp/vLLM bearer; never written to .env by the installer
  CLOUDFLARE_TUNNEL_TOKEN           Enables the Cloudflare profile only when Entra is enabled
  DOCKER_COMPOSE_VERSION            Manual Compose fallback version (default: v5.5.0)

The installer supports Docker's official repositories on Debian, Ubuntu, Fedora,
CentOS and RHEL. For derivative/other distributions it can use common distro
package managers (apt, dnf/yum, zypper, pacman, apk) and installs the Compose
CLI plugin manually when needed. If Docker is already present, it is preserved.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-dir)
      INSTALL_DIR="${2:?--install-dir requires a value}"
      shift 2
      ;;
    --image-tag)
      IMAGE_TAG="${2:?--image-tag requires a value}"
      shift 2
      ;;
    --ghcr-owner)
      GHCR_OWNER_VALUE="${2:?--ghcr-owner requires a value}"
      shift 2
      ;;
    --node-url|--dgx-url)
      INFERENCE_NODE_URL_VALUE="${2:?$1 requires a value}"
      if [[ "$1" == "--dgx-url" ]]; then
        echo "Warning: --dgx-url is deprecated; use --node-url." >&2
      fi
      shift 2
      ;;
    --provider-model)
      PROVIDER_MODEL_VALUE="${2:?--provider-model requires a value}"
      shift 2
      ;;
    --super-admins)
      if [[ $# -lt 2 ]]; then
        echo "--super-admins requires a value (use an empty quoted value to clear the list)." >&2
        exit 2
      fi
      SUPER_ADMINS_VALUE="$2"
      SUPER_ADMINS_PROVIDED=true
      shift 2
      ;;
    --prepare-only)
      PREPARE_ONLY=true
      shift
      ;;
    --skip-node-check|--skip-dgx-check)
      if [[ "$1" == "--skip-dgx-check" ]]; then
        echo "Warning: --skip-dgx-check is deprecated; use --skip-node-check." >&2
      fi
      SKIP_NODE_CHECK=true
      shift
      ;;
    --skip-docker-install)
      SKIP_DOCKER_INSTALL=true
      shift
      ;;
    --non-interactive)
      NON_INTERACTIVE=true
      shift
      ;;
    --validate-only)
      VALIDATE_ONLY=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

ENV_FILE="${LLMPROXY_ENV_FILE:-$INSTALL_DIR/.env}"

require_source_asset() {
  local path="$1"
  if [[ ! -e "$ROOT_DIR/$path" ]]; then
    echo "Required repository asset is missing: $path" >&2
    echo "Run this installer from a complete LlmProxy repository checkout." >&2
    exit 3
  fi
}

for asset in \
  docker/docker-compose.full.yml \
  docker/.env.production.example \
  docker/scripts/deploy.sh \
  docker/observability; do
  require_source_asset "$asset"
done

if [[ ! -r /etc/os-release ]]; then
  echo "/etc/os-release is required to identify the Linux distribution." >&2
  exit 3
fi

# shellcheck disable=SC1091
. /etc/os-release
DISTRO_ID="${ID:-unknown}"
DISTRO_LIKE="${ID_LIKE:-}"
DISTRO_NAME="${PRETTY_NAME:-$DISTRO_ID}"
ARCH="$(uname -m)"

if [[ "$VALIDATE_ONLY" == "true" ]]; then
  bash -n "$ROOT_DIR/docker/scripts/install-linux.sh"
  bash -n "$ROOT_DIR/docker/scripts/deploy.sh"
  printf 'Installer validation OK. distro=%s arch=%s install_dir=%s\n' "$DISTRO_ID" "$ARCH" "$INSTALL_DIR"
  exit 0
fi

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  cat >&2 <<EOF
Root privileges are required to install packages and prepare $INSTALL_DIR.
Run:
  sudo -E bash docker/scripts/install-linux.sh ...
EOF
  exit 4
fi

CALLER_USER="${SUDO_USER:-root}"
CALLER_GROUP="$(id -gn "$CALLER_USER" 2>/dev/null || printf 'root')"

LOG_DIR="${LLMPROXY_LOG_DIR:-/var/log/llmproxy}"
INSTALL_STARTED_AT="$(date -u +%Y%m%dT%H%M%SZ)"
INSTALL_LOG="$LOG_DIR/install-$INSTALL_STARTED_AT.log"
CURRENT_STAGE="initialization"

install -d -m 0750 "$LOG_DIR"
touch "$INSTALL_LOG"
chmod 0640 "$INSTALL_LOG"
ln -sfn "$INSTALL_LOG" "$LOG_DIR/latest-install.log"

if command -v tee >/dev/null 2>&1; then
  exec > >(tee -a "$INSTALL_LOG") 2>&1
else
  exec >>"$INSTALL_LOG" 2>&1
fi

timestamp() {
  date -u '+%Y-%m-%dT%H:%M:%SZ'
}

log() {
  printf '%s [llmproxy-install] %s\n' "$(timestamp)" "$*"
}

warn() {
  printf '%s [llmproxy-install] WARNING: %s\n' "$(timestamp)" "$*" >&2
}

stage() {
  local number="$1"
  local title="$2"
  CURRENT_STAGE="$title"
  printf '\n%s [llmproxy-install] ==> [%s/8] %s\n' "$(timestamp)" "$number" "$title"
}

installation_exit() {
  local rc=$?
  if [[ "$rc" -eq 0 ]]; then
    return 0
  fi

  {
    printf '\n============================================================\n'
    printf 'LlmProxy installation FAILED\n'
    printf 'Stage: %s\n' "$CURRENT_STAGE"
    printf 'Exit code: %s\n' "$rc"
    printf 'Persistent log: %s\n' "$INSTALL_LOG"
    printf 'Latest log link: %s/latest-install.log\n' "$LOG_DIR"
    printf '============================================================\n'
  } >&2

  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    printf '\nDocker container snapshot at failure:\n' >&2
    docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}' >&2 || true
  fi
}
trap installation_exit EXIT

log "Installation log: $INSTALL_LOG"
log "Target directory: $INSTALL_DIR"
log "Requested image tag: $IMAGE_TAG"

have() {
  command -v "$1" >/dev/null 2>&1
}

pkg_manager() {
  local manager
  for manager in apt-get dnf yum zypper pacman apk; do
    if have "$manager"; then
      printf '%s\n' "$manager"
      return 0
    fi
  done
  return 1
}

install_prerequisites() {
  local manager
  manager="$(pkg_manager || true)"
  if [[ -z "$manager" ]]; then
    for cmd in curl git openssl wget; do
      if ! have "$cmd"; then
        echo "No supported package manager found and prerequisite '$cmd' is missing." >&2
        exit 5
      fi
    done
    return 0
  fi

  log "Installing host prerequisites with $manager on $DISTRO_NAME"
  case "$manager" in
    apt-get)
      apt-get update
      DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl git gnupg jq openssl wget
      ;;
    dnf)
      dnf install -y ca-certificates curl git jq openssl wget
      ;;
    yum)
      yum install -y ca-certificates curl git jq openssl wget
      ;;
    zypper)
      zypper --non-interactive refresh
      zypper --non-interactive install ca-certificates curl git jq openssl wget
      ;;
    pacman)
      pacman -Sy --noconfirm --needed ca-certificates curl git jq openssl wget
      ;;
    apk)
      apk add --no-cache ca-certificates curl git jq openssl wget
      ;;
  esac
}

apt_repo_suite() {
  local repo_distro="$1"
  if [[ "$repo_distro" == "ubuntu" ]]; then
    printf '%s\n' "${LLMPROXY_DOCKER_REPO_CODENAME:-${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}}"
  else
    printf '%s\n' "${LLMPROXY_DOCKER_REPO_CODENAME:-${DEBIAN_CODENAME:-${VERSION_CODENAME:-}}}"
  fi
}

install_docker_apt_official() {
  local repo_distro="$1"
  local suite
  suite="$(apt_repo_suite "$repo_distro")"
  if [[ -z "$suite" ]]; then
    echo "Cannot determine Docker repository codename for $DISTRO_NAME." >&2
    echo "Set LLMPROXY_DOCKER_REPO_CODENAME explicitly or pre-install Docker." >&2
    exit 6
  fi

  log "Configuring Docker official $repo_distro repository ($suite)"
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/$repo_distro/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/$repo_distro
Suites: $suite
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y \
    docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
}

install_docker_rpm_official() {
  local repo_distro="$1"
  local manager
  manager="$(pkg_manager)"
  if [[ "$manager" != "dnf" && "$manager" != "yum" ]]; then
    echo "Expected dnf/yum for Docker $repo_distro packages." >&2
    exit 6
  fi
  log "Configuring Docker official $repo_distro repository"
  install -d -m 0755 /etc/yum.repos.d
  curl -fsSL "https://download.docker.com/linux/$repo_distro/docker-ce.repo" -o /etc/yum.repos.d/docker-ce.repo
  "$manager" install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
}

install_docker_from_distribution() {
  local manager
  manager="$(pkg_manager || true)"
  warn "Docker does not publish a verified installation recipe for '$DISTRO_ID' in this installer; using distribution packages."
  case "$manager" in
    apt-get)
      DEBIAN_FRONTEND=noninteractive apt-get install -y docker.io
      ;;
    dnf)
      dnf install -y docker || dnf install -y moby-engine
      ;;
    yum)
      yum install -y docker || yum install -y moby-engine
      ;;
    zypper)
      zypper --non-interactive install docker
      ;;
    pacman)
      pacman -S --noconfirm --needed docker
      ;;
    apk)
      apk add --no-cache docker
      ;;
    *)
      echo "Docker is not installed and no supported package manager was found." >&2
      echo "Install Docker Engine manually, then rerun with --skip-docker-install." >&2
      exit 6
      ;;
  esac
}

install_compose_plugin_fallback() {
  if docker compose version >/dev/null 2>&1; then
    return 0
  fi

  local compose_arch
  case "$ARCH" in
    x86_64|amd64) compose_arch="x86_64" ;;
    aarch64|arm64) compose_arch="aarch64" ;;
    *)
      echo "Docker Compose plugin is missing and architecture '$ARCH' has no installer mapping." >&2
      echo "Install Docker Compose v2 manually, then rerun the installer." >&2
      exit 7
      ;;
  esac

  log "Installing Docker Compose plugin $COMPOSE_VERSION fallback for $compose_arch"
  install -d -m 0755 /usr/local/lib/docker/cli-plugins
  curl -fsSL \
    "https://github.com/docker/compose/releases/download/$COMPOSE_VERSION/docker-compose-linux-$compose_arch" \
    -o /usr/local/lib/docker/cli-plugins/docker-compose
  chmod 0755 /usr/local/lib/docker/cli-plugins/docker-compose
}

start_docker_daemon() {
  if have systemctl; then
    systemctl enable --now docker
  elif have rc-update && have rc-service; then
    rc-update add docker default >/dev/null 2>&1 || true
    rc-service docker start
  elif have service; then
    service docker start || true
  fi

  if ! docker info >/dev/null 2>&1; then
    echo "Docker Engine is installed but the daemon is not reachable." >&2
    echo "Start the Docker service for this distribution and rerun the installer." >&2
    exit 8
  fi
}

ensure_docker() {
  if have docker && docker compose version >/dev/null 2>&1; then
    log "Docker Engine and Compose v2 are already installed; preserving the existing installation."
    start_docker_daemon
    return 0
  fi

  if [[ "$SKIP_DOCKER_INSTALL" == "true" ]]; then
    echo "--skip-docker-install was specified, but Docker Engine + Compose v2 are not both available." >&2
    exit 6
  fi

  if ! have docker; then
    case "$DISTRO_ID" in
      ubuntu)
        install_docker_apt_official ubuntu
        ;;
      debian)
        install_docker_apt_official debian
        ;;
      fedora)
        install_docker_rpm_official fedora
        ;;
      centos)
        install_docker_rpm_official centos
        ;;
      rhel)
        install_docker_rpm_official rhel
        ;;
      *)
        if [[ " $DISTRO_LIKE " == *" ubuntu "* ]]; then
          warn "$DISTRO_NAME is an Ubuntu derivative; using Docker's Ubuntu repository with the base codename."
          install_docker_apt_official ubuntu
        elif [[ " $DISTRO_LIKE " == *" debian "* ]]; then
          warn "$DISTRO_NAME is a Debian derivative; using Docker's Debian repository with the base codename."
          install_docker_apt_official debian
        elif [[ " $DISTRO_LIKE " == *" rhel "* || " $DISTRO_LIKE " == *" fedora "* ]]; then
          warn "$DISTRO_NAME is an RPM derivative; using distribution Docker packages because Docker does not verify every derivative."
          install_docker_from_distribution
        else
          install_docker_from_distribution
        fi
        ;;
    esac
  fi

  start_docker_daemon
  install_compose_plugin_fallback

  if ! docker compose version >/dev/null 2>&1; then
    echo "Docker Compose v2 validation failed after installation." >&2
    exit 7
  fi
}

hex_secret() {
  local bytes="$1"
  if have openssl; then
    openssl rand -hex "$bytes"
  else
    head -c "$bytes" /dev/urandom | od -An -tx1 | tr -d ' \n'
  fi
}

read_env_value() {
  local key="$1"
  sed -n "s/^${key}=//p" "$ENV_FILE" | tail -n 1 | tr -d '\r'
}

set_env_value() {
  local key="$1"
  local value="$2"
  local tmp
  if [[ "$value" == *$'\n'* || "$value" == *$'\r'* ]]; then
    echo "Refusing multiline value for $key." >&2
    exit 9
  fi
  tmp="$(mktemp)"
  # Passing secret values through awk -v interprets sequences such as \\n
  # and \\\\; ENVIRON keeps the original bytes intact.
  LLMPROXY_CONFIG_VALUE="$value" awk -v key="$key" '
    BEGIN { found = 0; value = ENVIRON["LLMPROXY_CONFIG_VALUE"] }
    index($0, key "=") == 1 { print key "=" value; found = 1; next }
    { print }
    END { if (!found) print key "=" value }
  ' "$ENV_FILE" > "$tmp"
  cat "$tmp" > "$ENV_FILE"
  rm -f "$tmp"
}

is_missing_env_value() {
  local value
  value="$(read_env_value "$1")"
  [[ -z "$value" || "$value" == CHANGE_ME* ]]
}

migrate_legacy_env_value() {
  local legacy_key="$1"
  local current_key="$2"
  if ! is_missing_env_value "$current_key"; then
    return 0
  fi

  local legacy_value
  legacy_value="$(read_env_value "$legacy_key")"
  if [[ -z "$legacy_value" || "$legacy_value" == CHANGE_ME* ]]; then
    return 0
  fi

  set_env_value "$current_key" "$legacy_value"
  log "Migrated legacy configuration $legacy_key -> $current_key."
}

migrate_legacy_inference_configuration() {
  migrate_legacy_env_value DGX_NODE_NAME INFERENCE_NODE_NAME
  migrate_legacy_env_value DGX_NODE_BASE_ADDRESS INFERENCE_NODE_BASE_ADDRESS
  migrate_legacy_env_value DGX_HARDWARE_METRICS_BASE_ADDRESS INFERENCE_NODE_HARDWARE_METRICS_BASE_ADDRESS
  migrate_legacy_env_value DGX_NODE_WEIGHT INFERENCE_NODE_WEIGHT
  migrate_legacy_env_value DGX_NODE_MAX_CONCURRENCY INFERENCE_NODE_MAX_CONCURRENCY
}

prompt_required_value() {
  local key="$1"
  local prompt="$2"
  if ! is_missing_env_value "$key"; then
    return 0
  fi
  if [[ "$NON_INTERACTIVE" == "true" || ! -t 0 ]]; then
    return 1
  fi
  local value=""
  read -r -p "$prompt: " value
  if [[ -n "$value" ]]; then
    set_env_value "$key" "$value"
  fi
  ! is_missing_env_value "$key"
}

prepare_environment() {
  local NEW_ENVIRONMENT=false
  [[ -f "$ENV_FILE" ]] || NEW_ENVIRONMENT=true
  install -d -m 0750 "$INSTALL_DIR" "$INSTALL_DIR/runtime" "$INSTALL_DIR/backups"
  if [[ "$CALLER_USER" != "root" ]]; then
    chown "$CALLER_USER:$CALLER_GROUP" "$INSTALL_DIR" "$INSTALL_DIR/runtime" "$INSTALL_DIR/backups"
  fi

  if [[ ! -f "$ENV_FILE" ]]; then
    log "Creating production environment: $ENV_FILE"
    install -m 0600 "$ROOT_DIR/docker/.env.production.example" "$ENV_FILE"
    set_env_value POSTGRES_PASSWORD "$(hex_secret 24)"
    set_env_value REDIS_PASSWORD "$(hex_secret 24)"
    set_env_value LLM_PROXY_API_KEY "llmp_$(hex_secret 24)"
    set_env_value LLM_PROXY_API_KEY_PEPPER "$(hex_secret 32)"
    set_env_value LLMPROXY_UPSTREAM_CREDENTIAL_KEY "$(hex_secret 32)"
    set_env_value GRAFANA_ADMIN_PASSWORD "$(hex_secret 20)"
  else
    log "Preserving existing production environment: $ENV_FILE"
    chmod 0600 "$ENV_FILE"
  fi

  migrate_legacy_inference_configuration

  if [[ -x "$ROOT_DIR/distribution/install-update-agent.sh" && -d "$ROOT_DIR/distribution/update-agent" ]]; then
    if have systemctl && [[ -d /run/systemd/system ]]; then
      log "Installing the host update agent used by Admin update orchestration."
      LLMPROXY_ENV_FILE="$ENV_FILE" bash "$ROOT_DIR/distribution/install-update-agent.sh"
    else
      warn "systemd is not available; skipping the optional host Update Agent. Manual llmproxyctl updates remain supported."
    fi
  fi

  if is_missing_env_value LLMPROXY_UPSTREAM_CREDENTIAL_KEY; then
    log "Generating upstream-credential encryption key for this installation."
    set_env_value LLMPROXY_UPSTREAM_CREDENTIAL_KEY "$(hex_secret 32)"
  fi

  set_env_value GHCR_OWNER "$GHCR_OWNER_VALUE"
  set_env_value LLMPROXY_IMAGE_TAG "$IMAGE_TAG"

  if [[ -n "$INFERENCE_NODE_URL_VALUE" ]]; then
    set_env_value INFERENCE_NODE_BASE_ADDRESS "$INFERENCE_NODE_URL_VALUE"
  fi
  if [[ -n "$PROVIDER_MODEL_VALUE" ]]; then
    set_env_value PROVIDER_MODEL_NAME "$PROVIDER_MODEL_VALUE"
  fi

  if [[ -n "${ENTRA_ENABLED:-}" ]]; then set_env_value ENTRA_ENABLED "$ENTRA_ENABLED"; fi
  if [[ -n "${ENTRA_TENANT_ID:-}" ]]; then set_env_value ENTRA_TENANT_ID "$ENTRA_TENANT_ID"; fi
  if [[ -n "${ENTRA_CLIENT_ID:-}" ]]; then set_env_value ENTRA_CLIENT_ID "$ENTRA_CLIENT_ID"; fi
  if [[ -n "${ENTRA_CLIENT_SECRET:-}" ]]; then set_env_value ENTRA_CLIENT_SECRET "$ENTRA_CLIENT_SECRET"; fi
  if [[ "$SUPER_ADMINS_PROVIDED" == "true" ]]; then
    set_env_value ENTRA_SUPER_ADMINS "$SUPER_ADMINS_VALUE"
    log "Updated configured Entra super-administrator principals without logging their identities."
  fi
  if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then set_env_value CLOUDFLARE_TUNNEL_TOKEN "$CLOUDFLARE_TUNNEL_TOKEN"; fi

  # Protected first-install flow optionally chooses host port mappings before
  # invoking the privileged bootstrap. Updates do not set these values and
  # therefore preserve the operator's existing port selection.
  local host_port_name requested_host_port
  for host_port_name in LLMPROXY_PORT GRAFANA_PORT; do
    requested_host_port="${!host_port_name:-}"
    if [[ -z "$requested_host_port" ]]; then continue; fi
    if [[ ! "$requested_host_port" =~ ^[1-9][0-9]{0,4}$ ]] ||
        (( 10#$requested_host_port > 65535 )); then
      echo "Invalid requested $host_port_name: expected TCP port 1-65535." >&2
      exit 9
    fi
    set_env_value "$host_port_name" "$requested_host_port"
  done
  if [[ "$(read_env_value LLMPROXY_PORT)" == "$(read_env_value GRAFANA_PORT)" ]]; then
    echo "LLMPROXY_PORT and GRAFANA_PORT must be different host ports." >&2
    exit 9
  fi

  if [[ -n "$(read_env_value CLOUDFLARE_TUNNEL_TOKEN)" ]]; then
    set_env_value REVERSE_PROXY_ENABLED true
    # New publicly tunneled installations must not expose an unnecessary public LAN listener.
    if [[ "$NEW_ENVIRONMENT" == true ]]; then
      set_env_value LLMPROXY_BIND_ADDRESS 127.0.0.1
      log "Cloudflare Tunnel detected: public host listener restricted to 127.0.0.1."
    fi
    if is_missing_env_value CLOUDFLARED_PROTOCOL; then
      set_env_value CLOUDFLARED_PROTOCOL http2
    fi
    log "Cloudflare Tunnel configured; enabling one-hop forwarded-header processing for external HTTPS/OIDC."
  fi

  if [[ "$PREPARE_ONLY" != "true" ]]; then
    local node_missing=false model_missing=false
    is_missing_env_value INFERENCE_NODE_BASE_ADDRESS && node_missing=true
    is_missing_env_value PROVIDER_MODEL_NAME && model_missing=true

    if [[ "$node_missing" == true && "$model_missing" == true ]]; then
      # Fresh control-plane installations can pair physical servers and deploy
      # models from Admin after installation. Never bootstrap a fictitious node.
      if [[ "$NEW_ENVIRONMENT" == true ]]; then
        set_env_value BOOTSTRAP_ENABLED false
        log "No inference host configured: starting with an empty model catalog. Pair a Linux Node Agent from Admin."
      elif [[ "$(read_env_value BOOTSTRAP_ENABLED)" != "false" ]]; then
        echo "Existing configuration expects an initial node, but both values are missing." >&2
        echo "Set BOOTSTRAP_ENABLED=false in $ENV_FILE for intentional control-plane-only mode." >&2
        exit 9
      fi
    elif [[ "$node_missing" != "$model_missing" ]]; then
      echo "Specify both --node-url and --provider-model, or neither for an Agent-managed installation." >&2
      exit 9
    else
      # Preserve explicit node support for operators migrating older deployments.
      if [[ "$NEW_ENVIRONMENT" == true ]]; then
        set_env_value BOOTSTRAP_ENABLED true
      fi
    fi
  fi

  chmod 0600 "$ENV_FILE"
  if [[ "$CALLER_USER" != "root" ]]; then
    chown "$CALLER_USER:$CALLER_GROUP" "$ENV_FILE"
  fi
}

login_ghcr_if_configured() {
  if [[ -z "${GHCR_TOKEN:-}" ]]; then
    log "GHCR_TOKEN not supplied; using existing Docker registry credentials or public package access."
    return 0
  fi
  if [[ -z "${GHCR_USER:-}" ]]; then
    echo "GHCR_TOKEN is set but GHCR_USER is empty." >&2
    exit 10
  fi
  log "Authenticating Docker to ghcr.io as $GHCR_USER"
  printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin >/dev/null
}

probe_url() {
  local url="$1"
  local bearer="${INFERENCE_NODE_UPSTREAM_BEARER_TOKEN:-}"
  if have curl; then
    local args=(--fail --silent --show-error --max-time 8)
    if [[ -n "$bearer" ]]; then
      args+=(-H "Authorization: Bearer $bearer")
    fi
    curl "${args[@]}" "$url" >/dev/null
  elif have wget; then
    local args=(-qO- --timeout=8)
    if [[ -n "$bearer" ]]; then
      args+=(--header="Authorization: Bearer $bearer")
    fi
    wget "${args[@]}" "$url" >/dev/null
  else
    return 1
  fi
}

check_dgx() {
  if [[ "$(read_env_value BOOTSTRAP_ENABLED)" == "false" ]]; then
    log "No initial inference node: Admin will onboard agents and models after gateway startup."
    return 0
  fi
  if [[ "$SKIP_NODE_CHECK" == "true" ]]; then
    warn "Skipping initial inference node/vLLM reachability check by request."
    return 0
  fi
  local root probe_root gateway
  root="$(read_env_value INFERENCE_NODE_BASE_ADDRESS)"
  root="${root%/}"
  probe_root="$root"

  if [[ "$root" == http://host.docker.internal:* || "$root" == http://host.docker.internal/* || "$root" == https://host.docker.internal:* || "$root" == https://host.docker.internal/* ]]; then
    gateway="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}' 2>/dev/null || true)"
    if [[ -z "$gateway" ]]; then
      echo "Could not resolve Docker's host-gateway address for same-host inference validation." >&2
      exit 11
    fi
    probe_root="${root/host.docker.internal/$gateway}"
    log "Checking same-host inference through Docker host gateway $gateway (configured root remains $root)"
  else
    log "Checking inference node/vLLM connectivity at $root"
  fi

  if ! probe_url "$probe_root/health" || ! probe_url "$probe_root/v1/models"; then
    if [[ "$root" == *"host.docker.internal"* ]]; then
      cat >&2 <<EOF
Same-host inference is not reachable through Docker's host gateway.
A llama-server bound only to 127.0.0.1 cannot be reached by LlmProxy's bridge container.

Bind llama-server to the Docker bridge gateway (currently $gateway), for example:
  llama-server --host $gateway --port 8080 --api-key '<secret>' ...

Keep LlmProxy configured with:
  INFERENCE_NODE_BASE_ADDRESS=http://host.docker.internal:8080
EOF
    fi
    exit 11
  fi
}

stage 1 "Inspecting Linux host"
log "Distribution: $DISTRO_NAME"
log "Architecture: $ARCH"
log "Caller: $CALLER_USER"

stage 2 "Installing host prerequisites"
install_prerequisites

stage 3 "Checking Docker Engine and Compose"
ensure_docker

if getent group docker >/dev/null 2>&1 && [[ "$CALLER_USER" != "root" ]]; then
  if ! id -nG "$CALLER_USER" | tr ' ' '\n' | grep -qx docker; then
    usermod -aG docker "$CALLER_USER"
    log "Added $CALLER_USER to the docker group. A new login/session is required for direct Docker use without sudo."
  fi
fi

stage 4 "Preparing persistent configuration"
prepare_environment

stage 5 "Checking container registry access"
login_ghcr_if_configured

if [[ "$PREPARE_ONLY" == "true" ]]; then
  log "Host preparation complete; deployment was skipped by --prepare-only."
  log "Review $ENV_FILE, then run docker/scripts/deploy.sh with the desired image tag."
  exit 0
fi

stage 6 "Checking inference runtime connectivity"
check_dgx

stage 7 "Deploying LlmProxy containers"
log "Deploying LlmProxy full stack with image tag $IMAGE_TAG"
LLMPROXY_DEPLOY_DIR="$INSTALL_DIR" \
LLMPROXY_ENV_FILE="$ENV_FILE" \
  bash "$ROOT_DIR/docker/scripts/deploy.sh" "$IMAGE_TAG"

if [[ -n "${INFERENCE_NODE_UPSTREAM_BEARER_TOKEN:-}" ]]; then
  log "Removing the one-time upstream bearer from the long-lived container environment after encrypted bootstrap."
  unset INFERENCE_NODE_UPSTREAM_BEARER_TOKEN
  LLMPROXY_DEPLOY_DIR="$INSTALL_DIR" \
  LLMPROXY_ENV_FILE="$ENV_FILE" \
    bash "$ROOT_DIR/docker/scripts/deploy.sh" "$IMAGE_TAG"
fi

stage 8 "Finalizing installation"
if [[ "$CALLER_USER" != "root" ]]; then
  chown -R "$CALLER_USER:$CALLER_GROUP" "$INSTALL_DIR/runtime" "$INSTALL_DIR/backups"
fi

PORT="$(read_env_value LLMPROXY_PORT)"
PORT="${PORT:-8080}"
GRAFANA_PORT_VALUE="$(read_env_value GRAFANA_PORT)"
GRAFANA_PORT_VALUE="${GRAFANA_PORT_VALUE:-3000}"

cat <<EOF

LlmProxy Linux installation completed successfully.

Gateway health:   http://127.0.0.1:$PORT/healthz
Gateway readiness:http://127.0.0.1:$PORT/readyz
Admin UI:         http://<host>:$PORT/admin/
Grafana (default loopback bind): http://127.0.0.1:$GRAFANA_PORT_VALUE
Environment file: $ENV_FILE
Runtime assets:   $INSTALL_DIR/runtime
Backups:          $INSTALL_DIR/backups
Install log:       $INSTALL_LOG
Latest log:        $LOG_DIR/latest-install.log

The generated client inference credential, HMAC pepper and upstream-credential master key are stored only in the protected environment file.
Back up LLM_PROXY_API_KEY_PEPPER and LLMPROXY_UPSTREAM_CREDENTIAL_KEY separately before treating this host as production.
EOF
