# AirLLM multi-user / layer-streaming experiment (P0)

**Decision: 2026-10-10.** AirLLM is a top-priority experimental inference runtime for LLMProxy, not an optional future investigation. Its key hypothesis is whether streaming a checkpoint **one layer (or required MoE expert) at a time** can make larger models usable and/or improve sustainable concurrent users on the **same physical Linux server**. These are two **different** hypotheses; both must be measured. Status: **PLANNED — no deployable AirLLM Node Agent adapter, packaged serving container, or validated multi-user result yet.**

## Upstream and scope

- Primary upstream is [lyogavin/airllm](https://github.com/lyogavin/airllm), which exposes Python \`airllm.AutoModel.from_pretrained\` and \`generate\`. **Do not assume** that installing the package installs an OpenAI-compatible HTTP serving daemon, or that a third-party fork implements the same API, security, batching, streaming or throughput.
- Evaluate a specific immutable upstream release + commit and pinned Python/CUDA/PyTorch/Transformers dependencies; document source, license, model support and a reproducible image digest. Upstream v4.0.0 is a research candidate as of 2026-10-10, not an automatically approved production baseline.
- Start with one **verified identical** checkpoint compatible with both AirLLM and vLLM; a Qwen-family instruct checkpoint is a candidate **only after a real compatibility check**. Test the same model revision, tokenizer, precision and generation parameters. If only different formats/quantizations work, label that comparison explicitly as *not same-checkpoint*.
- Target initial NVIDIA Linux x64/ARM64 hosts with usable driver/container GPU runtime, adequate fast local disk, network and enough system RAM. CPU-only and AMD/other accelerator profiles are **separate compatibility tracks**, not implicitly supported because the Node Agent can run there.

## Architectural work

1. **Serving adapter:** build an LLMProxy-owned experimental AirLLM container providing \`GET /health\`, \`GET /v1/models\`, \`POST /v1/chat/completions\` and a tested OpenAI Responses translation (or explicitly return a supported error while disabled). Support genuine, correctly terminated SSE and cancellation before declaring either compatible; never mark a prematurely terminated stream successful. When AirLLM produces whole generations only, report TTFT honestly and do not emit fake token streaming.
2. **Managed lifecycle:** extend runtime discriminators, Node Agent allowlists/launch profiles and the Admin catalog with opt-in \`airllm\`, pinned image and a dedicated checkpoint/cache/converted-layer directory. Hook install/start/stop/remove, immutable profiles, health checks, errors and audit into existing managed deployment/routing/Agent relay. Keep it experimental and disabled for general routing until acceptance is green.
3. **Bounded worker model:** begin with one model instance, one inference worker and an explicit finite FIFO queue, deadline and backpressure (429/503 with stable error semantics); log queue depth and active requests. Never naively execute shared model \`generate()\` concurrently from unrelated threads. Next compare (a) serial requests, (b) queued requests, (c) safe batching/layer-aware scheduling if AirLLM actually supports it for the chosen model, and (d) multiple isolated instances. Prevent out-of-memory and disk thrash with admission.
4. **Resource isolation:** sample GPU allocated/resident/peak memory, host RAM, working set, model shard disk size, cache usage, disk read throughput/IOPS, GPU utilization and optional PCIe transfer rate. Assign only allowed GPUs and enforce per-node budget; \`--gpus all\` is not suitable for a multi-tenant physical-capacity experiment. Limit active workers and track cache pressure.
5. **UI:** in Infrastructure allow a clearly marked *Experimental / AirLLM* runtime selection with model/quantization/revision, layer-shard cache, compression, max context, prefetch settings **only where supported**, resource requirements, execution limits and install progress; show warnings and prevent production enablement without an explicit policy.
6. **Reconciliation/security:** persist desired/observed installation state and benchmark metadata. Reject arbitrary shell commands, untrusted runtime images/host mounts and unreviewed model remote code. Encrypt optional repository credentials. Show cleanup of converted model shards separately from shared source weights.

## Reproducible multi-user benchmark

### Baseline

Use the **same physical server**, same checked checkpoint where possible, same prompt set and tokenizer, equal prompt/output token limits, and record:
- GPU architecture, number of GPUs, driver, compute capability, GPU RAM and RAM;
- CPU, SSD/NVMe type, free disk, disk bandwidth/IOPS, container runtime, image digest, cache state;
- exact model revision, quantization/precision, effective context and AirLLM/vLLM flags;
- cold installation + first model layer conversion, cold start, warm start and warm sustained run **separately**.

Compare vLLM resident loading, AirLLM layered loading, AirLLM optional compression (off/4bit/8bit only if actually supported), and optional llama.cpp / SGLang reference results. Use identical checkpoints when assessing *engine* differences. Repeat with one oversized checkpoint where AirLLM may be feasible and resident vLLM not feasible; call this a **feasibility** result, not a faster-engine claim.

### Load matrix and metrics

Run active clients **1, 2, 4, 8, 12, 16** (plus 24/32 only if healthy), per input length **512 and 2048 tokens** and output target **64 and 256 tokens**. Include at least 20 completed requests per level and three independent warmed repeats with deterministic prompt sets and separately marked cold-run data. Record:
- **completed, successful requests/minute**, aggregate actual output tokens/second, per-request tokens/second, queue wait, throughput vs offered load;
- latency p50/p95/p99, TTFT and time per output token *where genuine streaming is supported*, per-user fairness and timeout/cancellation/OOM rates;
- GPU VRAM idle/peak, CPU/RAM, disk reads/second, IOPS and shard/cache size, GPU utilization, warm/cold startup times;
- streaming protocol completion integrity, payload accuracy, prompt/model compatibility, tool calls/Responses explicitly marked **unsupported** until tested;
- direct runtime vs gateway (same workload) to distinguish AirLLM engine bottlenecks from LLMProxy relay or routing overhead.

Do not count merely accepted/queued clients as simultaneous *serving*. Sustainable concurrency is the highest level passing a declared profile, e.g. **success >=99%, p95 TTFT <=5 seconds (streaming only), p95 end-to-end latency <=60 seconds for the test prompt/output profile**, with no OOM and stable aggregate output throughput. These values are **initial adjustable experiment targets, not performance guarantees**; if AirLLM cannot meet them at concurrency 1, report that honestly instead of silently relaxing the score.

### Questions the results must answer

1. Does AirLLM run a checkpoint vLLM cannot keep resident on this exact server? At what token/s and cost in first-load/shard conversion?
2. With a checkpoint both support, is AirLLM's sustainable **simultaneous completed work** greater, equal or lower than vLLM? At which latency threshold?
3. Can layer-aware **batching** share weight loads across users, or does it merely queue requests and overload disk or system RAM? What does it do to throughput and fairness?
4. Does using two AirLLM instances on the same host help or worsen disk contention/VRAM versus a single scheduler?
5. Would hybrid deployment (vLLM for frequent/high-throughput models; AirLLM for rarely used or oversized models) yield better utilization than replacing vLLM wholesale?

## Acceptance before product promotion

- [ ] Lab image built reproducibly from a pinned reviewed release, published with version/checksum/digest and vulnerability review.
- [ ] Node Agent can install/start/stop/remove/recover **one** experimental AirLLM installation remotely via outbound Agent, with sandboxed resources and no arbitrary command execution.
- [ ] /health, /v1/models, OpenAI Chat non-streaming proven; streaming/Responses/cancellation/tool calling only advertised for independently validated features.
- [ ] Synthetic contract tests cover queue full, cancellation, deadlines, partial SSE and concurrent requests without needing physical GPU in every PR.
- [ ] Real Linux/NVIDIA test with one supported model; separate end-to-end and performance acceptance artifacts, no secrets or raw prompt bodies.
- [ ] Same-host AirLLM vs vLLM report covering client concurrency 1/2/4/8/12/16 and both cold and warm observations, with resource and throughput measurements.
- [ ] Admin shows measured recommendation, flags low-confidence results and requires human approval to change live physical/deployment capacity.

**Release rule:** Do not claim AirLLM is installable or improves throughput until the runtime/serving adapter exists, checks are green, and hardware experiments demonstrate it. Keep the existing vLLM/llama.cpp/SGLang paths unchanged until then.
