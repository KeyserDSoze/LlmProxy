#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INSTALL_DIR="${LLMPROXY_INSTALL_DIR:-/opt/llmproxy}"
ENV_FILE=""
IMAGE_TAG="${LLMPROXY_IMAGE_TAG:-main}"
GHCR_OWNER_VALUE="${GHCR_OWNER:-keyserdsoze}"
DGX_URL_VALUE="${DGX_NODE_BASE_ADDRESS:-}"
PROVIDER_MODEL_VALUE="${PROVIDER_MODEL_NAME:-}"
COMPOSE_VERSION="${DOCKER_COMPOSE_VERSION:-v5.5.0}"
PREPARE_ONLY=false
SKIP_DGX_CHECK=false
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
  --dgx-url URL           Initial DGX/vLLM service root
  --provider-model MODEL  Exact provider model id exposed by vLLM
  --prepare-only          Install host prerequisites and create config, but do not deploy
  --skip-dgx-check        Skip /health and /v1/models checks against the initial DGX
  --skip-docker-install   Require Docker + Compose to already be installed
  --non-interactive       Never prompt; required values must be supplied by flags/env/file
  --validate-only         Validate installer/repository compatibility without changing host
  -h, --help              Show this help

Optional environment variables:
  GHCR_USER / GHCR_TOKEN            Login to a private GHCR package without storing the token
  ENTRA_ENABLED / ENTRA_TENANT_ID / ENTRA_CLIENT_ID / ENTRA_CLIENT_SECRET
  DGX_UPSTREAM_BEARER_TOKEN        Optional one-time llama.cpp/vLLM bearer; never written to .env by the installer
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
    --dgx-url)
      DGX_URL_VALUE="${2:?--dgx-url requires a value}"
      shift 2
      ;;
    --provider-model)
      PROVIDER_MODEL_VALUE="${2:?--provider-model requires a value}"
      shift 2
      ;;
    --prepare-only)
      PREPARE_ONLY=true
      shift
      ;;
    --skip-dgx-check)
      SKIP_DGX_CHECK=true
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

log() {
  printf '[llmproxy-install] %s\n' "$*"
}

warn() {
  printf '[llmproxy-install] WARNING: %s\n' "$*" >&2
}

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
  awk -v key="$key" -v value="$value" '
    BEGIN { found = 0 }
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

  if is_missing_env_value LLMPROXY_UPSTREAM_CREDENTIAL_KEY; then
    log "Generating upstream-credential encryption key for this installation."
    set_env_value LLMPROXY_UPSTREAM_CREDENTIAL_KEY "$(hex_secret 32)"
  fi

  set_env_value GHCR_OWNER "$GHCR_OWNER_VALUE"
  set_env_value LLMPROXY_IMAGE_TAG "$IMAGE_TAG"

  if [[ -n "$DGX_URL_VALUE" ]]; then
    set_env_value DGX_NODE_BASE_ADDRESS "$DGX_URL_VALUE"
  fi
  if [[ -n "$PROVIDER_MODEL_VALUE" ]]; then
    set_env_value PROVIDER_MODEL_NAME "$PROVIDER_MODEL_VALUE"
  fi

  if [[ -n "${ENTRA_ENABLED:-}" ]]; then set_env_value ENTRA_ENABLED "$ENTRA_ENABLED"; fi
  if [[ -n "${ENTRA_TENANT_ID:-}" ]]; then set_env_value ENTRA_TENANT_ID "$ENTRA_TENANT_ID"; fi
  if [[ -n "${ENTRA_CLIENT_ID:-}" ]]; then set_env_value ENTRA_CLIENT_ID "$ENTRA_CLIENT_ID"; fi
  if [[ -n "${ENTRA_CLIENT_SECRET:-}" ]]; then set_env_value ENTRA_CLIENT_SECRET "$ENTRA_CLIENT_SECRET"; fi
  if [[ -n "${CLOUDFLARE_TUNNEL_TOKEN:-}" ]]; then set_env_value CLOUDFLARE_TUNNEL_TOKEN "$CLOUDFLARE_TUNNEL_TOKEN"; fi

  if [[ "$PREPARE_ONLY" != "true" ]]; then
    prompt_required_value DGX_NODE_BASE_ADDRESS "DGX/vLLM service root (for example http://10.0.0.21:8000)" || true
    prompt_required_value PROVIDER_MODEL_NAME "Exact provider model id exposed by vLLM" || true

    if is_missing_env_value DGX_NODE_BASE_ADDRESS || is_missing_env_value PROVIDER_MODEL_NAME; then
      cat >&2 <<EOF
