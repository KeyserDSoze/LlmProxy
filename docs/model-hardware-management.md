# Managed hardware and model lifecycle

LlmProxy can manage prepared inference hardware without assuming a specific NVIDIA DGX product. A managed node can be a DGX, a conventional x86_64 GPU server, an ARM64 accelerator host, or another Linux machine able to run the configured container runtime.

## Architecture

```text
Admin browser
    |
    v
LlmProxy Admin/API
    |
    | encrypted management bearer
    v
LlmProxy Node Agent :9900
    |
    +--> host inventory: CPU / RAM / disk / NVIDIA GPU
    |
    +--> Docker lifecycle
            |
            +--> vLLM model A :18000
            +--> vLLM model B :18001
            +--> vLLM model C :18002
```

The physical node and the model runtime are deliberately different concepts. A node owns hardware inventory and physical capacity; a deployment binds one logical model to one node; a managed deployment also stores its own `RuntimeBaseAddress`. Routing is grouped by logical model and uses only deployments that actually host that model, so multiple models on one physical machine can listen on different ports and participate in different routing pools.

Stopping or removing a managed model disables its deployment before the remote lifecycle operation. This prevents new traffic from being routed to a runtime that is being stopped.

## Infrastructure Admin workspace

The former Inference Nodes, Hardware and Model & Hardware navigation entries are consolidated under **Infrastructure**. Its tabs separate **Fleet & access**, **Capacity & telemetry**, and **Inventory & model lifecycle** while keeping one physical-machine concept.

For the selected node, Inventory & model lifecycle shows CPU logical cores, total/free system RAM, total/free disk, detected NVIDIA GPUs, per-GPU total/free VRAM, driver and compute capability, plus the configured runtime version. This inventory is reported by the management agent; it is not a second manually maintained hardware database.

The deployable-model catalog records model family, source/model card, license, parameter count, context size, precision, minimum/recommended GPU memory, system RAM, disk, GPU count, capabilities and notes. The page evaluates those requirements against the live free resources and reports `fits`, `tight`, `insufficient` or `unknown` with an explanation.

Compatibility is a planning heuristic, not a benchmark guarantee. KV cache, context length, quantization, tensor parallelism, concurrent sequences and runtime versions materially change memory use and performance. Production capacity still needs representative benchmarking.

## Managed lifecycle

```text
install -> pull configured vLLM image
        -> optionally prefetch model weights into the shared Hugging Face cache
        -> register installation in the agent

start   -> create a dedicated vLLM container and host port
        -> wait for /health
        -> return RuntimeBaseAddress
        -> LlmProxy enables routing

stop    -> LlmProxy disables routing
        -> agent stops the container

remove  -> LlmProxy disables routing
        -> agent removes the container and installation record
```

Removing an installation does not automatically delete the shared Hugging Face cache. Another runtime may reuse the same weights, so cache cleanup is an explicit host-operator action.

## Preparing a Linux node

Required:

1. Linux on x86_64 or ARM64.
2. Docker Engine with a working daemon.
3. For NVIDIA GPUs, a supported driver, working `nvidia-smi`, and NVIDIA Container Toolkit configured so `docker run --gpus all ...` works.
4. Enough local disk for the vLLM image and model weights.
5. Network access to the model source when weights are not already cached.
6. TCP/9900 reachable from the LlmProxy control plane only.
7. The managed runtime port range (default begins at 18000) reachable from the LlmProxy gateway.
8. A stable hostname/IP for `NodeAgent__AdvertiseHost`.

Do not expose the node-agent port publicly.

## Installing from a GitHub Release

Immutable releases publish self-contained agent archives for both architectures:

```text
llmproxy-node-agent-<version>-linux-x64.tar.gz
llmproxy-node-agent-<version>-linux-arm64.tar.gz
```

Verify the adjacent SHA-256 file, extract the archive, then run:

```bash
sudo bash install-node-agent.sh
```

The installer puts the agent under `/opt/llmproxy-node-agent`, creates `/etc/llmproxy/node-agent.env` from the example if necessary, installs the systemd unit, and leaves the service disabled when placeholders are still present.

Edit `/etc/llmproxy/node-agent.env`. At minimum set:

```dotenv
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:9900
NodeAgent__BearerToken=<long-random-secret>
NodeAgent__AdvertiseHost=<IP-or-DNS-name-reachable-from-LlmProxy>
```

Then:

