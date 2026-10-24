## 2026-10-25 — Surface stalled Agent upgrade with missing heartbeat (source; CI pending)

Real Spark fleet screenshot: Node remains `Draining`, Agent reported offline and old installed v0.2.57, requested v0.2.59, stage `updating`, tunnel has no owner. Verified that release 0.2.59 and ARM64 Agent asset are actually published. Updater status/heartbeat cannot be determined from the screenshot alone: inspect systemd `llmproxy-node-agent-update`, Node Agent service and `/var/lib/llmproxy-node-agent/update-status.json` locally on Spark. A successful 101 WebSocket from earlier is not proof of current connectivity.

The durable upgrade worker previously waited indefinitely in `updating` on absent heartbeats. It now marks `needs_attention` after >=12 minutes elapsed and >3 minutes without an Agent heartbeat, preserving the routing drain and never touching Docker containers. Any later verified heartbeat and healthy runtimes can still automatically complete the upgrade. Fleet UI provides local diagnostic commands and allows safe manual exit only with explicit acknowledgement, disabling model routing. No automatic unsafe GPU restarts or pairings.


## 2026-10-23 — Automatic safe Node Agent upgrade with inference routing drain (CI pending)

Admin → Infrastructure → Fleet & access → Update Agent no longer rejects the upgrade with HTTP 409 when a managed model has active routing. The Agent service can be replaced without stopping its Docker inference containers: stopping/restarting models would reload GPU memory and increase thermal risk (especially on DGX Spark). The update request uses the existing distributed node maintenance coordinator to block new inference, persists `Draining` on the physical node and a target+stage marker in PostgreSQL, and returns promptly. A durable gateway worker waits for all previously admitted requests to complete, then writes `DesiredAgentVersion` (only after drain), letting the old Node Agent trigger the existing detached, checksum-verified systemd updater. The worker waits for an authenticated fresh heartbeat proving either target version AND successful local version healthcheck or update failure/rollback. It verifies the Agent management `/health` identity, the installed model inventory, and real `/health` of every still-enabled Agent-managed runtime before reopening the node's routing. No Docker model is automatically stopped, started or retried. On failed verification the node stays Draining/blocked and Admin displays `needs_attention`; on a successful binary rollback, routing can be restored after the same checks. Durable stage `releasing` retries cleanup of Redis admission blocks across gateway restarts. Only the exact existing enabled deployments become route-eligible again; previously disabled deployments remain disabled. Active external unmanaged services are not modified.

The update action now asks for clear confirmation and exposes drain/upgrade/verification progress. Safety decision: never automatically cold-restart a heavy GPU container after a hardware thermal shutdown; no assumption that an Agent update fixes hardware issues.

## 2026-10-23 — Detached persistent GPU model Start jobs and retained failure diagnostics (CI pending)

User observed that LFM2.5 `Start` ends with `Runtime startup cancelled; container cleanup requested`, a generic proxy HTTP 502 and no surviving Docker container logs. The old Admin route waited synchronously for GPU Docker startup and model `/health`; the Node Agent tied the operation to a request cancellation token propagated through Cloudflare/WebSocket, so a transient disconnect aborted model initialization and deleted its container.

- New Agent-local persistent `start-jobs.json`: enqueue with POST and poll with GET over short authenticated relay calls. Starting continues through a browser disconnect or lost relay. On Agent restart, previously queued/running jobs are marked `interrupted`, **never restarted automatically** (GPU safety).
- Gateway enqueue and list routes; Admin installed-model status shows queued/starting/completed/failed, recent Docker error excerpts, explicit cancellation and a separate `/health`-verified routing activation after a completed start.
- New Start and Cancel endpoints require `AdminWrite` when Entra is enabled; status lists retain `AdminRead`.
- Failed startup attempts collect a short token-redacted Docker log tail *before* removing failed containers. Distinguish explicit cancellation from health readiness timeout. Model weights remain cached.
- UI must never show `Model started and enabled` immediately when a background job is merely queued.
- Safety: this doesn't resolve the earlier physical DGX Spark heat/shutdown; do not start the heavy GPU model until cooling/power diagnosis is complete. Real Spark validation pending.

## 2026-10-23 — Wrong physical node health target with enabled external System One deployment (source, CI pending)

Real Admin screenshots: `spark-1d52-a97bbb9e` has four routing deployments, three disabled Agent-managed models on virtual ports 18000/18002/18005 and one **enabled unmanaged** `systemone` classifier at `http://10.0.0.2:8002`. Previously `NodeHealthMonitor` loaded only enabled *managed* runtime roots, then treated *any* enabled deployment as a reason to fall back to `node.BaseAddress`. Consequently, it queried stopped Qwen3 `/runtime/18000/health` instead of the live configured classifier, forcing `Unhealthy` even when the classifier might be working. Fix health selection to check all enabled deployment-specific runtime roots and their credentials, using the legacy node root only for unmanaged deployments without overrides. A managed deployment without an address reports missing runtime rather than checking another model's port. When no deployments are enabled, continue checking the physical Agent `/management/health`. Unit cases explicitly cover external System One, managed port, missing managed root and legacy fallback.

This does **not** fix the independently observed outbound WebSocket disconnections (~10 seconds). The external classifier's reachability from the Gateway and whether it implements `/health` must still be tested; if it is offline, an `Unhealthy` result can be genuine. Do not create/enable GPU serving to mask an unhealthy external classifier.

## 2026-10-23 — Test ~10 second outbound WebSocket closures with a 5-second keepalive (CI pending)

The deployed Spark Agent v0.2.53 repeatedly reaches the Gateway v0.2.54 (`101 Switching Protocols`) but two captured Kestrel sessions terminate without a WebSocket Close frame after 10.34/10.44 seconds. This is not an enrollment or authentication failure. Neither Kestrel logs nor empty cloudflared logs prove whether the client, Cloudflare edge, cloudflared, intermediary NAT, or an origin proxy closed the TCP connection. Both application sides previously sent Ping/Pong only every **20 seconds**, so a 10-second idle intermediary can kill an unused session before the first heartbeat. Move Gateway and Agent WebSocket keepalive interval to **5 seconds** while retaining **15-second Pong deadline**; test with a paired rollout of both binaries and confirm whether session uptime improves beyond 10 seconds. A hard lifetime cap will not be fixed by pings; use network/Cloudflare diagnostics if disconnect persists. Do not start any GPU model during tunnel acceptance after Spark's unexplained thermal/power shutdown.

## 2026-10-23 — Stop polling stopped Agent runtime and trace WebSocket disconnects (source; CI pending)

