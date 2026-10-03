# Benchmarking and capacity profiling

LlmProxy capacity must be derived from measured inference node/vLLM behavior, not from the number of licensed developers. The benchmark harness under `tests/performance/` exists to produce repeatable evidence for concurrency limits and smart-routing calibration.

## What we measure

For each requested concurrency level the harness measures:

- attempted/succeeded/failed requests and success rate;
- wall-clock throughput in successful requests/second;
- p50/p95/p99 time to first output (TTFT);
- p50/p95/p99 end-to-end request duration;
- input/output/total token counts when the OpenAI-compatible upstream reports usage;
- aggregate output tokens/second;
- error breakdown by HTTP/transport/timeout category.

Streaming TTFT is captured at the first meaningful SSE output delta. Role-only/metadata events do not count as the first token. Non-streaming TTFT is the first response-body byte.

The report also records safe run metadata: target service root, model name, surface, streaming mode, concurrency, request count, maximum output tokens, prompt label and prompt character count. It does **not** record the prompt text or API credential.

## Gateway vs direct-vLLM baselines

Run two comparable profiles whenever possible.

### Direct vLLM

```text
benchmark client -> vLLM -> model
```

This establishes raw model/runtime capacity and latency.

### Through LlmProxy

```text
benchmark client -> LlmProxy -> routing/auth/telemetry -> vLLM -> model
```

This establishes production-path behavior and lets us quantify gateway overhead, routing distribution and failover behavior.

Use the same model, prompt profile, token limit, concurrency levels and network location when comparing the two paths.

## CLI usage

Show all arguments:

```bash
dotnet run --project tests/performance/LlmProxy.Benchmark -- --help
```

Gateway example:

```bash
export LLMPROXY_API_KEY='lp_...'

dotnet run --project tests/performance/LlmProxy.Benchmark -- \
  --target http://localhost:8080 \
  --model agic-code \
  --api-key-env LLMPROXY_API_KEY \
  --surface chat \
  --stream true \
  --concurrency 1,2,4,8,12,16 \
  --requests 20 \
  --warmup 3 \
  --max-output-tokens 128
```

Direct vLLM example with a path-prefixed service root:

```bash
dotnet run --project tests/performance/LlmProxy.Benchmark -- \
  --target http://10.0.0.21:8000/vllm \
  --model Qwen/provider-model \
  --surface chat \
  --stream true \
  --concurrency 1,2,4,8,12,16 \
  --requests 20
```

The target may be a root such as `http://host:8000/vllm` or may already end in `/v1`. Query strings, fragments and embedded credentials are rejected.

## Secrets and prompts

There is intentionally no `--api-key` argument. Use `--api-key-env` so the actual secret is not copied into shell history, command logs or generated reports.

The built-in prompt is a short synthetic coding task labelled `synthetic-coding-v1`. A custom prompt can be supplied with `--prompt-file`; report output stores only the safe `--prompt-label` (or `custom`) and character count.

Never use proprietary source code or production prompts in generic capacity runs unless the test environment and data handling have explicitly been approved.

## Output

The default output directory is `benchmark-results/`, which is git-ignored. Every run produces:

```text
llmproxy-benchmark-<run-id>.json
llmproxy-benchmark-<run-id>.csv
```

JSON is the canonical machine-readable report. CSV contains one row per concurrency level for quick spreadsheet/BI comparison.

## Recommended GPU inference hardware test protocol

For each model/deployment profile:

1. Start from a healthy, stable runtime with the intended vLLM model already loaded.
2. Record model/runtime configuration: quantization, context limit, tensor settings and vLLM flags.
3. Run direct-vLLM baseline at low concurrency.
4. Run the same sweep through LlmProxy.
5. Increase concurrency gradually; do not jump directly to a large number.
6. Watch LlmProxy request metrics, vLLM running/waiting/KV-cache signals and DCGM GPU/memory/temperature telemetry in parallel.
7. Repeat the run enough times to distinguish warm-up/noise from stable behavior.
8. Stop increasing concurrency when TTFT/error/queue behavior clearly violates the desired service level or the runtime becomes unstable.

Initial useful sweep candidates are `1,2,4,8,12,16`, then extend only if the model/hardware still has headroom. These are test points, not promised capacity.

## Capacity profile we want to derive

For each `(inference node class, provider model, runtime configuration)` we ultimately want evidence for:

```text
recommended max concurrency
p50/p95 TTFT at normal load
p95 total duration
sustainable output tokens/sec
queue depth at saturation
KV-cache behavior at saturation
failure/OOM boundary
GPU and framebuffer observations
```

These values can then inform node/deployment concurrency defaults and the persisted smart-routing tuning policy.

## Routing calibration

Do not tune routing weights merely to maximize GPU utilization. The objective is user-perceived service quality and stable throughput. Use benchmark evidence to decide whether changes to TTFT, queue, KV-cache or future hardware penalties improve the measured result.

Hardware telemetry remains observational until a repeatable benchmark demonstrates that a hardware threshold predicts degraded inference behavior better than the existing vLLM/application signals.

## CI behavior

CI must compile the benchmark console and execute its unit tests, including option validation, endpoint composition, percentile math and OpenAI usage/SSE-event parsing. CI must **not** run an actual load sweep against a remote or production endpoint.

Real benchmark runs are environment acceptance/performance activities and should be executed deliberately from the target network or a dedicated self-hosted runner once that environment is available.