```bash
sudo systemctl enable --now llmproxy-node-agent
sudo systemctl status llmproxy-node-agent
curl -H "Authorization: Bearer <secret>" http://127.0.0.1:9900/health
```

Enter the same bearer once in **Infrastructure → Inventory & model lifecycle**. LlmProxy stores it encrypted using `Security:UpstreamCredentialEncryptionKey`; it is never returned by the management API.

## Consolidating legacy same-host rows

Older configuration could create a separate node row for System One even when its runtime was just another port on the same server. **Infrastructure → Fleet & access → Consolidate** provides the explicit upgrade path: LlmProxy establishes a distributed maintenance drain, waits for active work to reach zero, moves the source deployments to the selected physical hardware, preserves their runtime root and encrypted upstream bearer, then removes the duplicate node row.

If a source deployment previously inherited the old node concurrency, consolidation writes that value as an explicit deployment ceiling so the move does not silently increase that model's concurrency. The target node's physical ceiling then applies across all consolidated deployments. Agent-managed installations are deliberately excluded because their agent ownership must be changed through the lifecycle workflow instead.

For new legacy System One imports, if the configured runtime hostname already matches a registered hardware node, startup attaches the classifier deployment to that hardware directly and keeps the System One port/bearer at deployment scope.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `NodeAgent__BearerToken` | none | Required in Production. |
| `NodeAgent__AdvertiseHost` | machine name | Host/IP returned in model runtime endpoints. Set explicitly in production. |
| `NodeAgent__DockerExecutable` | `docker` | Docker CLI. |
| `NodeAgent__DockerImage` | `vllm/vllm-openai:latest` | vLLM image. Pin a tested immutable tag/digest in production. |
| `NodeAgent__DataDirectory` | `/var/lib/llmproxy-node-agent` | Agent state. |
| `NodeAgent__ModelCacheDirectory` | `/var/lib/llmproxy-node-agent/huggingface` | Shared model cache. |
| `NodeAgent__PortStart` | `18000` | First managed host port. |
| `NodeAgent__UseNvidiaGpus` | `true` | Adds `--gpus all`. |
| `NodeAgent__PrefetchModels` | `true` | Downloads weights during install. |
| `NodeAgent__CommandTimeoutMinutes` | `60` | Docker pull/prefetch timeout. |
| `NodeAgent__StartupTimeoutMinutes` | `20` | vLLM startup-health timeout. |

Production should pin `NodeAgent__DockerImage` to a vLLM tag or digest validated with the target GPU driver and models rather than relying on `latest`.

## Agent API

All endpoints require the configured bearer when one is configured.

```http
GET    /health
GET    /v1/system
GET    /v1/models
POST   /v1/models/install
POST   /v1/models/{installationId}/start
POST   /v1/models/{installationId}/stop
DELETE /v1/models/{installationId}
```

The agent is not a general remote shell. Its HTTP contract is limited to hardware inventory and the model lifecycle implemented by LlmProxy.

## Security

- Use a long random bearer per managed node.
- Keep port 9900 on a private management network or firewall allow-list.
- Restrict model runtime ports to the LlmProxy gateway network.
- Preserve `Security:UpstreamCredentialEncryptionKey`.
- Runtime arguments are passed as process argument-list entries, not shell-expanded commands.
- Production refuses to start the node agent without a bearer.
- Admin lifecycle actions require Admin write authorization when Entra is enabled and are audited.
- The catalog is curated metadata; it does not execute arbitrary user-provided model repositories.

## Routing example

```text
Hardware A: model-1, model-2
Hardware B: model-1
Hardware C: model-2
Hardware D: model-2
Hardware E: model-2
```

A request for `model-1` can route only to A/B. A request for `model-2` can route only to A/C/D/E. If A hosts the runtimes on different ports, deployment-specific runtime addresses keep the pools separate even though the physical node is shared.

## Multi-runtime launch profiles (initial implementation, 2026-10-09)

The managed Node Agent supports `vllm` (Hugging Face/Transformers weights) and experimental `llama.cpp` (GGUF checkpoints) plus `sglang` (curated Transformers/AWQ checkpoints). The curated catalog assigns each model to its allowed runtime: regular Qwen/Mistral models remain on vLLM, and `qwen3-4b-gguf-q4` is an experimental Q4_K_M GGUF deployment. Do not pass a standard Transformers checkpoint to llama.cpp or silently transform model formats. SGLang has an experimental managed launcher for a curated AWQ checkpoint; AirLLM is not installed or exposed yet.