Live Spark/Gateway logs for 0.2.54 show the Gateway healthy, Redis/PG healthy, repeated outbound WebSocket starts and old `/runtime/18000/metrics` requests even though the Qwen3 managed deployment is disabled. Fixed legacy node-scoped vLLM metrics collection so it only polls enabled, non-Agent-managed deployments. The independent enabled-managed-deployment metrics collector continues observing real runtimes. Added Gateway WebSocket attach/replacement/disconnect diagnostics, including session uptime, close code and categorized error reason; do not log node secrets or inference messages. Respond to peer Close frames with a proper close handshake instead of unconditionally aborting. These changes improve observability but DO NOT prove the root reason for current tunnel drops; correlate with Cloudflare ingress logs, Agent reconnects and duplicate Agent PIDs.

## 2026-10-23 — Recovering Admin from Spark outbound Agent disconnects (source; CI pending)

Observed after Agent v0.2.53 upgrade: heartbeat shows Agent connected and tunnel ready, but the model overview falls back to cached inventory with `Agent connection lost`; routing remains disabled and the legacy physical node's `BaseAddress` points to stopped Qwen3 at `/runtime/18000`. These signals measure different things; a heartbeat is not proof that the management request relay succeeded.

- The Admin node connection test selects a current enabled deployment runtime, or the **management Agent /health** when none is enabled; it never probes an old stopped model as proof that the Agent is unavailable. Result explicitly declares `probeMode: agent|inference`; inference endpoints are not advertised when no inference is active.
- Read-only GET relay requests retry briefly across WebSocket/Redis owner handoffs, while POST/PUT/DELETE cannot be replayed automatically. Cross-replica owner lease refresh is shorter, and relay GETs fail with an actionable timeout rather than silently waiting 30 minutes. Truncated management JSON is reported as an Agent connection error rather than an unhandled 500.
- Admin does not automatically enable stopped models. This change does not imply the Spark hardware shutdown or original tunnel drop was resolved. NodeAgent and Gateway logs remain necessary for root-cause proof.

## 2026-10-23 — GPU crash-loop prevention and temperature start gate (source; CI and real hardware acceptance pending)

A DGX Spark experienced severe heat followed by an abrupt physical shutdown while a large vLLM model repeatedly failed to become healthy. Cause of shutdown is **unverified**: power supply, ventilation, CPU/board and firmware must still be investigated. Software safeguards are not hardware thermal protection.

- Managed GPU-serving Docker containers (vLLM/SGLang/AirLLM/Diffusers and CUDA llama.cpp) are launched with `--restart=no`; this prevents unbounded crash/restart loops and means **manual Admin Start after reboot**. CPU-only media containers keep the previous persistence policy.
- Agent background reconciliation removes `unless-stopped` restart policy from preexisting registered GPU containers without killing them; untracked Docker workloads are untouched.
- A failed or timed-out runtime start requests removal of its own managed container while preserving cached model weights. The Agent records an error and an explicit new Start is required.
- Before new GPU starts, the Agent checks NVIDIA GPU temperature. Default block threshold is **80 Celsius**, adjustable through `NodeAgent__GpuStartMaxTemperatureCelsius` (60–100). An unavailable sensor is reported as unknown; opt-in `NodeAgent__RequireGpuTemperatureSensor=true` rejects starts when thermal data are unavailable. The GPU sensor is **not** a CPU, PSU or enclosure sensor and the Agent does not autonomously shut down a running hot server. Hardware safety remains the operator's responsibility.
- Admin Memory & processes shows temperature, configured start threshold, sensor availability and explanation. Tests cover hot/unknown/cool telemetry and runtime classification.
- After a physical thermal event, inspect power, ventilation, firmware and logs before attempting a restart; never treat a successful model health request as proof of hardware health.

## 2026-10-22 — DGX Spark GPU process attribution follow-up

Actual Spark `nvidia-smi` identified a host PID `llama-server` reserving 58,119 MiB (~56.8 GiB), even though only the LFM2.5 vLLM and Laya Docker containers were running. Docker stats alone would hide this critical clue. Extend the on-demand read-only Agent resource snapshot with structured NVIDIA compute PID/name/memory query and classic process-table fallback, with PID and reported memory rendered in Admin. No external PID termination endpoint is exposed. On GB10/NVIDIA UMA, process-reported GPU memory overlaps with RAM and Docker memory and must never be double counted. Identifying the owning model/systemd service still requires host inspection.

## 2026-10-22 — Spark RAM diagnosis and verified Docker stop (source, CI + hardware acceptance pending)
Stop now checks real Docker state twice before declaring a managed model stopped. Admin also offers explicit audited force-stop restricted to its own registered containers. On-demand Memory & processes lists Docker cgroup RAM usage, managed/external ownership, and recently logged vLLM requested/free/shortfall (historical, not current headroom). No host process kill, automatic victim selection, GPU reset or cache deletion. DGX Spark uses unified CPU/GPU memory: Docker cgroup RSS is not GPU-resident memory, and other host processes/file cache are not attributed. Real Spark/Agent test and exact-head CI remain pending.

# Project status / handover snapshot

## 2026-10-20 — Liquid AI LFM2.5 and context presets (source; CI/hardware acceptance pending)

LFM2.5-24B-A2B is now curated in the official Liquid AI BF16, FP8 and Q4_K_M GGUF formats, with 131,072 native tokens. The checkpoints have their own LFM Open License 1.0 terms. This catalog addition does not guarantee that the configured vLLM, SGLang or llama.cpp Docker image already implements the new lfm2_moe architecture, FP8 kernels or structured OpenAI tools/reasoning parsers. Real-host tests must prove Chat, SSE, tool-call parsing, memory, long context and restart behaviour before production routing.

On Admin → Models & inference → Install & manage models, installation has explicit 8/16/32/64/128K context presets bounded by the model's published native context and a "Use maximum" shortcut. Installed models can be stopped, edited without re-downloading weights, and restarted to apply the new persisted maxModelLen. The initial 8192-token setting remains conservative. For custom checkpoints the 262144-token Agent policy limit is explicitly unverified as a native model limit. For vLLM and SGLang maxModelLen is per request; llama.cpp --ctx-size is the total context pool shared across slots. Longer context can increase KV cache significantly and reduce sustainable concurrency; catalog memory estimates are not sufficient evidence for 128K operation.

Official sources:
- https://huggingface.co/LiquidAI/LFM2.5-24B-A2B
- https://huggingface.co/LiquidAI/LFM2.5-24B-A2B-FP8
- https://huggingface.co/LiquidAI/LFM2.5-24B-A2B-GGUF


## Persisted System One default and curated one-click start/publish — PR #39 combined CI pending (2026-10-13)

