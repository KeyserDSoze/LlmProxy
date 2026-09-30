# Release-based Linux installation and updates

LlmProxy supports a release-oriented Linux distribution path so an operator does not need a Git checkout, .NET SDK, Node.js or a local source build.

## Distribution model

Each immutable `vX.Y.Z...` Git tag produces:

- a multi-architecture GHCR image for `linux/amd64` and `linux/arm64`;
- `llmproxy-<version>-linux.tar.gz` with the production Compose file, observability configuration, operator scripts and `llmproxyctl`;
- SHA-256 checksum files;
- `llmproxy-bootstrap.sh`;
- the existing SBOM/provenance release manifest;
- a GitHub Release tied to the exact tag.

The host-owned state remains outside the release bundle:

```text
/opt/llmproxy/.env                 protected configuration and secrets
/opt/llmproxy/runtime/             active Compose/observability assets
/opt/llmproxy/releases/<version>/  installed immutable operator bundles
/opt/llmproxy/current              symlink to the active operator bundle
/opt/llmproxy/backups/             backup destination
/usr/local/bin/llmproxyctl         stable operator command
```

PostgreSQL, Redis and observability data remain in Docker volumes and are not deleted by normal install/update operations.

## Why this is not an npm/npx installer

The production runtime is Docker-based and must work on clean Linux servers. Requiring Node.js only to install the product would add a dependency that the runtime itself does not need. The primary installer is therefore POSIX/Linux shell plus Docker. Node remains an implementation detail of the bundled Admin UI build.

## Private repository authentication

The repository and GHCR package are currently private. An operator therefore needs GitHub authorization for release downloads and container pulls.

Recommended options:

```bash
gh auth login
```

or export a token only for the installation/update command:

````bash
export GH_TOKEN='<token-with-repository-read-and-package-read-access>'
# Optional: set GHCR_USER/GHCR_TOKEN explicitly; otherwise bootstrap derives them when possible.
```

The bootstrap can reuse authenticated GitHub CLI credentials or `GH_TOKEN` for the private release and GHCR login when the token also has package-read access. The application environment file never stores GitHub/GHCR download credentials.

If the distribution becomes public later, the same bootstrap script can be downloaded with ordinary unauthenticated `curl`.

## First installation from a release

Download the bootstrap asset from the GitHub Release, or when GitHub CLI is authenticated:

```bash
gh release download v0.2.0-preview.8 \
  --repo KeyserDSoze/LlmProxy \
  --pattern llmproxy-bootstrap.sh
chmod +x llmproxy-bootstrap.sh
```

Then install an exact version:

```bash
./llmproxy-bootstrap.sh \
  --version 0.2.0-preview.8 \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-provider-model-id>'
```

On a host where Docker is already installed:

```bash
./llmproxy-bootstrap.sh \
  --version 0.2.0-preview.8 \
  --skip-docker-install \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-provider-model-id>'
```

The bootstrap downloads the immutable bundle and checksum, verifies SHA-256, extracts it into a temporary directory and delegates privileged host work to the versioned installer. Release installations intentionally use the canonical `/opt/llmproxy` layout so `llmproxyctl`, updates and rollback always agree on one host-owned state root.

For a DGX Spark/GB10 where the inference runtime runs on the **same Linux host** as Docker, bind the runtime to Docker's bridge-gateway address instead of loopback. This keeps it reachable from LlmProxy without publishing it on every LAN interface:

```bash
DOCKER_HOST_GATEWAY="$(docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')"

llama-server \
  --host "$DOCKER_HOST_GATEWAY" \
  --port 8080 \
  --api-key llama-local \
  ...your existing model/context arguments...
```

Then provide the runtime bearer only to the installation command and configure the node from the container perspective:

```bash
export DGX_UPSTREAM_BEARER_TOKEN='llama-local'

./llmproxy-bootstrap.sh \
  --version 0.2.0-preview.8 \
  --skip-docker-install \
  --dgx-url http://host.docker.internal:8080 \
  --provider-model qwen3-next-80b-1m

