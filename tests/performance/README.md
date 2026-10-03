# Performance and capacity tests

This folder contains load-generation and measurement tools. It is test tooling, not product runtime code.

## Benchmark harness

`LlmProxy.Benchmark` is a .NET 10 CLI that sends independent OpenAI-compatible requests to either LlmProxy or a direct vLLM service root. It supports Chat Completions and Responses, streaming/non-streaming execution, warm-up and a concurrency sweep.

The harness records only operational measurements. **Prompt bodies and bearer tokens are never written to the report.** A bearer token is read from an environment variable named with `--api-key-env`; there is intentionally no `--api-key` CLI argument so secrets do not land in shell history.

Example against LlmProxy:

```bash
export LLMPROXY_API_KEY='lp_...'

dotnet run --project tests/performance/LlmProxy.Benchmark -- \
  --target http://localhost:8080 \
  --model agic-code \
  --api-key-env LLMPROXY_API_KEY \
  --surface chat \
  --stream true \
  --concurrency 1,2,4,8 \
  --requests 20
```

Direct vLLM baseline:

```bash
dotnet run --project tests/performance/LlmProxy.Benchmark -- \
  --target http://10.0.0.21:8000/vllm \
  --model Qwen/provider-model \
  --surface chat \
  --stream true \
  --concurrency 1,2,4,8 \
  --requests 20
```

A target is always explicit. It may contain a path prefix and may optionally already end in `/v1`; the harness builds `/v1/chat/completions` or `/v1/responses` safely from it.

Outputs are written to `benchmark-results/` by default as JSON and CSV. Each concurrency level contains success/error counts, wall-clock throughput, p50/p95/p99 TTFT, p50/p95/p99 total duration and token throughput when upstream usage is available.

For streaming requests TTFT means the first actual output delta rather than merely the HTTP response headers or an assistant-role-only SSE chunk. For non-streaming requests it is the time to the first response-body byte.

## Safety

Do not point high concurrency levels at production unintentionally. Start low, benchmark one model at a time and watch vLLM plus DCGM telemetry. The CI pipeline compiles and unit-tests the benchmark harness but does **not** execute load against any inference endpoint.

Representative production-capacity runs belong on the target LAN close to the gateway/inference node so Internet latency is not mixed into the inference baseline unless that external path is intentionally what is being measured.