- Admin can persist the preferred System One logical alias, with deterministic fallback when an explicit request alias is absent. Public `/v1/systemone` still uses a typed classifier contract, not Chat.
- Curated runtime installs offer explicit download-only versus start/check/publish. Agent persists job intent and verifies a running runtime; Gateway reconciles only the matching live installation before enabling routing. Custom models cannot auto-start.
- Original PR #39 branch CI was green; merged tree with the latest capability-column UI requires fresh CI before release. Native Laya/Kokoro/Diffusers readiness and GB10/Spark inference remain EXTERNAL acceptance (#40).


## Managed runtime limits + HF Hub discovery — SOURCE / CI PENDING (2026-10-11)

- User requests larger context/output limits and ability to inspect defaults, adjust configuration at installation time or after stopping an installed runtime, and explicitly uninstall. AirLLM previous server maximum generated output was hardcoded 256; new `AIRLLM_MAX_OUTPUT_TOKENS` cap allows 1–4096 with shared prompt+completion context enforcement. Qwen3 native max 32,768 without unverified YaRN tuning. vLLM/SGLang/llama.cpp already receive per-request max_tokens; no supported global max output profile override.
- Version 0.2 AirLLM adapter image is built when required by a new/updated Agent without removing weights. Editing an installed runtime is permitted only when stopped and requests are validated at both Gateway and Agent; Agent registry persists changes across reboot. Changes take effect at next Start.
- New read-only Hugging Face Hub discovery uses trusted HTTPS endpoint, bounded sorting/search/limit and cache; models discovered are not guaranteed to install. A custom model still requires explicit license/sizing confirmation. Prefetched checkpoint file revisions are pinned for downloads and recorded in model registry; revisions are compared only where recorded. This reports cached revision (not guaranteed to be a fully pinned running container for every engine), does not install untrusted updates, and does not delete shared cache when uninstalling.
- Real Spark acceptance pending for >256 generated tokens and long context.

# Project status / handover snapshot

## Runtime-aware model capability declarations — SOURCE / CI PENDING (2026-10-11)

Qwen3-8B model card supports text Chat, tool calling and thinking, with 32K native context and up to 131K with appropriately configured YaRN. Our experimental AirLLM adapter only implements non-streaming text Chat, no structured tools, no vision and no per-request structured thinking controls; MAX_CONTEXT defaults to 8192 and Chat max_tokens is capped at 256. Admin exposes distinct model vs serving API capability reports, including source, model/serving context/output limits and safe copyable settings for third-party model configuration. Declared or potentially enabled features remain unverified until a live endpoint test proves them; the report never modifies third-party client settings or promises tool/SSE support from model weights alone. Exact known Qwen3 IDs are curated; unknown HF IDs remain unverified rather than guessed. Await backend and browser CI before release.

# Project status / handover snapshot

## Cached FITS with Agent offline and missing Admin URL routing — SOURCE / CI PENDING (2026-10-11)

The Spark model catalog shows Qwen3 4B FITS from cached GPU/RAM while the Agent is offline and no WebSocket tunnel exists. The Install button correctly requires `agentAvailable=true`, but the old UI silently disabled it; Admin now explains the block and links Fleet → Agent setup/recovery. Recovering the existing Spark node is a separate host-side step, not fixed by a catalog change. Admin views and nested workspace tabs now update the URL and support reload/history restoration.



## Per-hardware persistent Agent recovery and server-specific commands — IMPLEMENTED IN SOURCE / CI PENDING (2026-10-11)

Existing one-time enrollment invitations expire after 30 minutes and are not suitable for reinstall or repair. The API now provisions an encrypted, per-node recoverable token and supports audited AdminWrite reveal/rotation; /api/agent-connection/recover checks that token, preserves the original Node ID, updates the node's operational Agent secret, and does not create new physical nodes, models or deployments. Fleet shows the per-node Agent management/recovery modal and copyable latest-checksummed installer command bound to the existing ID. The Linux installer performs an atomic executable replacement and reboots the Agent service without erasing the data directory; after successful recovery the agent clears bootstrap keys while keeping a root-only gateway-connection.json across restarts. Fleet renders No runtime separately from connectivity for paired machines with the inert 127.0.0.1:1 bootstrap address. Actual existing Spark (401 expired invite) recovery end-to-end is still pending; do not consider it fixed on the hardware until verified.


## GB10 unified-memory discovery, sampled telemetry and experimental AirLLM ARM64 — SOURCE + CI TESTS; TARGET HARDWARE ACCEPTANCE PENDING (2026-10-10)

The paired DGX Spark `spark-1d52` exposes an NVIDIA GB10 with `nvidia-smi` memory.total/free both `[N/A]`. Previously the Agent discarded the device and returned `gpus: []`, making most model profiles falsely `insufficient`. The corrected Agent keeps the GB10 and enumerates other NVIDIA devices through `nvidia-smi -L` fallback, marks unified RAM distinct from dedicated VRAM, and samples CPU activity and available NVIDIA utilization/temperature/power. The Admin now displays GPU count/indices and refreshed metrics. Gateway fit for unified memory uses a single shared CPU+GPU resource budget with host reserve; it does not infer extra VRAM or promise actual model startup.

The experiment bundles a native ARM64 AirLLM Dockerfile based on NVIDIA PyTorch iGPU image, automatically chosen by the Agent, plus catalog profiles for Qwen3 4B/8B/32B and custom Hugging Face IDs. The embedded adapter supports only single-generator non-streaming Chat and bounded queueing, **not** production SSE/Responses/tools or proven parallel throughput. Real NVIDIA GB10 ARM64 Docker build, layer conversion and model generation **remain unverified**, including compatibility of the specific pinned base image. All software CI results must remain separate from physical GPU validation. See PR #10 and issue #11.


## More Admin-managed runtime/resource/download settings — IMPLEMENTED IN SOURCE / CI PENDING (2026-10-10)

Infrastructure now supports guarded custom **AirLLM** Hugging Face repositories in addition to the curated Qwen3-4B AirLLM profile. It records the selected runtime for custom-model deployments, so Admin can identify AirLLM and choose the non-streaming experimental benchmark even if the Node Agent is offline. The install wizard also supports optional comma-separated NVIDIA GPU IDs, validated by the Agent against duplicate/malformed device assignments and incompatible tensor-parallel cardinality. The runtime container is limited via Docker's `--gpus device=...`; hardware occupancy/VRAM is still **not reserved**, and concurrent models must not be assumed safe solely from device indexing.

Agent self-updates now report `preparing`, `downloading`, `verifying`, `extracting`, `switching`, `healthcheck`, and final status on authenticated heartbeat. When the published archive responds with Content-Length, the updater measures actual local downloaded bytes and the Admin Fleet view displays percentage; otherwise the phase remains visible with no invented number. Model download jobs similarly report image layer, file and completed-file percentages; llama.cpp GGUF lazy first-start downloads still require a dedicated progress integration. Full server-side job finalization, autonomous recovery, runtime live GPU contention telemetry, AirLLM SSE/tool-calling and actual device benchmarks remain future work.

