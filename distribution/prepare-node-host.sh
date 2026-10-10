#!/usr/bin/env bash
set -euo pipefail
# Supported host preparation from a checksummed immutable Agent release.
# Kernel GPU drivers are intentionally never installed or rebooted automatically.
if [[ "$(id -u)" -ne 0 ]]; then echo "Root privileges required." >&2; exit 1; fi
if ! command -v systemctl >/dev/null 2>&1; then
  echo "A systemd-based Linux host is required." >&2; exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
  echo "[llmproxy-agent] Installing Docker from the Linux distribution package repository."
  if command -v apt-get >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y docker.io
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y moby-engine || dnf install -y docker
  else
    echo "Unsupported automatic Docker installation. Supported: Debian/Ubuntu (apt) and compatible RPM hosts (dnf)." >&2
    exit 2
  fi
fi
systemctl enable --now docker
if ! docker info >/dev/null 2>&1; then
  echo "Docker installed but its daemon is not responding." >&2
  exit 3
fi
if command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi -L >/dev/null 2>&1; then
  if ! command -v nvidia-ctk >/dev/null 2>&1; then
    if command -v apt-get >/dev/null 2>&1; then
      # Pin the signing key to the NVIDIA toolkit package source (not a remote shell script).
      command -v gpg >/dev/null 2>&1 || apt-get install -y gnupg
      command -v curl >/dev/null 2>&1 || apt-get install -y curl
      install -d -m 0755 /usr/share/keyrings
      curl --fail --silent --show-error --location https://nvidia.github.io/libnvidia-container/gpgkey |
        gpg --dearmor --yes -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
      curl --fail --silent --show-error --location         https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list |
        sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#'         > /etc/apt/sources.list.d/nvidia-container-toolkit.list
      apt-get update -qq
      apt-get install -y nvidia-container-toolkit
    else
      echo "[llmproxy-agent] NVIDIA driver detected; toolkit is missing and must be provisioned for this distribution." >&2
    fi
  fi
  if command -v nvidia-ctk >/dev/null 2>&1; then
    nvidia-ctk runtime configure --runtime=docker
    systemctl restart docker
  fi
else
  echo "[llmproxy-agent] No operational NVIDIA driver detected; CPU runtimes remain available."
fi
echo "[llmproxy-agent] Host prerequisite preparation complete."
