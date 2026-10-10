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

## Priority AirLLM engine: source-installable lab adapter, target-hardware validation pending (2026-10-10)

AirLLM is a **P0 managed-runtime target** because its layer/expert weight streaming may make a larger model feasible on a limited-memory host, and its layer reuse/batching potential must be measured for real multi-user inference. The Agent allowlist now includes experimental `airllm`. Infrastructure provides a separate Qwen3-4B AirLLM catalog profile (non-streaming Chat only). An Agent release bundles a Dockerfile and Python adapter, builds the container on the target machine, and downloads weights into the Agent-managed cache. This path is source-implemented but has not yet passed real Linux GPU acceptance; do not describe it as production-ready. Implement a reproducible, pinned AirLLM serving image/HTTP adapter with explicit health, OpenAI Chat/Responses compatibility, SSE completion and cancellation; a bounded single-worker queue first, then experiment with safe batching, if supported. Expose its measured resource usage, effective throughput and sustainable concurrency alongside the vLLM baseline, rather than treating low GPU memory as proof of more simultaneous users. See [AirLLM concurrency experiment](airllm-concurrency-experiment.md).

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

### Credentialed remote inference

When a managed deployment uses a configured encrypted upstream bearer, the Gateway restores that token only for the outbound HTTP request. The Agent tunnel forwards the provider's `Authorization: Bearer` header inside authenticated WSS to the specific locally managed runtime port. The Agent's own management bearer remains independent and is generated locally; provider authorization never grants local management access.

### Node Agent update results

A detached Agent upgrade writes a permission-restricted status marker under `/var/lib/llmproxy-node-agent/update-status.json`. The next authenticated heartbeat forwards only its `running`/`succeeded`/`failed` flag and verified version to LlmProxy, which persists and displays the result alongside the Agent's current version. No console log or arbitrary process output is ingested. This avoids requiring SSH to determine whether an upgrade finished.

### Remaining production acceptance for remote Agents

The current source must be tested on at least two separate Linux hosts or networks using a real HTTPS/WSS ingress: Agent enrollment, heartbeat after reconnect, registry downloads, lifecycle and health, Chat Completions and Responses SSE and tool calls, cancellation under load, and staged/rollback Agent update. CI synthetically verifies tunnel framing but does not substitute for real network/GPU evidence. Remote models should be exercised initially behind **one** gateway API replica until distributed Agent session forwarding is implemented; this limitation is deliberately fail-closed.

### Agent packages in LLMProxy Admin

In **Infrastructure → Fleet & access → Pair Linux agent**, the Admin presents direct version-pinned GitHub Release download links for the checksum-verified Linux x64 and ARM64 Agent archives and the pairing installer, plus each respective SHA-256. The server resolves the latest immutable release through release discovery; the published archive is never repackaged with a reusable enrollment token. The time-limited pairing invitation is separate and must be generated for each new server. After first installation, ordinary changes and upgrades are managed in Admin; a disconnected server is still shown with its last known inventory.

## Multiple gateway API replicas with Redis (2026-10-10)

When `Redis:Enabled=true` all gateway API replicas share a transient redis-based Agent relay. Each connected outbound Agent session gets an owner key with a ten-second TTL. Requests sent to another replica reach that owner through authenticated, encrypted Redis pub/sub and are streamed back using ephemeral per-request response channels; the bridge does not store raw inference requests in persistent Redis data. A missing owner, saturated buffer, or failed transport errors the stream rather than silently returning an apparently complete response.

The encryption for the relay uses `SensitiveDataProtector` and the `Authentication:ApiKeyPepper` shared by gateway replicas. This secret must be identical across all gateway instances. Network access to Redis must be restricted and protected. Benchmark/reporting data remains metadata-only. Failover reconnects through Agent's existing WSS retry loop; active requests are not transparently resumed after a lost Agent socket.

## One-time automatic Linux preparation

The immutable Agent release contains `prepare-node-host.sh`. During initial installation the host preparer validates systemd and an operational Docker daemon. On Debian/Ubuntu systems it installs Docker through APT when absent; on RPM systems using DNF it attempts the standard distribution Docker packages. For NVIDIA hosts where a working driver is already detected, Debian/Ubuntu can also install the NVIDIA Container Toolkit from NVIDIA's signed APT repository and configure Docker's GPU runtime. No Linux kernel, NVIDIA GPU driver or reboot is automatically replaced; incompatible GPU drivers require a proper maintenance window and are reported in the Admin inventory rather than silently modified. CPU-only hardware is supported. The Agent inventory reports Docker installed/operational, NVIDIA driver/toolkit states and actionable issues.

### Host prerequisite repair from Admin

After initial enrollment, **Inventory & model lifecycle → Host prerequisites → Prepare / repair host** requests the Agent to run only its trusted, immutable-release-installed `prepare-node-host.sh` helper. The browser cannot submit arbitrary shell commands. The API audits the request and refuses to modify host packages while a deployment is enabled; the Admin must stop its models first. An explicit browser confirmation warns about Docker restarts. The Agent reports only success/error code and a fresh readiness inventory (never package manager output or secrets). A second concurrent prepare call is rejected. On unsupported systems, the UI explains that prerequisite remediation remains a host-specific operation.

### Dynamic GPU availability at model startup