## AirLLM managed lab adapter + Admin download orchestration — IMPLEMENTED IN SOURCE / CI AND REAL GPU VALIDATION PENDING (2026-10-10)

The Admin's Infrastructure install dialog now supports runtime selection (vLLM, SGLang, llama.cpp where model formats permit, and **experimental AirLLM** for a separate Qwen3-4B catalog entry) and custom Hugging Face repository IDs with explicit unverified-license/hardware opt-in. Install jobs are persisted on the paired Agent, continue after browser disconnect, report image/model weight stage progress and measured per-layer/file percentages, expose cancellation and retry of interrupted downloads, and are finalized into gateway deployment records when the Admin UI next observes completion. This **is not** a unified global cumulative percentage: Docker reports individual layer progress, and Hugging Face weights report completed bytes by file (which may stay unchanged during an individual large file download). Indeterminate stages are explicitly labelled, not guessed. Model files remain on the host after Agent reboot, but an interrupted job is marked interrupted rather than silently auto-resumed.

Experimental AirLLM 4.0.0 serving is now source-implemented as a locally built image bundled with Linux Agent releases, /health, /v1/models and non-streaming OpenAI Chat endpoints with one generator and a bounded queue. **It has not passed real NVIDIA Linux hardware validation; SSE, Responses API and tool calls remain unsupported**. The AirLLM catalog entry declares streaming/tools false; gateway routing must not conflate AirLLM with the resident-vLLM deployment for the same checkpoint. A true capacity claim requires same-host cold/warm 1/2/4/8/12/16-user benchmark comparisons. See [docs/airllm-concurrency-experiment.md](airllm-concurrency-experiment.md).

## Public domain-hosted Linux Agent distribution — IMPLEMENTED / VALIDATION PENDING (2026-10-10)

The Admin pairing wizard now offers an origin-derived public `curl` installer and direct public Agent archive URLs (x64/ARM64 and SHA-256). The gateway exposes only the inert installer via public StaticFiles and allowlisted version-pinned redirects to published official artifacts. Enrollment still requires a one-time 30-minute Admin invitation; systemd and root-protected locally persisted node credentials survive reboots without a new pairing. Exact-head CI and published-image acceptance remain to be verified.


## Installer-managed Linux host prerequisites — IMPLEMENTED / CI AND HARDWARE VALIDATION PENDING (2026-10-10)

The Agent archive now bundles a host-preparation script that provisions missing Docker via supported distribution packages, configures NVIDIA Container Toolkit on Debian/Ubuntu when a working NVIDIA driver exists, and reports Docker daemon/GPU runtime readiness to Admin. The installer never force-installs a GPU kernel driver or triggers an unattended reboot. Unsupported Linux distributions and missing drivers are surfaced as readiness issues, not claimed fixed.


## Redis multi-replica Agent transport — IMPLEMENTED / CI AND REMOTE ACCEPTANCE PENDING (2026-10-10)

With Redis coordination, gateway replicas publish short-lived owner leases for connected outbound Agent tunnels. Requests to another replica are forwarded through AES-GCM-protected transient Redis pub/sub channels with bounded per-request streaming chunks and cancellation, never persisted as Redis keys or logged. The Admin knows if a local or remote replica owns a tunnel. Without Redis the original single-replica route remains. CI/build, failover race and real cross-network load tests are required before claiming production HA.


## Remaining acceptance and HA constraints (2026-10-09)

The managed Agent can be paired across networks, remotely managed and controlled from Admin; Admin benchmarks and immutable Agent self-updates have been implemented. They remain subject to full CI and real Linux/GPU acceptance. **The WSS tunnel is still per-API-replica; Redis-backed cross-replica forwarding is NOT implemented in the released code.** A multi-replica cluster must not claim remote Agent inference HA until that backplane and soak tests are complete. Linux Docker/NVIDIA prerequisites still need host preparation, even though no CLI is needed for normal day-to-day model lifecycle operations after Agent setup.


## Node Agent immutable release update — IMPLEMENTED / CI PENDING (2026-10-09)

Admin can queue a newer published immutable Node Agent release. Each paired Agent reads a desired version in its authenticated heartbeat and schedules a detached systemd updater. The updater downloads archive+published SHA-256, stages the new executable tree, preserves config/cache/identity, checks service and local HTTP readiness and rolls back on startup failure. CI/static shell checks and actual host acceptance still needed. Cross-replica remote relay remains restricted to single API replica (distributed backplane not validated).


## Admin benchmark execution — IMPLEMENTED / CI PENDING (2026-10-09)

Added PostgreSQL-persisted per-deployment benchmark queue, atomic cross-replica worker claim, bounded real SLO sweep using existing library and UI results/status. The system records capacity evidence but never automatically modifies live limits. Must validate CI and target GPU before production use.


## Auto-enrollment and outbound reverse inference transport — IMPLEMENTED / VALIDATION IN PROGRESS (2026-10-09)

The Admin pairing wizard can generate a single-use, expiring enrollment code and an immutable release checksum-verified one-line Linux Agent installer. The Agent self-registers, persists a credential locally, heartbeats with inventory, and creates its own authenticated WebSocket for remote management and byte-preserving SSE inference even across unrelated networks/NAT. Existing direct management remains available. Agent connectivity is separate from model health. CI and real-host acceptance remain required; the current relay is gateway-replica-local, not HA or multi-replica routing.


## Benchmark SSE completion integrity — IMPLEMENTED / VALIDATION PENDING (2026-10-09)

Benchmark streaming success now requires a terminal SSE marker or Responses completion event; premature EOF and explicit failures are counted in the error breakdown, not as successful requests. Synthetic HTTP handler tests added. This closes a false-positive path in concurrency recommendations. Target-hardware acceptance and exact-head CI pending.


## Admin deployment alias and benchmark workflow — IMPLEMENTED / VALIDATION PENDING (2026-10-09)

Install modal now supports explicit logical alias / optional host port to create independently addressable profile instances and, when provider IDs match, combine their routing pool. Each running installation exposes a copyable direct-runtime benchmark command. No benchmark starts unattended or changes physical capacity. Awaiting frontend CI and target hardware measurements.


## Multi-runtime deployment metrics — IMPLEMENTED / VALIDATION PENDING (2026-10-09)

Added a separate Prometheus collector and tracker per Agent-managed deployment, normalizing vLLM, SGLang and llama.cpp gauges/counters into the Infrastructure Admin installation list. llama.cpp launch now enables Prometheus metrics. Metrics are observation-only and do not affect existing node-scoped routing scoring. Synthetic parser tests added; real runtime and exact-head CI validation pending.