unset DGX_UPSTREAM_BEARER_TOKEN
```

For `host.docker.internal`, the installer resolves Docker's bridge gateway and performs authenticated `/health` + `/v1/models` preflight checks against that address. A runtime still bound only to `127.0.0.1` therefore fails before LlmProxy deployment with the gateway address to use.

During first bootstrap the bearer is encrypted into the node record, then the installer redeploys LlmProxy without `DGX_UPSTREAM_BEARER_TOKEN` so the plaintext is not retained in the long-lived container environment. Keep the inference port restricted to the Docker bridge/trusted network.

## Operator command

After installation:

```bash
llmproxyctl status
llmproxyctl health
llmproxyctl version
llmproxyctl doctor
llmproxyctl logs
```

Lifecycle operations:

```bash
sudo llmproxyctl stop
sudo llmproxyctl start
sudo llmproxyctl restart
```

## Update

An update is always explicit and versioned:

```bash
sudo -E llmproxyctl update 0.2.0-preview.9
```

For a private repository, make release-download credentials available to the command when required:

``bash
export GH_TOKEN='<repo-read-token>'
sudo -E llmproxyctl update 0.2.0-preview.9
unset GH_TOKEN
```

The update path downloads and verifies the new operator bundle, preserves `/opt/llmproxy/.env`, refreshes runtime assets, pulls the exact application image and requires `/healthz` plus `/readyz` before the new bundle becomes `current`.

The initial install performs direct authenticated inference-runtime preflight. Later `llmproxyctl update` operations intentionally skip that direct provider precheck because a protected node's bearer is write-only and no longer exists in host plaintext configuration. The update reuses the encrypted credential already persisted with the node and treats the gateway's post-deploy readiness as the acceptance gate.

## Rollback

Previously installed release bundles remain under `/opt/llmproxy/releases`.

```bash
sudo llmproxyctl rollback 0.2.0-preview.8
```

Rollback switches the configured image tag and deploy tooling to that already-installed version. It does not roll back database schema/data automatically; a release that introduces a non-backward-compatible migration must document its database rollback requirements explicitly.

## Creating a release

Releases are deliberately owner-triggered rather than automatic on every green `main` build.

1. Update the SemVer in all product sources and release notes.
2. Push to `main`.
3. Wait for **CI** and **Full stack smoke** to succeed on that exact SHA.
4. In GitHub Actions run **Create immutable release tag**.
5. Enter the exact version and confirmation `RELEASE`.
6. The workflow creates `v<version>` only if the tag does not already exist and the exact main SHA has green CI/full-stack evidence.
7. The existing **Publish container** workflow reacts to the tag, builds `linux/amd64` + `linux/arm64`, verifies SBOM/provenance, packages the Linux installer bundle and creates the GitHub Release.

Exact release tags are immutable and must never be moved or reused for different bits.

## ARM64 / DGX Spark

The release pipeline publishes the application container for both:

```text
linux/amd64
linux/arm64
```

This makes the same exact release consumable by conventional x86_64 Linux hosts and ARM64 systems such as NVIDIA DGX Spark / Dell Pro Max with GB10.

Repository CI still runs primarily on GitHub-hosted amd64 runners. A successful multi-architecture Buildx publication proves the ARM64 image builds, while real DGX Spark installation/runtime acceptance remains a target-environment acceptance step.

## Inference runtime boundary

The installer deploys **LlmProxy and its control-plane dependencies**. It does not currently install or own llama.cpp/vLLM/model weights. The configured inference runtime must already expose the OpenAI-compatible service-root contract documented in `docs/dgx-vllm.md`.

Protected inference runtimes may use a per-node upstream bearer credential. The Admin API/UI treats it as write-only, persists only AES-GCM ciphertext, and applies it to health/model probes, runtime metrics, maintenance warm-up and inference. The deployment master key `LLMPROXY_UPSTREAM_CREDENTIAL_KEY` is an external recovery dependency.

The client-facing LlmProxy API key and the upstream provider credential are deliberately different trust boundaries: the former authenticates a client to LlmProxy and is never forwarded; the latter authenticates LlmProxy to the selected inference node.