At each managed model start, the Agent checks `nvidia-smi -L` and Docker's configured runtimes. It passes `--gpus all` only when both are actually ready. vLLM and SGLang startup fail with an actionable Admin repair message if GPU support is required and not available; llama.cpp may run in CPU mode. Repairing NVIDIA prerequisites no longer requires editing `NodeAgent__UseNvidiaGpus` or reinstalling the Agent; the next start checks the current machine state.


### Public Linux Agent downloads from the gateway domain (2026-10-10)

Anyone can download **the public, unconfigured installer** using `curl -fsSL https://<gateway-domain>/downloads/agent/connect-node.sh`. The file is copied into the gateway's public static wwwroot and served before its authentication middleware. Published Linux x64 and ARM64 Agent archives can be requested at `/downloads/agent/<version>/linux-x64.tar.gz` or `/downloads/agent/<version>/linux-arm64.tar.gz`, plus the corresponding `.sha256`, without logging in. These version-pinned public URLs redirect to official GitHub immutable release assets, so the Linux machine must also be able to reach GitHub to fetch the binaries. Every redirect is constructed from an explicit allowlist of file names and a version known to published releases; user-supplied arbitrary URLs are never accepted.

`GET /downloads/agent/version` publicly provides the latest stable release version. The public bootstrap retrieves that version, downloads the matching pinned archive and checksum using the configured `LLMPROXY_GATEWAY_URL`, and checks SHA-256 **before** invoking the systemd installer. Pairing is a separate authenticated registration using a **single-use invitation expiring after 30 minutes**, created only by an administrator. The invitation is never embedded in the distributable binary/script.

After enrollment, the Agent persists its node ID and credential at `/var/lib/llmproxy-node-agent/gateway-connection.json` with root-only file permissions and removes the one-use invitation from `/etc/llmproxy/node-agent.env`. `systemctl enable --now llmproxy-node-agent` installs a boot-enabled systemd unit with `Restart=always`. After a Linux reboot, it uses the persisted identity, sends a heartbeat approximately every ten seconds and reconnects an outbound WebSocket with a retry loop—**no second pairing**. Re-pairing is required only after deleting its local identity or revoking the node. Losing connectivity does not invalidate a previously paired node. If the gateway itself is reinstalled with a lost PostgreSQL enrollment database or its signing pepper is changed, restore the original backed-up configuration or perform an explicit re-pair.

For public bootstrap integrity, `/downloads/agent/connect-node.sh.sha256` is calculated against the exact static script in the deployed gateway image rather than a GitHub Release copy, which might belong to a different software version. Archives remain checked against the published immutable release SHA-256 assets.

The public version endpoint uses a five-minute cached GitHub release asset manifest, only lists immutable stable releases that contain both Agent archives, their SHA-256 files, and the bootstrap. This avoids scanning historical upgrade plans on every unauthenticated download request. The quickstart integration suite checks anonymous bootstrap download and validates the live gateway-hosted script against its own public checksum.


## Background transfer jobs and selectable runtime profiles (2026-10-10)

**Infrastructure → Inventory & model lifecycle → Deploy models** now queues downloads/installation on the paired Agent instead of holding an HTTP request open for Docker pulls. The job status is persisted under the Agent's protected data directory, remains queryable across browser sessions, and reports image-build/pull progress and per-file weight download progress, plus failure and cancellation states. An Agent reboot marks interrupted jobs explicitly; reissuing the installation preserves already cached image/model files. The Admin auto-finalizes completed jobs into logical-model deployments on its next successful polling cycle. Closing the UI does **not** stop an Agent job; however the registration in the gateway only occurs when Admin subsequently reconnects and finalizes it. Automated server-side reconciliation is future work.

A custom Hugging Face repository can be entered by Admin using an explicit repository ID (not a URL) and vLLM/SGLang/llama.cpp serving. The Admin must explicitly accept unverified model license, hardware sizing and features; custom models start with streaming/tool support **not declared**, and operator benchmarking is essential. AirLLM remains restricted to its curated Qwen3-4B experimental entry until more models pass compatibility testing.

Reported Docker percentage applies to a **single current image layer**, not overall pull; weight percentage is based on Hugging Face metadata for completed files and can stall while one large file downloads. Other operations can be indeterminate. Do not present any stage percentage as an overall wall-clock completion estimate.


## Dedicated per-model GPUs and Agent update progress (2026-10-10)

The Admin install profile can optionally select an allowlisted group of numeric NVIDIA GPU indices (for example `0` or `0,1`). They are carried in the persisted profile and used as Docker's explicit GPU device list rather than `--gpus all`. Validation rejects duplicate/malformed indices and tensor-parallel settings that request more GPUs than selected; actual device existence and availability must still be checked on the Linux host. This is GPU *selection*, **not** a GPU memory reservation system or proof of safe multi-tenant concurrency. AirLLM is experimental single-GPU.

Under Infrastructure → Fleet & access, Agent upgrade status now includes download/verification/extraction/install/health phases. A real archive-download percentage appears when GitHub returns a valid Content-Length; otherwise the UI explicitly avoids claiming a numerical percentage. Existing reboot and rollback behavior is preserved, and progress is sampled on the authenticated Agent heartbeat. Update download cancellation/resume and generalized progress for llama.cpp lazy GGUF downloads are not yet implemented.

Experimental AirLLM can be selected for unverified custom Hugging Face checkpoint IDs with explicit administrator acknowledgement. Its current adapter remains non-streaming Chat with one generator worker; no measured parallel efficiency is claimed.