## Multi-instance managed deployment — IMPLEMENTED / VALIDATION PENDING (2026-10-09)

The Agent supports several configurations of the same catalog checkpoint with different container IDs and ports; gateway registration keys on the managed installation ID. Filtered PostgreSQL uniqueness preserves manual-deployment deduplication and allows multiple managed profiles under the same physical node. Migration and metadata/profile tests added; real hardware validation and exact SHA CI still required. Physical Redis admission remains node aggregate, not multiplied by profile count.


## Benchmark SLO capacity evidence — IMPLEMENTED / VALIDATION PENDING (2026-10-09)

Benchmark CLI now rejects sweeps with fewer requests than declared concurrency and offers optional p95 TTFT, success-rate and per-slot throughput-proxy SLOs. JSON/console output includes advisory recommendations with per-level reasons and minimum sample requirements; no live capacity is changed. Synthetic unit tests added. Real hardware/A-B quantized throughput results remain pending.

## Multi-runtime deployment profiles — IMPLEMENTED / CI VALIDATION PENDING (2026-10-09)

Added managed vLLM execution controls (max simultaneous sequences, context, KV FP8/auto, CPU weight offload), official AWQ quantized vLLM checkpoints, and experimental llama.cpp GGUF plus SGLang runtimes in the Node Agent. Catalog, admin deployment dialog and persisted agent installation metadata carry runtime-specific profiles. The existing default vLLM lifecycle remains the baseline. New Node Agent unit coverage validates profiles. Actual inference hardware benchmark evidence, llama.cpp end-to-end smoke and exact-main CI are pending; AirLLM remains research work, not implemented. SGLang HTTP feature compatibility and telemetry require real hardware acceptance.


Last reviewed: **2026-10-09**.

This is the canonical current-state snapshot for LlmProxy. Read root `AGENTS.md` first.

## Streaming Request Audit normalization — IMPLEMENTED / VALIDATION IN PROGRESS

Streaming Chat Completions and Responses are now audited as one bounded, structured JSON response rather than a concatenation of token-level SSE messages. Incremental reconstruction tracks partial delivered content, tool calls, usage and terminal/interrupted/cancelled status; the client continues to receive the unmodified SSE stream. The Request Audit inspection dialog renders readable generated text or reconstructed JSON, with stream status/event count/truncation metadata. No old log migration or legacy SSE adapter is provided because this deployment is pre-production. Backend/React tests were added; exact-head CI/full-stack evidence is still required before marking this DONE.

## Physical infrastructure and capacity consolidation — IMPLEMENTED / VALIDATION IN PROGRESS

Current source makes the physical-capacity model explicit and removes three overlapping administrator entry points:

- **Infrastructure** now contains Fleet & access, Capacity & telemetry, and Inventory & model lifecycle tabs;
- physical `MaxConcurrency` is editable after node creation and is explicitly defined as simultaneous inference requests across the machine, not a user count;
- the live Capacity view reports distributed Redis node activity when coordination is available, while retaining the local gateway count as a diagnostic;
- per-deployment concurrency is editable independently and may inherit the hardware ceiling;
- legacy same-host rows such as a DGX runtime on `:8080` plus System One on `:8090` can be safely consolidated after a coordinated drain, preserving runtime roots, encrypted upstream credentials and the old deployment ceiling;
- future legacy System One import attaches to already-registered same-host hardware instead of creating a second physical-capacity pool;
- hardware inventory remains live CPU/RAM/disk/GPU/driver/runtime data supplied by the management agent.

Caller admission remains separate: **Users & Access** controls the admitted Entra population and **Usage & Governance** controls request/token quotas. There is no supported people-to-concurrency conversion; production concurrency still requires representative model/runtime/hardware benchmarks.

Local validation: Admin production build and 26 frontend unit tests pass. Local .NET execution is blocked by the runner's CoreCLR memory limit and Playwright Chromium download is blocked by the runner/CDN path, so exact-head GitHub CI/Full Stack remains required before this increment can be called validated or released.

## Request audit expansion — IMPLEMENTED / VALIDATION IN PROGRESS

Current source makes full-body inference auditing a first-class operator/user surface rather than an administrator-only content-log utility:

- Admin navigation now distinguishes **Request Audit** from **Administrative Audit**.
- Admin Request Audit supports server-side pagination and filters for user ownership, credential, logical model, surface, status, request ID and time range, plus exact request/response inspection.
- admitted normal users get **My request audit** and may inspect only rows whose `ApiCredentialId` belongs to one of their personal credentials owned by the same stable Entra `tid + oid`;
- another user's payload, an organization/shared credential payload, or a direct non-owned detail ID is never exposed through self-service;
- payloads remain application-encrypted at rest and decrypted detail responses remain `Cache-Control: no-store`;
- administrator-controlled full-body retention now spans 10 through 4015 days (11 x 365 days), defaults to 30 days and is cleaned every four hours.

Focused contracts: `docs/data-retention.md`, `docs/security.md`, `docs/user-access.md`, `docs/admin-observability.md`.

Validation required before marking DONE: backend build/unit, frontend/Vitest, Playwright E2E, Docker/PostgreSQL integration and distributed Full Stack CI on the exact resulting main SHA.

## Safe automatic updates + routed System One — IMPLEMENTED / VALIDATION IN PROGRESS

Current source now closes two control-plane gaps:

- administrator update policy is persisted as Manual, ASAP (five-minute checks), Nightly, Weekly or Monthly with configured time zone/time/day;
- automatic updates always select the latest stable release but submit every intermediate release in ascending order, preserving per-release custom migration plans;
- future manual `llmproxyctl update VERSION` resolves and executes the same safe stable-release chain instead of jumping directly to the target;
- bootstrap downloads retry transient GitHub/network failures while retaining SHA-256 verification;
- System One is a first-class model surface in the shared model/deployment/node catalog rather than a single global upstream URL;
- System One traffic uses the common routing, capacity, failover, request-rate governance, metrics and content-log correlation path;
- old `SYSTEM_ONE_*` configuration is imported once into normal topology records for upgrade compatibility;
- Models & Deployments and Playground now distinguish OpenAI versus System One surfaces and expose System One placement/routing controls.

Validation required before marking DONE: exact-head backend/frontend CI, full-stack System One compatibility smoke, distributed gate and immutable release publication.

## Administrator UX consolidation — IMPLEMENTED / VALIDATION IN PROGRESS

The current source consolidates the administrator experience around compact task surfaces:

