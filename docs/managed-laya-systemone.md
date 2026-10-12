# Experimental managed System One / Laya (CPU-first)

This branch adds a curated, **not yet hardware-certified** Laya runtime using the upstream Apache-2.0 `convaiinnovations/laya` classifier and the upstream `laya[serve]==0.4.2` endpoint `/v1/systemone`. This is **not a Chat model** or a generic HF text-classification server.

## Safety contract
- The Agent only accepts `laya-english-cpu` paired with `convaiinnovations/laya`; no shell-supplied model commands, arbitrary repo or startup args.
- The installer records the HF source revision, downloads weights into the shared cache and the runtime resolves the exact **local snapshot**. `HF_HUB_OFFLINE=1` prevents silent new checkpoint revisions on restart.
- Laya is CPU-only until native GB10 ARM64 CUDA correctness and memory/performance acceptance. One server and one request at a time by default.
- Startup readiness is an actual typed classifier probe, not just the `GET /health` HTTP code (upstream /health can be 200 with nothing loaded).
- Logical model surface is `SystemOne`; requests route only to System One deployments. API keys, node capacity, audit and routing remain gateway-owned.
- Running containers are not automatically published to the gateway until the Admin installation finalization succeeds; server-side job finalization remains a distinct issue.

## Required acceptance before marking ready
1. Build this pinned Dockerfile on x64 and NVIDIA GB10 ARM64, and verify torch CPU wheel dependencies.
2. Install the model from Admin; inspect revision, free disk, download progress and correct persistent cache.
3. Start; assert /health reports `loaded` and the Agent probe returns real `answers` with probabilities.
4. Send a typed `POST /v1/systemone` through the public gateway. Check English answer schema, model isolation and refusal of Chat aliases.
5. Reboot, stop/start, uninstall, interrupted download, cancellation, outbound relay, concurrency and low-memory tests.
6. Test multilingual separately; this initial curated profile is **English only** and does not claim support for all Laya checkpoint variants.

Sources: https://github.com/NandhaKishorM/laya/blob/main/docs/http-api.md and https://huggingface.co/convaiinnovations/laya
