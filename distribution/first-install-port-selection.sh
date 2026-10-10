#!/usr/bin/env bash
# Inlined exclusively into the password-protected first-install payload.
# Never prompt on test-update.sh or any normal llmproxyctl update.
choose_llmproxy_install_ports() {
  local existing_env="/opt/llmproxy/.env"
  local saved_gateway="8080"
  local saved_grafana="3000"
  local choice
  local tty_fd

  # Re-running the first-install script on an already installed host should
  # preserve its existing port selection and behave like a non-interactive
  # redeploy. The dedicated update command never calls this function.
  if [[ -e /opt/llmproxy/current ]]; then
    echo "Existing LLMProxy installation detected: keeping configured host ports."
    return 0
  fi

  # An interrupted first install may have written .env already. Use those
  # ports as the defaults rather than discarding the operator's earlier choice.
  if [[ -r "$existing_env" ]]; then
    choice="$(sed -n 's/^LLMPROXY_PORT=//p' "$existing_env" | tail -n 1)"
    [[ -z "$choice" ]] || saved_gateway="$choice"
    choice="$(sed -n 's/^GRAFANA_PORT=//p' "$existing_env" | tail -n 1)"
    [[ -z "$choice" ]] || saved_grafana="$choice"
  fi

  # Installation through a pipe has no interactive stdin, but the controlling
  # terminal is still available via /dev/tty. CI/batch callers keep defaults.
  if ! { exec {tty_fd}</dev/tty; } 2>/dev/null; then
    export LLMPROXY_PORT="$saved_gateway"
    export GRAFANA_PORT="$saved_grafana"
    echo "No interactive terminal: using configured/default host ports."
    return 0
  fi

  choose_one_port() {
    local label="$1" suggested="$2" new_port
    local other="$3"
    while true; do
      if command -v ss >/dev/null 2>&1 &&
          ss -H -ltn "( sport = :$suggested )" 2>/dev/null | grep -q .; then
        printf 'Note: TCP host port %s appears to be in use.\n' "$suggested" > /dev/tty
      fi
      printf '%s host port [%s] (Enter to keep): ' "$label" "$suggested" > /dev/tty
      if ! IFS= read -r new_port <&"$tty_fd"; then
        echo "Could not read the installation port selection." >&2
        return 2
      fi
      new_port="${new_port:-$suggested}"
      if [[ ! "$new_port" =~ ^[1-9][0-9]{0,4}$ ]] ||
          (( 10#$new_port > 65535 )); then
        printf 'Invalid TCP port. Enter a number between 1 and 65535.\n' > /dev/tty
        continue
      fi
      if [[ "$new_port" == "$other" ]]; then
        printf 'Gateway and Grafana cannot share the same host port.\n' > /dev/tty
        continue
      fi
      if command -v ss >/dev/null 2>&1 &&
          ss -H -ltn "( sport = :$new_port )" 2>/dev/null | grep -q .; then
        printf 'Port %s is occupied. If it belongs to an existing LLMProxy container, it may be reused on retry. Continue? [y/N]: ' "$new_port" > /dev/tty
        local answer
        IFS= read -r answer <&"$tty_fd" || return 2
        if [[ "$answer" != [yY] && "$answer" != [yY][eE][sS] ]]; then
          continue
        fi
      fi
      printf '%s' "$new_port"
      return 0
    done
  }

  printf '\nFirst installation: select the host ports (or press Enter for defaults).\n' > /dev/tty
  printf 'A host port conflict does not require stopping other containers.\n' > /dev/tty
  GRAFANA_PORT="$(choose_one_port "Grafana" "$saved_grafana" "$saved_gateway")" || return 2
  LLMPROXY_PORT="$(choose_one_port "LLMProxy gateway" "$saved_gateway" "$GRAFANA_PORT")" || return 2
  export GRAFANA_PORT LLMPROXY_PORT
  exec {tty_fd}<&-
  echo "Host port selection: Gateway $LLMPROXY_PORT, Grafana $GRAFANA_PORT."
  echo "Cloudflare Docker Tunnel normally continues using internal http://llmproxy:8080 regardless of host ports."
}
choose_llmproxy_install_ports