On the Admin **Infrastructure → Inventory & model lifecycle → Deploy models** tab, the installation dialog provides `maxNumSeqs`, `maxModelLen` and (for vLLM) `kvCacheDtype` / `cpuOffloadGiB`. The Node Agent stores the chosen profile in its installation registry and selects runtime-specific Docker images and flags. For llama.cpp, `maxModelLen` is the **total** context pool shared by concurrent slots; vLLM `maxModelLen` is a per-request model limit. The engine is not silently changed for existing installations, and an attempt to reinstall the same catalog id with a different profile is rejected until the old deployment is stopped and removed. Profiles do not change distributed physical/request concurrency limits.

`NodeAgent__DockerImage` is the vLLM image. `NodeAgent__LlamaCppDockerImage` defaults to `ghcr.io/ggml-org/llama.cpp:server-cuda` for NVIDIA systems. **Pin both to immutable tested tags/digests in production**, validate the host CUDA driver, and use the CPU `:server` variant with `NodeAgent__UseNvidiaGpus=false` on a CPU-only test node. GGUF weights are pulled by llama.cpp on first server start and stored in the shared agent cache, not by the vLLM Python prefetch step. A GGUF lazy download may exceed normal startup times; set `NodeAgent__StartupTimeoutMinutes` accordingly. `llama-server` listens inside the container on port 8080, vLLM on 8000, both with the existing host-port management and /health contract.

`maxNumSeqs` and `--parallel` are runtime execution controls, **not** promises of sustainable user concurrency. Benchmark direct vs gateway at 1,2,4,8,12,16 before changing deployment/node MaxConcurrency. An installation is not considered production validated without Chat, Responses, SSE, tool calls, cancellation, actual model load and concurrent-load acceptance. AirLLM needs a separate HTTP adapter and model-specific validation.

### Quantized baseline and SGLang pilot

The catalog now includes the official `Qwen/Qwen3-4B-AWQ` and `Qwen/Qwen3-8B-AWQ` 4-bit checkpoints for vLLM. These are distinct logical catalog entries from BF16; no lossy on-the-fly conversion is implied. `qwen3-4b-awq-sglang` is an **experimental** third engine using the same official AWQ checkpoint for a measured A/B test. For SGLang, Node Agent starts the managed image with `python3 -m sglang.launch_server --model-path ... --port 30000`, configures max running requests and context length, and exposes `/health` plus OpenAI-compatible endpoints. Validate the image entrypoint/flags on the actual tagged runtime before production.

The image can be configured via `NodeAgent__SglangDockerImage` (default `lmsysorg/sglang:latest`, **pin to a tested digest** before rollout). The vLLM `kvCacheDtype` and `cpuOffloadGiB` fields are explicitly not applied to SGLang. SGLang telemetry does not yet feed the vLLM-specific runtime-pressure parser; routing relies on common queue/load and gateway capacity signals until a dedicated metrics adapter is tested.

Suggested baseline matrix: Qwen3 4B BF16 vLLM vs AWQ vLLM vs AWQ SGLang vs Q4_K_M GGUF llama.cpp. Test 1,2,4,8,12,16 concurrent requests with identical input/output token profiles; record aggregate output tokens/s, per-request token/s, p95 TTFT, OOM, batch behavior and KV cache. Do not infer 12 concurrent users just from `maxNumSeqs=12`.

### Multiple runtime profiles per model/node (2026-10-09)

An installation is idempotent for the exact catalog model, runtime, checkpoint, runtime flags and (if supplied) explicit port. Different profiles use separate installation IDs, ports and Docker containers, and the gateway registers distinct deployment IDs. Every deployment still consumes the **same physical node capacity lease**: this does not multiply GPU capacity and concurrent models can exhaust VRAM.

Migration `20261009223000_AllowManagedDeploymentProfiles` changes the PostgreSQL uniqueness constraint: manual/unmanaged deployments remain unique for (NodeId, ModelId), while managed deployments are unique per (NodeId, ManagedInstallationId). Existing installations survive unchanged. Downgrading is refused if multiple profiles would violate the old constraint.

Two variants may share the same logical model only if they expose the same provider model identifier. Different checkpoint/model identifiers should have different logical aliases until per-deployment provider-model overrides are supported. Stop, start and remove operate on distinct installation IDs; do not assume a configured `maxNumSeqs` number is a sustainable user count.

