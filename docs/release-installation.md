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

## Public release downloads and optional registry authentication

The GitHub repository and GitHub Release assets are public. The bootstrap therefore downloads release bundles and checksum files without a GitHub token when no authenticated GitHub CLI/token is available.

For example, the bootstrap itself can be downloaded with ordinary `curl`:

```bash
curl -fL https://github.com/KeyserDSoze/LlmProxy/releases/latest/download/llmproxy-bootstrap.sh -o llmproxy-bootstrap.sh
curl -fL https://github.com/KeyserDSoze/LlmProxy/releases/latest/download/llmproxy-bootstrap.sh.sha256 -o llmproxy-bootstrap.sh.sha256
sha256sum -c llmproxy-bootstrap.sh.sha256
chmod +x llmproxy-bootstrap.sh
```

GHCR package visibility is a separate GitHub setting. If the container package requires authentication, export `GHCR_USER` and `GHCR_TOKEN` only for installation/update. Those download credentials are never written into the LlmProxy application environment.

## First installation from a release

Production startup requires Entra. Before the first production deployment, provide the tenant application values through the installer environment (the installer writes them to the protected host configuration):

```bash
export ENTRA_ENABLED=true
export ENTRA_TENANT_ID='<tenant-id>'
export ENTRA_CLIENT_ID='<client-id>'
export ENTRA_CLIENT_SECRET='<client-secret>'
# Optional full administrators; stable oid:<object-id> entries are preferred.
export ENTRA_SUPER_ADMINS='admin1@example.com;admin2@example.com'
```

If Entra is not available yet, use the Development/full-stack acceptance path for private validation; do not label a no-Entra deployment as Production.

Resolve the latest validated immutable release and download its bootstrap asset:

```bash
LATEST_TAG="$(gh release view --repo KeyserDSoze/LlmProxy --json tagName --jq .tagName)"
VERSION="${LATEST_TAG#v}"

gh release download "$LATEST_TAG" \
  --repo KeyserDSoze/LlmProxy \
  --pattern llmproxy-bootstrap.sh
chmod +x llmproxy-bootstrap.sh
```

Then install that exact version:

```bash
./llmproxy-bootstrap.sh \
  --version "$VERSION" \
  --dgx-url http://10.0.0.21:8000 \
  --provider-model '<exact-provider-model-id>'
```

On a host where Docker is already installed:

```bash
./llmproxy-bootstrap.sh \
  --version "$VERSION" \
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
  --version "$VERSION" \
  --skip-docker-install \
  --dgx-url http://host.docker.internal:8080 \
  --provider-model qwen3-next-80b-1m

unset DGX_UPSTREAM_BEARER_TOKEN
```

For `host.docker.internal`, the installer resolves Docker's bridge gateway and performs authenticated `/health` + `/v1/models` preflight checks against that address. A runtime still bound only to `127.0.0.1` therefore fails before LlmProxy deployment with the gateway address to use.

During first bootstrap the bearer is encrypted into the node record, then the installer redeploys LlmProxy without `DGX_UPSTREAM_BEARER_TOKEN` so the plaintext is not retained in the long-lived container environment. Keep the inference port restricted to the Docker bridge/trusted network.

## Installer progress, logs and failure diagnostics

The installer is intentionally verbose about **progress and decisions**, but it never enables shell `set -x` and does not print API keys, bearer tokens, Entra client secrets, database passwords or generated encryption keys.

A normal installation reports eight high-level phases:

```text
[1/8] Inspecting Linux host
[2/8] Installing host prerequisites
[3/8] Checking Docker Engine and Compose
[4/8] Preparing persistent configuration
[5/8] Checking container registry access
[6/8] Checking inference runtime connectivity
[7/8] Deploying LlmProxy containers
[8/8] Finalizing installation
```

Deployment output also reports Compose validation, image pull/start and liveness/readiness progress.

Every privileged install/update writes a persistent log:

```text
/var/log/llmproxy/install-YYYYMMDDTHHMMSSZ.log
/var/log/llmproxy/latest-install.log -> latest attempt
```

Follow the current log from another terminal with:

```bash
sudo tail -f /var/log/llmproxy/latest-install.log
```

If installation fails, the terminal prints:

- the stage that failed;
- the exit code;
- the exact persistent log path;
- a Docker container snapshot when Docker is available.

A readiness timeout also prints `docker compose ps` and the last 100 LlmProxy container log lines.

Failures **before** privileged installation — GitHub download, SHA-256 validation or bundle extraction — preserve the bootstrap temporary directory automatically. The failure summary prints the exact `bootstrap.log` path and directory containing the downloaded files instead of deleting diagnostic evidence.

Useful commands after installation:

```bash
llmproxyctl doctor
llmproxyctl status
llmproxyctl health
llmproxyctl logs
sudo tail -n 200 /var/log/llmproxy/latest-install.log
```

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
sudo -E llmproxyctl update 0.0.2
```

The existing super-administrator list is preserved when omitted. To replace it as part of an update:

```bash
sudo -E llmproxyctl update 0.0.3 \
  --super-admins 'admin1@example.com;admin2@example.com'