Production configuration still needs DGX_NODE_BASE_ADDRESS and PROVIDER_MODEL_NAME.
Edit $ENV_FILE or rerun with --dgx-url and --provider-model.
EOF
      exit 9
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
  local bearer="${DGX_UPSTREAM_BEARER_TOKEN:-}"
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
  if [[ "$SKIP_DGX_CHECK" == "true" ]]; then
    warn "Skipping initial DGX/vLLM reachability check by request."
    return 0
  fi
  local root probe_root gateway
  root="$(read_env_value DGX_NODE_BASE_ADDRESS)"
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
    log "Checking DGX/vLLM connectivity at $root"
  fi

  if ! probe_url "$probe_root/health" || ! probe_url "$probe_root/v1/models"; then
    if [[ "$root" == *"host.docker.internal"* ]]; then
      cat >&2 <<EOF
Same-host inference is not reachable through Docker's host gateway.
A llama-server bound only to 127.0.0.1 cannot be reached by LlmProxy's bridge container.

Bind llama-server to the Docker bridge gateway (currently $gateway), for example:
  llama-server --host $gateway --port 8080 --api-key '<secret>' ...

Keep LlmProxy configured with:
  DGX_NODE_BASE_ADDRESS=http://host.docker.internal:8080
EOF
    fi
    exit 11
  fi
}

install_prerequisites
ensure_docker

if getent group docker >/dev/null 2>&1 && [[ "$CALLER_USER" != "root" ]]; then
  if ! id -nG "$CALLER_USER" | tr ' ' '\n' | grep -qx docker; then
    usermod -aG docker "$CALLER_USER"
    log "Added $CALLER_USER to the docker group. A new login/session is required for direct Docker use without sudo."
  fi
fi

prepare_environment
login_ghcr_if_configured

if [[ "$PREPARE_ONLY" == "true" ]]; then
  log "Host preparation complete; deployment was skipped by --prepare-only."
  log "Review $ENV_FILE, then run docker/scripts/deploy.sh with the desired image tag."
  exit 0
fi

check_dgx

log "Deploying LlmProxy full stack with image tag $IMAGE_TAG"
LLMPROXY_DEPLOY_DIR="$INSTALL_DIR" \
LLMPROXY_ENV_FILE="$ENV_FILE" \
  bash "$ROOT_DIR/docker/scripts/deploy.sh" "$IMAGE_TAG"

if [[ -n "${DGX_UPSTREAM_BEARER_TOKEN:-}" ]]; then
  log "Removing the one-time upstream bearer from the long-lived container environment after encrypted bootstrap."
  unset DGX_UPSTREAM_BEARER_TOKEN
  LLMPROXY_DEPLOY_DIR="$INSTALL_DIR" \
  LLMPROXY_ENV_FILE="$ENV_FILE" \
    bash "$ROOT_DIR/docker/scripts/deploy.sh" "$IMAGE_TAG"
fi

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

The generated client inference credential, HMAC pepper and upstream-credential master key are stored only in the protected environment file.
Back up LLM_PROXY_API_KEY_PEPPER and LLMPROXY_UPSTREAM_CREDENTIAL_KEY separately before treating this host as production.
EOF