### Deployment-level runtime pressure telemetry

Every enabled Agent-managed runtime with a configured runtime root exposes its own sampled telemetry in **Infrastructure → Inventory & model lifecycle → Installed models**. An independent collector reads `<runtime-root>/metrics` with the configured upstream bearer. vLLM, SGLang and llama.cpp metric names are parsed into per-deployment running/queued requests, optional cache utilization and optional token counters. For llama.cpp, Node Agent now adds `--metrics` to launch arguments. A missing metric remains unknown, not zero.

This telemetry is **observational only**: it does not alter routing score or physical-node admission rules. The old node-root vLLM metrics collector remains intact for current routing behavior. The dedicated collector uses a bounded 2 MiB response and drops stale installation IDs; external runtime endpoint traffic must remain on the trusted private network.

### Admin install aliases and benchmarking controls

The installation dialog now exposes an optional logical model alias and explicit host port. Reusing a logical alias connects profiles to the same logical routing pool only when their provider model name matches; the gateway rejects an alias collision with a different provider ID. An explicit new port can request another instance of the same launch profile. Each row in **Installed models** has a Benchmark command for a running runtime, using the existing repository CLI (direct-runtime baseline). Repeat through the gateway with the logical alias and key provided via an environment variable. The benchmark launcher does not run on the Admin host and does not automatically apply capacity limits.

## Agent onboarding without SSH after installation (2026-10-09)

The **Infrastructure → Fleet & access → Pair Linux agent** wizard generates a 30-minute one-time enrollment invitation and an installation command. The Linux bootstrap in `distribution/connect-node.sh` resolves the latest immutable release, detects x64/ARM64, verifies its published SHA-256, installs the systemd service, generates its private management bearer and connects automatically. An Agent with Docker/NVIDIA prerequisites available then appears as a new hardware node, with separate connection status and inference-health status. The Agent credentials are generated on successful enrollment, stored root-only on the host and hashed in PostgreSQL; the invitation is not retained after pairing. Reinstalling the same Agent with the same state directory preserves its node identity.

Two management paths exist:

- **Direct:** the gateway can contact the Node Agent's private management address and deployed model runtime ports. This requires network reachability, and the default advertised hostname may need a correct private DNS/host address.
- **Outbound (WSS):** the Agent initiates HTTPS registration + heartbeat and a long-lived authenticated WebSocket to LlmProxy. The control plane routes management operations and SSE HTTP inference through that socket to the Agent's locally managed ports, so the remote firewall needs **outbound HTTPS/WSS only**, no incoming port 9900 or runtime ports. A private gateway virtual hostname selects the Agent, not public DNS.

An agent heartbeat is not inference readiness: the physical node must still have a started, healthy serving runtime before it can accept model requests. Admin displays Agent connected/offline and whether the relay socket is available independently of model health.

**Limitations:** the first WSS bridge is **single gateway-replica scoped**. In multi-replica deployments, a request landing on another replica will fail closed. Run only one LlmProxy API replica for outbound nodes until a secure cross-replica relay is implemented and tested. Prompts and stream bytes are not stored in Redis or PostgreSQL by the relay. The public LlmProxy endpoint must terminate TLS and support HTTP WebSocket upgrades; remote nodes require outgoing network access to the proxy, Docker/image registries and model checkpoints. On-box prerequisites (Docker, GPU drivers and NVIDIA Container Toolkit where applicable) are currently separate from the Agent installation. Outbound authentication, model lifecycle and SSE must still be acceptance-tested on real remote hosts before production claims.

### Security: outbound listener and node revocation

When installed in outbound mode, the installer binds the Agent HTTP API to `127.0.0.1:9900`, not `0.0.0.0`. Runtime traffic and management are forwarded only over the authenticated outgoing WebSocket. In direct mode port 9900 listens on available interfaces but must be restricted to the trusted LlmProxy gateway at the firewall. Deleting a disabled, installation-free node revokes its long-lived credential digest and closes its active tunnel as part of the Admin deletion workflow.

### Distribution security

The Admin one-command bootstrap is served from the latest **immutable GitHub Release asset**, not mutable `main`, with an adjacent published `.sha256`. That bootstrap downloads each immutable architecture-specific Agent archive and independently checks the archive's official SHA-256 before execution. The bootstrap script itself is fetched through HTTPS from the release endpoint; organizations requiring an independent verification of the bootstrap can download and check the adjacent SHA-256 asset before executing it.