- Inference Nodes defaults to active nodes; add-node and upstream-credential workflows are dialogs, while disabled idle nodes can be deleted safely after managed model installations have been removed.
- Hardware is split into Telemetry, Physical capacity and Benchmark profiles tabs; endpoint/profile creation no longer occupies the default page.
- Model & Hardware separates inventory from model deployment and exposes the management-agent installer directly.
- Models and Deployments are one bidirectional workspace for model → nodes and node → models, including quick deploy to another/all active nodes and routing enable/disable.
- API Credentials makes organization vs personal ownership explicit, retains administrator secret reveal/rotate/revoke, and new/rotated keys use `lp_org_` / `lp_usr_` prefixes.
- Request Metrics has a server-side filtered/paginated request browser beginning with the newest 20 requests.
- Playground, Usage & Governance, and Users & Access use tabs, with creation/configuration actions opened on demand in dialogs.
- Routing, Dashboard, Audit Trail, Help & Endpoints and the existing Release Notes/Updates workflow intentionally keep their current behavior for this increment.

Validation required before marking DONE: exact-head frontend/backend CI, distributed Full Stack acceptance and automatic immutable release publication.

## End-user provisioning / suspension — IMPLEMENTED, VALIDATION IN PROGRESS

The current PR now also adds a first-class platform-user registry and a new **Users & Access** administrator screen.

Behavior:

- global provisioning mode is either `manual` (default) or `automatic`;
- manual mode admits only administrator-censused, enabled Entra identities;
- automatic mode registers an authenticated Entra identity on first access to `/admin/me`;
- authorization identity is stable Entra `tid + oid`; email/UPN/display name are metadata only;
- existing personal-key owners are migrated into the registry at startup;
- administrators can disable a user, which blocks `/api/me/*` and `/admin/me` and revokes all active personal API keys for that owner;
- re-enable restores portal access without resurrecting revoked keys;
- the user portal is now **My dashboard** and shows latest request metadata as well as personal keys, own usage and limits;
- shared GitHub Copilot provider credentials remain workload/service identity, not guaranteed individual developer identity.

Focused contract: `docs/user-access.md`.

These changes were made after the previously validated observability head below, so they require a new exact-head CI/Full Stack pass before PR #1 can be promoted.

## Administrator observability / testing — DONE / VALIDATED

Branch / review:

```text
branch   feature/admin-observability-docs
PR       #1
state    exact feature head validated; ready for promotion
```

The validated increment adds:

- administrator-recoverable encrypted copies for newly created/rotated client API keys, with audited reveal and no-store responses;
- application-encrypted full request/response content logs for Chat Completions, Responses and System One;
- administrator-only live content-log UI with 2-second refresh, exact body inspection and copy controls;
- configurable full-body log retention from 10 through 180 days, default 30 days, with automatic cleanup every four hours and manual audited cleanup;
- an Admin Playground that tests enabled logical models through real routing/capacity admission and tests the configured System One classifier using an editable JSON body;
- a Help & Endpoints page with copy-ready client examples and platform flow documentation;
- a collapsed-by-default contextual documentation accordion on all principal Admin, Governance, Releases and User Portal screens;
- focused documentation in `docs/admin-observability.md`, plus API/security/retention/identity/governance contract updates.

Security boundary:

```text
request metrics / OTEL / audit      metadata-only
full prompt + response payloads     dedicated encrypted content-log store only
content-log readers                 LlmProxy.Admin / configured super admins only
API-key authentication              HMAC only
API-key recovery copy               encrypted at rest, admin reveal only
headers / bearer secrets            never copied into content logs
```

Exact validation evidence:

```text
validated head          6641739bd80f7eaf2b8a92594a5a75541c82546d
PR                      #1
CI                      37073147425 SUCCESS
Backend unit/build      SUCCESS
Frontend/Vitest         SUCCESS
Playwright E2E          SUCCESS
Docker/PostgreSQL       SUCCESS
Redis/OTEL full stack   SUCCESS
```

The Docker integration smoke explicitly exercised the administrator System One classifier diagnostic against the classifier mock, the administrator model-chat diagnostic through normal routing/capacity admission, bootstrap API-key reveal, encrypted full-body request/response inspection and the 10-180 day retention boundary. Promotion to `main` still requires the final documentation-only head to re-pass CI; the automatic release train then requires a green `main` CI for the exact merge SHA before publication.

## Administrator release update orchestration — DONE / VALIDATED

The current feature branch adds self-service control-plane update management for administrators:

- Release Notes discovers later stable immutable GitHub Releases and their published `llmproxy-update-plan.json`;
- each release declares a `standard` or `custom` update mode plus an operator-visible command;
- AdminWrite users can run an update immediately or schedule it for a specific time;
- pending jobs can be cancelled and recent outcomes are visible in the Admin UI;
- a bearer-authenticated systemd **LlmProxy Update Agent** runs on the Linux host outside the gateway container, so replacing the container does not terminate the update;
- scheduled job state persists under `/var/lib/llmproxy-update-agent`;
- custom release behavior is constrained to a checksum-covered `distribution/update.sh` entry point from the immutable bundle; no arbitrary shell command is accepted by the Admin API;
- normal `llmproxyctl update VERSION` uses the same release update-plan contract.

Focused contract: `docs/update-management.md`.

Validation evidence:

```text
validated feature head         301bb179c6b848dd245f30c185b923b09491792c
PR #5 CI                       37138750752 SUCCESS
Backend build/unit             SUCCESS
Frontend build/Vitest          SUCCESS
Playwright E2E                 SUCCESS
Docker/PostgreSQL integration  SUCCESS
Redis/OTEL/Grafana full stack  SUCCESS
```

The final documentation-status commit must itself re-pass CI before promotion to `main`.

## Current validated product baseline

Current distribution release contract:

```text
green main push -> automatic immutable release
initial version  -> v0.0.1
default bump     -> patch
explicit bump    -> release:minor / release:major
```

The older `0.2.0-preview.*` values remain source-history checkpoints rather than the generated distribution counter.


Validated runtime/release checkpoint:

```text
version                 0.2.0-preview.7
implementation          df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
CI                      35592623906 SUCCESS
Full stack              35592624282 SUCCESS
Publish GHCR            35593081824 SUCCESS
immutable image alias   sha-df3ecf7
image digest            sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
release artifact        10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

CI `35592623906` covers backend/unit/frontend/Playwright plus Docker/PostgreSQL regressives, including aggregate user-quota governance, restart republish, backup/restore and retention. Full Stack `35592624282` proves Redis/OpenTelemetry bootstrap, transactional outbox recovery, aggregate user request quota across two gateway replicas, distributed output-token budgets, cross-replica credential rotation and safe maintenance. Publish `35593081824` validates source CI before GHCR login and verifies the pushed OCI digest, SPDX SBOM and SLSA/BuildKit provenance.

The validated deployment image is `ghcr.io/keyserdsoze/llmproxy:sha-df3ecf7`. Later documentation-only commits may republish mutable `main` but do not replace this runtime checkpoint.

## Automatic immutable distribution — IMPLEMENTED / LIVE VALIDATION

The release layer now automatically turns each green `main` CI run into an immutable Linux distribution:

```text
main push
  -> CI (including Full Stack gate)
  -> automatic exact-SHA release tag
  -> tagged multi-arch GHCR image (linux/amd64 + linux/arm64)
  -> SPDX/SLSA verification
  -> checksummed Linux operator bundle
  -> GitHub Release
  -> bootstrap.sh
  -> /opt/llmproxy/releases/<version>
  -> llmproxyctl install/update/status/logs/doctor/rollback