```

An explicitly empty value clears the local elevation list:

```bash
sudo -E llmproxyctl update 0.0.3 --super-admins ''
```

Public GitHub Release assets need no release-download token. If GHCR requires authentication, provide `GHCR_USER` / `GHCR_TOKEN` to the update command environment.`

The update path downloads and verifies the new operator bundle, preserves `/opt/llmproxy/.env`, refreshes runtime assets, pulls the exact application image and requires `/healthz` plus `/readyz` before the new bundle becomes `current`.

The initial install performs direct authenticated inference-runtime preflight. Later `llmproxyctl update` operations intentionally skip that direct provider precheck because a protected node's bearer is write-only and no longer exists in host plaintext configuration. The update reuses the encrypted credential already persisted with the node and treats the gateway's post-deploy readiness as the acceptance gate.

## Rollback

Previously installed release bundles remain under `/opt/llmproxy/releases`.

```bash
sudo llmproxyctl rollback 0.0.1
```

Rollback switches the configured image tag and deploy tooling to that already-installed version. It does not roll back database schema/data automatically; a release that introduces a non-backward-compatible migration must document its database rollback requirements explicitly.

## Creating a release

Release creation is automatic after one repository setup step.

In **Settings → Secrets and variables → Actions**, create `RELEASE_TOKEN` using a repository-scoped fine-grained PAT with **Contents: read/write** and **Workflows: read/write**. This permission is required by GitHub when an immutable tag/release points at a commit that modifies workflow files; the built-in `GITHUB_TOKEN` cannot receive that permission.

After that one-time setup, release creation is automatic.

1. Push a commit to `main`.
2. The complete **CI** workflow runs, including the distributed full-stack gate.
3. If CI fails, no release is created.
4. If CI succeeds, **Create immutable release tag** runs automatically for that exact SHA.
5. The default version increment is patch. Put `release:minor` or `release:major` in the final commit message when that push should advance a larger SemVer component.
6. The workflow creates the next unused stable `vMAJOR.MINOR.PATCH` tag, starting from `v0.0.1`.
7. It directly calls the reusable **Publish container** workflow, which builds `linux/amd64` + `linux/arm64`, verifies SBOM/provenance, packages the Linux bundle and creates the GitHub Release.

Every successful `main` push therefore produces at most one immutable release. Exact release tags and exact container tags are never moved or overwritten.

## ARM64 / DGX Spark

The release pipeline publishes the application container for both:

```text
linux/amd64
linux/arm64
```

This makes the same exact release consumable by conventional x86_64 Linux hosts and ARM64 systems such as NVIDIA DGX Spark / Dell Pro Max with GB10.

Repository CI still runs primarily on GitHub-hosted amd64 runners. A successful multi-architecture Buildx publication proves the ARM64 image builds, while real DGX Spark installation/runtime acceptance remains a target-environment acceptance step.

## Cloudflare Tunnel and Entra ID

When the bundled `cloudflared` service is used, publish exactly one Cloudflare origin:

```text
HTTP -> llmproxy:8080
```

Keep the host listener loopback-only:

```env
LLMPROXY_BIND_ADDRESS=127.0.0.1
LLMPROXY_PORT=8081
CLOUDFLARE_TUNNEL_TOKEN=<tunnel-token>
CLOUDFLARED_PROTOCOL=http2
REVERSE_PROXY_ENABLED=true
```

The Linux installer automatically sets `REVERSE_PROXY_ENABLED=true` whenever a persisted Cloudflare tunnel token is present. The bundled tunnel defaults to HTTP/2 over TCP/7844 for compatibility with networks where QUIC/UDP 7844 is unstable; set `CLOUDFLARED_PROTOCOL=auto` or `quic` only when desired. In that mode LlmProxy accepts one direct `X-Forwarded-*` proxy hop so OpenID Connect sees the original public HTTPS scheme/host.

For a public hostname such as:

```text
https://llmproxy.example.com
```

register this exact **Web** redirect URI in the Microsoft Entra application:

```text
https://llmproxy.example.com/signin-oidc
```

The Admin SPA itself is protected at top-level navigation when Entra is enabled. Unauthenticated browser navigation therefore challenges Entra before the SPA loads. Protected API calls return a plain `401` rather than redirecting an XHR to Microsoft; this avoids the browser surfacing an opaque CORS `Failed to fetch`. A signed-in user without the required application role receives `403` instead of being sent through another login loop.

## Inference runtime boundary

The installer deploys **LlmProxy and its control-plane dependencies**. It does not currently install or own llama.cpp/vLLM/model weights. The configured inference runtime must already expose the OpenAI-compatible service-root contract documented in `docs/dgx-vllm.md`.

Protected inference runtimes may use a per-node upstream bearer credential. The Admin API/UI treats it as write-only, persists only AES-GCM ciphertext, and applies it to health/model probes, runtime metrics, maintenance warm-up and inference. The deployment master key `LLMPROXY_UPSTREAM_CREDENTIAL_KEY` is an external recovery dependency.

The client-facing LlmProxy API key and the upstream provider credential are deliberately different trust boundaries: the former authenticates a client to LlmProxy and is never forwarded; the latter authenticates LlmProxy to the selected inference node.
