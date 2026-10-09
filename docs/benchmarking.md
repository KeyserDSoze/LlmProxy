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

## Evidence-based candidate recommendation (2026-10-09)

To evaluate a tentative interactive capacity automatically, pass an explicit latency SLO and (if runtime token usage is available) a per-slot output throughput floor:

```bash
dotnet run --project tests/performance/LlmProxy.Benchmark -- \
  --target http://localhost:8080 --model agic-code \
  --api-key-env LLMPROXY_API_KEY --stream true \
  --concurrency 1,2,4,8,12,16 --requests 40 --warmup 3 \
  --max-output-tokens 128 --max-p95-ttft-ms 5000 \
  --min-success-percent 99 --min-output-tps-per-slot 10
```

A request count smaller than the maximum concurrency is now rejected: it would not exercise the advertised number of simultaneous slots. To mark a level as passing, the evaluator also requires at least max(20, 2 × concurrency) attempted requests, the configured success rate, p95 TTFT and, when selected, aggregate output token/s divided by configured concurrency. The last is only a *conservative capacity proxy*, **not** a measured per-user decoding speed distribution. If a runtime does not report usage tokens, no per-slot TPS claim is made.

The serialized JSON includes the optional `recommendation` section with each level's pass/fail and reasons, plus the largest consecutively passing concurrency. This is **advisory**, not a production capacity guarantee or an automatic change of LLMProxy node/deployment limits. Repeat tests (and direct vs gateway) on identical model, hardware, quantization and realistic prompt/context profiles, then save the evidence manually in the Capacity Profile admin UI.

## Streaming completion integrity

An HTTP 200 or a first text delta is **not** a successful streaming inference by itself. The benchmark now requires an explicit `[DONE]` SSE marker, or `response.completed` for the Responses event format. `response.failed`, `response.incomplete` and structured SSE error payloads are failures even when later followed by `[DONE]`. Premature EOF is classified as `stream_incomplete` rather than successful output. This prevents incomplete streams at high concurrency from inflating success rates and capacity recommendations. This validation affects only the benchmarking client: the gateway still forwards SSE bytes without modifying them.

## Admin-triggered background benchmarks

**Infrastructure → Inventory & model lifecycle → Installed models → Benchmark** now schedules a persisted synthetic-only job (PostgreSQL). The API worker executes the repository's actual benchmark evaluator, not a simulated score: streaming success requires SSE completion, and tested concurrency is 1/2/4/8/12/16 with 40 requests each. Only one pending/running job per managed deployment is admitted, even across API replicas. Admin polls persisted status, error and JSON results, with the SLO recommendation and an individual level breakdown.

If a tested concurrency level passes, the recommendation is stored as a deployment Capacity Profile, **but the active physical or deployment concurrency limit is not modified**. Admin must separately apply an accepted recommendation under Capacity & telemetry. The worker's synthetic prompt, bearer and request/response text are never written into benchmark reports; database stores only timing, usage, numeric results and errors. A non-responsive runtime can take up to thirty minutes to time out; a crashed worker's lock is reclaimed after thirty-five minutes.

Do not benchmark a production instance at saturation during active user traffic. A run can impose significant GPU pressure. The target is selected server-side from the managed deployment, never supplied as an arbitrary URL by the browser.