```

Host-owned `/opt/llmproxy/.env` and Docker volumes remain outside versioned bundles. The release installer reuses `docker/scripts/install-linux.sh` and `deploy.sh`; it does not fork a second deployment implementation.

The distribution still installs LlmProxy/control-plane dependencies rather than llama.cpp/vLLM/model weights. Protected inference runtimes are now supported: nodes can carry an AES-GCM-encrypted write-only upstream bearer, and same-host llama.cpp is validated through Docker's bridge gateway instead of relying on a skipped precheck.

Validation still required before promotion:

- green CI and Full Stack on the exact candidate SHA;
- successful Buildx publication for both amd64 and arm64 plus existing SBOM/provenance verification;
- owner-triggered immutable tag and GitHub Release asset creation;
- first real install/update/rollback exercise on the GB10 ARM64 host, including bridge-bound llama.cpp and its upstream bearer credential.

## Product/versioning — DONE / VALIDATED

Version authority and product surfaces:

```text
Directory.Build.props                  compiled version
src/LlmProxy.Admin/package.json        bundled Admin version
GET /healthz                           runtime version
GET /api/admin/product                 product/release/build object
/admin/releases                        operator-visible release notes
CHANGELOG.md                            human-readable product history
docs/versioning.md                     release/version/build rules
```

Formal release sequence:

```text
0.1.0-preview.1  initial versioned product baseline
0.2.0-preview.1  historical usage rollups
0.2.0-preview.2  GHCR SBOM/provenance verification
0.2.0-preview.3  source-validated main/tag publication
0.2.0-preview.4  consolidated Linux production deployment + host installer
0.2.0-preview.5  production environment acceptance evidence
0.2.0-preview.6  Entra-owned personal API keys + user self-service
0.2.0-preview.7  aggregate Entra user request quotas
```

No immutable `v0.2.0-preview.7` Git tag or GitHub Release has been created. That remains an explicit product-owner publication action.

## Linux production deployment — DONE / VALIDATED FOR REPOSITORY PATH

Canonical operator runbook:

```text
docs/linux-production-deployment.md
```

Supported production topology:

```text
LlmProxy + PostgreSQL + Redis
+ OpenTelemetry Collector
+ Prometheus + Tempo + Loki + Grafana
+ optional Cloudflare Tunnel profile
```

Canonical first-install script:

```text
docker/scripts/install-linux.sh
```

It supports Docker official repository installation on Debian, Ubuntu, Fedora, CentOS and RHEL, controlled package-manager fallbacks for common `apt`, `dnf`/`yum`, `zypper`, `pacman` and `apk` families, preservation of existing working Docker + Compose v2, protected host-owned config, generated initial secrets, optional GHCR login, DGX precheck and invocation of the canonical `docker/scripts/deploy.sh`.

Production host state:

```text
/opt/llmproxy/.env
/opt/llmproxy/runtime/
/opt/llmproxy/backups/
/opt/llmproxy/acceptance/
```

Actual target-host package/service behavior is still external evidence; repository validation does not claim universal Linux package compatibility.

## Production environment acceptance — REPOSITORY DONE / REAL ENVIRONMENT EXTERNAL

Canonical manual command:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

Focused runbook:

```text
docs/environment-acceptance.md
```

Self-hosted Actions path:

```text
.github/workflows/environment-acceptance.yml
```

The harness records Linux/Docker/Compose metadata and validates direct VM -> DGX/vLLM plus LlmProxy gateway surfaces:

```text
/health or /healthz + /readyz
/v1/models
/v1/chat/completions
/v1/chat/completions stream=true
/v1/responses
/v1/responses stream=true
```

Direct provider-model visibility and gateway logical-model visibility are required. Canonical vLLM `/health` is a status-only check because a healthy vLLM server may return HTTP 200 with an empty body.

Evidence is deliberately metadata-only:

```text
summary.md
checks.tsv
```

Request bodies/prompts, source code, generated output, response bodies and API/bearer secrets are temporary only and are not copied into evidence. A final guard rejects evidence containing the gateway or optional DGX bearer value.

The GitHub Actions workflow:

- runs on `self-hosted, linux, x64, llmproxy-prod`;
- uses the `production` GitHub environment;
- has no API-key workflow-dispatch inputs;
- reads `/opt/llmproxy/.env` through the root-owned acceptance process;
- requires non-interactive sudo;
- rejects unexpected evidence files;
- uploads only `summary.md` and `checks.tsv` with 14-day retention;
- preserves a failed acceptance result after uploading available metadata evidence;
- deletes runner-local evidence afterward.

Repository CI proves the script and workflow repository path without pretending to prove the physical environment. The first real run on the target VM/DGX remains **EXTERNAL**.

## Supply-chain release evidence — DONE / VALIDATED

Every publication resolves one exact source SHA, requires successful `CI` push evidence before GHCR login, enforces tag/version match for exact tags, builds with source/version/date identity, emits SPDX + SLSA/BuildKit attestations, verifies the pushed OCI index and uploads `release-manifest.json`.

Validated `preview.7` runtime registry evidence:

```text
image                 ghcr.io/keyserdsoze/llmproxy
version                 0.2.0-preview.7
source                df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38
validating CI         35592623906 SUCCESS
Full Stack            35592624282 SUCCESS
Publish GHCR          35593081824 SUCCESS
image alias           sha-df3ecf7
image digest          sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99
attestation manifest    sha256:0da97b9a569aa974e9d77b5dd18d62082cde063fbf87221a908dc70d84fe60b8
SBOM predicate          https://spdx.dev/Document
provenance predicate    https://slsa.dev/provenance/v1
artifact              10635322261
artifact digest         sha256:d0884b5f48e2ecf00f55a0e52d153131b827f880f41306b89b9a31e8cd93e51b
```

## Core runtime scope — DONE FOR CURRENT MVP

```text
PostgreSQL = durable source of truth + runtime-state outbox + usage rollups
Redis      = distributed L2 + request/capacity/token-budget/maintenance coordination
local RAM  = per-gateway request-path configuration L1
```

Ordinary inference configuration lookup is DB-free after startup/runtime publication.

Current request path:

```text
OpenAI-compatible client / GitHub Copilot
  -> HMAC bearer credential from local L1
  -> UsageGroup + Entra owner + user/credential caller policy from local L1
  -> output-token reservation when configured
  -> request-rate admission
  -> logical model -> route catalog from local L1
  -> smart routing
  -> Redis/local physical-capacity admission
  -> vLLM
  -> output-token settlement
  -> metadata-only metric + OTEL telemetry
  -> application-encrypted full-body request audit (global Admin + owner-scoped personal self-service)
```

### Runtime state

Node/Model/Deployment/Credential/RatePolicy changes and runtime outbox rows commit in the same PostgreSQL transaction. A globally serialized advisory-lock worker publishes/retries Redis state, updates its L1 and marks rows processed only after acknowledged publication. Pending outbox rows are never retention-deleted.

### Safe maintenance

```http
GET  /api/admin/nodes/{id}/maintenance
POST /api/admin/nodes/{id}/maintenance/drain
POST /api/admin/nodes/{id}/maintenance/resume
```

Drain pre-blocks admission. Existing streams finish. Resume requires zero global active leases and successful `/health`, `/v1/models` and one-token warm-up validation. The legacy direct drain endpoint remains deprecated.

### Credentials and caller governance

Credentials use HMAC-SHA256 hashes and safe metadata for authentication. Newly created/rotated credentials additionally retain an application-encrypted recovery copy for administrator reveal/copy; plaintext is never stored. Administrator-created service credentials remain unowned. Personal credentials store stable Entra `OwnerTenantId + OwnerObjectId` and optional principal-name display metadata. In-place rotation preserves credential identity, Entra ownership, group/policy/history linkage while replacing hash/prefix/recovery ciphertext.

Entra roles are `LlmProxy.Admin`, `LlmProxy.User` and `LlmProxy.Reader`. Normal users use `/api/me/*` or `/admin/me` to create/list/rotate/revoke only their own keys and inspect own credential-attributed usage. Admin/Reader identity inventory is available under `/api/admin/identity`.

Caller governance includes credential/model request-rate counters and output-token budgets with pre-inference reservation, output-cap injection, known-usage refund and conservative uncertain-usage charging. Preview.7 adds aggregate Entra-user request-rate policies across all personal keys, keyed by stable `tid+oid` and optionally logical model. Applicable user and credential request counters are acquired atomically with AND semantics. Redis coordinates the aggregate counters across replicas; the request path remains DB-free.

Aggregate user output-token quotas, input/total-token quotas and monetary budgets remain requirements-driven; spend enforcement additionally requires an explicit pricing/chargeback model.

### Historical reporting / retention

Defaults:

```text
raw request metrics           90 days
daily usage rollups          730 days
audit events                 365 days
processed runtime outbox      30 days
full-body content logs         30 days default, configurable 10-4015 (11 years)
```

Complete expired UTC days roll up transactionally before raw deletion. A PostgreSQL advisory transaction lock serializes compaction across replicas. Reporting merges historical rollups with newer raw metrics without double counting.

### Backup / restore

PostgreSQL is durable recovery authority; Redis is rebuildable runtime state. Bash and PowerShell backup/restore operators are validated with destructive clean-target restore smokes. Plaintext API secrets are not in PostgreSQL; administrator recovery copies and full-body inference logs are stored only as application-encrypted ciphertext. `Authentication__ApiKeyPepper` is therefore both authentication and decryption/recovery material and must be preserved separately with deployment secrets.

### Routing / capacity / observability

Current MVP includes logical aliases, weighted least loaded / round robin / weighted round robin, health hysteresis, path-prefixed service roots, pre-response-only failover, vLLM pressure/EWMA feedback, benchmark-derived Capacity Profiles, distributed capacity leases with lease-loss cancellation, metadata-only OTEL/DCGM observability, and a separate encrypted administrator content-log store.

Prompts/source/generated output remain excluded from ordinary metrics, audit, OTEL and acceptance evidence. Exact request/response bodies are persisted only in the bounded-retention encrypted content-log store. API keys and bearer headers remain excluded everywhere except the dedicated encrypted credential-recovery ciphertext.

## Current development focus

The immediate step is first obtaining green exact-head CI for the new user-registry changes, then validating the first automatically generated `v0.0.x` releases and then continuing physical product acceptance:

1. get green CI + Full Stack on the exact `0.2.0-preview.8` source and validate tagged multi-architecture publication/release assets;
2. install/update/rollback the candidate on the actual ARM64 GB10 host;
2. install/validate the `llmproxy-prod` self-hosted GitHub Actions runner;
3. execute `.github/workflows/environment-acceptance.yml` against the real VM + DGX/vLLM and retain the metadata evidence artifact;
4. run real DGX benchmark sweeps + representative Copilot load and apply measured Capacity Profiles;
5. validate real Entra Admin/Reader roles plus manual/automatic platform-user admission, disable/re-enable, personal-key revocation, aggregate user request quotas and Cloudflare/public hostname;
6. validate GitHub Copilot BYOK end-to-end;
7. choose customer backup destination/encryption/retention and PostgreSQL/Redis/observability HA/storage;
8. create an immutable Git tag/GitHub Release only when explicitly requested.

## Identity limitation

A personal API key is attributable to its Entra owner through stable `tid + oid` plus the credential ID retained in usage telemetry/rollups. A centrally configured GitHub Copilot BYOK provider may still use one shared **service credential**; traffic through that shared key cannot reliably identify an individual GitHub user. Never infer identity from IP.

## Exact resume point

A new development session should:

1. read `AGENTS.md`, this file, `CHANGELOG.md`, `docs/versioning.md`, latest `docs/development-log.md`, `docs/roadmap.md` and focused docs;
2. inspect latest `main` and Actions before changing code;
3. treat `0.2.0-preview.7`, runtime source `df3ecf7cb4ab6a6ff99fa6ea21b1169c44f15a38`, CI `35592623906`, Full Stack `35592624282`, Publish `35593081824`, image alias `sha-df3ecf7` and digest `sha256:de82c1b7fa29b6d0b7104b1e5960316b6eeea81cf85a9d23c4fcc53ac2ae4d99` as the validated runtime baseline;
4. preserve the Entra `tid+oid` personal-key ownership contract, service-credential compatibility and atomic user+credential request-rate admission;
5. preserve transactional-outbox ordering, Redis fail-closed token/capacity semantics, safe maintenance admission, DB-free config lookup, rollup/raw no-double-counting and pre-response-only failover;
6. preserve metadata-only acceptance evidence and the bodyless vLLM `/health` status-only rule;
7. preserve the pre-GHCR source-validation gate and post-push SPDX/SLSA registry verification;
8. for new product/operator-visible behavior, follow `docs/versioning.md`;
9. update engineering docs/evidence after every meaningful increment.
