import type { Page, Route } from '@playwright/test'

/**
 * Deterministic Admin API used by the visual suite: two servers (one healthy
 * DGX Spark, one unhealthy), running/failed/stopped managed models — including
 * the llama.cpp "exactly one out metadata, path_model" startup failure — jobs,
 * access, governance, observability and release data.
 */
export const FIXED_NOW = '2026-11-12T10:00:00Z'

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })

const ggufFailure = [
  'Model container exited during startup with exit code 1. Automatic GPU restart is disabled; review the diagnosis below before retrying.',
  "Likely root cause: 0.00.750.554 E llama_model_load_from_file_impl: exactly one out metadata, path_model, and file must be defined",
  'What to do: llama.cpp started without a resolved GGUF file (repo/:QUANT not resolvable or downloadable at start). Start again: the Agent now downloads and pins the exact GGUF first and reports the precise cause if it cannot.',
  'Full bounded startup log saved on the inference host: /var/lib/llmproxy-node-agent/startup-logs/custom-01ea8a84.log',
  'Last Docker log lines:',
  '0.00.750.571 E common_fit_params: encountered an error while trying to fit params to free device memory: failed to load model',
  "0.00.750.578 E cmn common_init_: failed to load model ''",
  "0.00.750.580 E srv load_model: failed to load model, ''",
  '0.00.751.477 E srv server_run_i: exiting due to model loading error'
].join('\n')

export const nodes = [
  {
    id: 'node-1', name: 'spark-01', baseAddress: 'http://agent-node-1.llmproxy.invalid/runtime/18000',
    hardwareMetricsBaseAddress: 'http://10.0.0.21:9400', managementBaseAddress: 'http://agent-node-1.llmproxy.invalid',
    hasManagementCredential: true, hasUpstreamCredential: false, weight: 1, maxConcurrency: 8, enabled: true,
    status: 'Healthy', lastHealthCheckUtc: '2026-11-12T09:59:50Z', lastHealthyAtUtc: '2026-11-12T09:59:50Z',
    lastHealthLatencyMilliseconds: 12, lastHealthError: null, consecutiveHealthSuccesses: 42, consecutiveHealthFailures: 0
  },
  {
    id: 'node-2', name: 'spark-02', baseAddress: 'http://10.0.0.22:8000', hardwareMetricsBaseAddress: null,
    managementBaseAddress: null, hasManagementCredential: false, hasUpstreamCredential: false, weight: 1,
    maxConcurrency: 4, enabled: true, status: 'Unhealthy', lastHealthCheckUtc: '2026-11-12T09:59:40Z',
    lastHealthyAtUtc: '2026-11-11T22:10:00Z', lastHealthLatencyMilliseconds: null,
    lastHealthError: 'Connection refused (10.0.0.22:8000)', consecutiveHealthSuccesses: 0, consecutiveHealthFailures: 18
  }
]

export const models = [
  { id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'Qwen/Qwen3-8B', supportsStreaming: true, supportsTools: true, surface: 'OpenAi', enabled: true },
  { id: 'model-2', publicName: 'gemma-vision-gguf', providerModelName: 'unsloth/gemma-3-4b-it-GGUF:Q4_K_M', supportsStreaming: true, supportsTools: false, surface: 'OpenAi', enabled: true },
  { id: 'model-3', publicName: 'systemone-laya', providerModelName: 'convaiinnovations/laya', supportsStreaming: false, supportsTools: false, surface: 'SystemOne', enabled: true }
]

export const deployments = [
  { id: 'deployment-1', nodeId: 'node-1', modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4, managedInstallationId: 'qwen3-8b-1a2b3c4d', catalogModelId: 'qwen3-8b', recommendedMaxConcurrency: 6, benchmarkP95TtftMilliseconds: 420, benchmarkP95DurationMilliseconds: 5200, sustainableOutputTokensPerSecond: 210, benchmarkSource: 'benchmark-results/qwen3-8b.json', benchmarkMeasuredAtUtc: '2026-11-10T12:00:00Z' },
  { id: 'deployment-2', nodeId: 'node-1', modelId: 'model-2', enabled: false, weight: 1, maxConcurrency: null, managedInstallationId: 'custom-01ea8a84', catalogModelId: 'custom', recommendedMaxConcurrency: null, benchmarkP95TtftMilliseconds: null, benchmarkP95DurationMilliseconds: null, sustainableOutputTokensPerSecond: null, benchmarkSource: null, benchmarkMeasuredAtUtc: null },
  { id: 'deployment-3', nodeId: 'node-1', modelId: 'model-3', enabled: true, weight: 1, maxConcurrency: 2, runtimeBaseAddress: 'http://10.0.0.2:8002', managedInstallationId: null, catalogModelId: null, recommendedMaxConcurrency: null, benchmarkP95TtftMilliseconds: null, benchmarkP95DurationMilliseconds: null, sustainableOutputTokensPerSecond: null, benchmarkSource: null, benchmarkMeasuredAtUtc: null }
]

const feature = (model: string, serving: string, explanation: string) => ({ model, serving, explanation })
const chatCapabilities = (providerModelName: string, runtime: string) => ({
  providerModelName, runtime, source: 'Curated model card + runtime contract',
  modelCardUrl: 'https://huggingface.co/' + providerModelName.split(':')[0],
  chat: feature('supported', 'supported', 'OpenAI Chat Completions'),
  toolCalling: feature('supported', runtime === 'vllm' ? 'supported' : 'unverified', 'Tool parser depends on runtime flags'),
  streaming: feature('not-applicable', 'supported', 'SSE streaming'),
  vision: feature(runtime === 'llama.cpp' ? 'supported' : 'unsupported', runtime === 'llama.cpp' ? 'partial' : 'unsupported', 'Multimodal projector'),
  thinking: feature('supported', 'partial', 'Reasoning text is not structured'),
  nativeContextTokens: 32768, extendedContextTokens: 131072, servingContextTokens: 8192, maximumOutputTokens: null,
  notes: 'Verify with a live request before routing production traffic.'
})

const catalog = [
  {
    model: { id: 'qwen3-8b', displayName: 'Qwen3 8B', providerModelName: 'Qwen/Qwen3-8B', family: 'Qwen3', runtime: 'vllm', license: 'Apache-2.0', sourceUrl: 'https://huggingface.co/Qwen/Qwen3-8B', parameterBillions: 8, contextTokens: 32768, precision: 'BF16', minimumGpuMemoryGiB: 20, recommendedGpuMemoryGiB: 24, minimumSystemMemoryGiB: 32, recommendedSystemMemoryGiB: 64, diskGiB: 17, minimumGpuCount: 1, recommendedGpuCount: 1, supportsStreaming: true, supportsTools: true, tags: ['chat', 'tools'], surface: 'OpenAi', notes: 'Balanced coding and chat model.' },
    compatibility: { status: 'fits', summary: 'Fits in shared CPU/GPU memory with headroom.', reasons: [], suggestedTensorParallelSize: 1 },
    capabilities: chatCapabilities('Qwen/Qwen3-8B', 'vllm')
  },
  {
    model: { id: 'lfm25-24b-a2b-gguf-q4', displayName: 'Liquid AI LFM2.5 24B A2B — GGUF Q4_K_M', providerModelName: 'LiquidAI/LFM2.5-24B-A2B-GGUF:Q4_K_M', family: 'LFM2.5', runtime: 'llama.cpp', license: 'LFM Open License 1.0', sourceUrl: 'https://huggingface.co/LiquidAI/LFM2.5-24B-A2B-GGUF', parameterBillions: 24, contextTokens: 131072, precision: 'Q4_K_M', minimumGpuMemoryGiB: 16, recommendedGpuMemoryGiB: 24, minimumSystemMemoryGiB: 32, recommendedSystemMemoryGiB: 64, diskGiB: 15, minimumGpuCount: 0, recommendedGpuCount: 1, supportsStreaming: true, supportsTools: true, tags: ['gguf'], surface: 'OpenAi', notes: 'Official 14.5 GB GGUF.' },
    compatibility: { status: 'tight', summary: 'Fits, but long contexts reduce concurrency.', reasons: ['KV cache grows with context.'], suggestedTensorParallelSize: 1 },
    capabilities: chatCapabilities('LiquidAI/LFM2.5-24B-A2B-GGUF:Q4_K_M', 'llama.cpp')
  },
  {
    model: { id: 'qwen3-235b', displayName: 'Qwen3 235B A22B', providerModelName: 'Qwen/Qwen3-235B-A22B', family: 'Qwen3', runtime: 'vllm', license: 'Apache-2.0', sourceUrl: 'https://huggingface.co/Qwen/Qwen3-235B-A22B', parameterBillions: 235, contextTokens: 32768, precision: 'BF16', minimumGpuMemoryGiB: 480, recommendedGpuMemoryGiB: 640, minimumSystemMemoryGiB: 512, recommendedSystemMemoryGiB: 768, diskGiB: 470, minimumGpuCount: 8, recommendedGpuCount: 8, supportsStreaming: true, supportsTools: true, tags: ['moe'], surface: 'OpenAi', notes: 'Requires a multi-GPU server.' },
    compatibility: { status: 'insufficient', summary: 'Needs 480 GiB of accelerator memory.', reasons: ['Only 121 GiB of unified memory is installed.'], suggestedTensorParallelSize: 8 },
    capabilities: chatCapabilities('Qwen/Qwen3-235B-A22B', 'vllm')
  }
]

const installations = [
  {
    id: 'deployment-1', modelId: 'model-1', logicalModel: 'agic-code-fast', providerModelName: 'Qwen/Qwen3-8B',
    catalogModelId: 'qwen3-8b', managedInstallationId: 'qwen3-8b-1a2b3c4d', runtime: 'vllm', enabled: true,
    agentStatus: 'running', runtimeBaseAddress: 'http://agent-node-1.llmproxy.invalid/runtime/18000',
    runtimeMetrics: { deploymentId: 'deployment-1', nodeId: 'node-1', available: true, runtime: 'vllm', modelName: 'Qwen/Qwen3-8B', runningRequests: 2, waitingRequests: 1, cacheUsageRatio: 0.42, collectedAtUtc: FIXED_NOW },
    agentState: { installationId: 'qwen3-8b-1a2b3c4d', status: 'running', runtime: 'vllm', port: 18000, maxNumSeqs: 4, maxModelLen: 16384, sourceRevision: 'b968826d9c46dd6066d109eabc6255188de91218', gpuMemoryUtilization: 0.7 },
    capabilities: chatCapabilities('Qwen/Qwen3-8B', 'vllm')
  },
  {
    id: 'deployment-2', modelId: 'model-2', logicalModel: 'gemma-vision-gguf', providerModelName: 'unsloth/gemma-3-4b-it-GGUF:Q4_K_M',
    catalogModelId: 'custom', managedInstallationId: 'custom-01ea8a84', runtime: 'llama.cpp', enabled: false,
    agentStatus: 'error', runtimeBaseAddress: null, runtimeMetrics: null,
    agentState: { installationId: 'custom-01ea8a84', status: 'error', runtime: 'llama.cpp', port: 18001, maxNumSeqs: 2, maxModelLen: 8192, sourceRevision: null, error: ggufFailure },
    capabilities: chatCapabilities('unsloth/gemma-3-4b-it-GGUF', 'llama.cpp')
  },
  {
    id: 'deployment-4', modelId: 'model-4', logicalModel: 'lfm25-24b', providerModelName: 'LiquidAI/LFM2.5-24B-A2B-GGUF:Q4_K_M',
    catalogModelId: 'lfm25-24b-a2b-gguf-q4', managedInstallationId: 'lfm25-24b-a2b-gguf-q4-9f8e7d6c', runtime: 'llama.cpp', enabled: false,
    agentStatus: 'stopped', runtimeBaseAddress: null, runtimeMetrics: null,
    agentState: { installationId: 'lfm25-24b-a2b-gguf-q4-9f8e7d6c', status: 'stopped', runtime: 'llama.cpp', port: 18002, maxNumSeqs: 4, maxModelLen: 32768, sourceRevision: '5e1a63a3a5c4d34f27e0fbbd0f3cbd0e0a3f9c1d', modelFile: 'LFM2.5-24B-A2B-Q4_K_M.gguf' },
    capabilities: chatCapabilities('LiquidAI/LFM2.5-24B-A2B-GGUF', 'llama.cpp')
  }
]

const hardwareInventory = {
  hostname: 'spark-1d52', operatingSystem: 'Ubuntu 24.04.1 LTS', architecture: 'arm64', cpuLogicalCores: 20,
  cpuUtilizationPercent: 18, systemMemoryTotalGiB: 121.6, systemMemoryAvailableGiB: 72.4,
  diskTotalGiB: 3725, diskAvailableGiB: 2890, runtime: 'docker', runtimeVersion: '28.3.2',
  gpus: [{ name: 'NVIDIA GB10', memoryTotalGiB: 0, memoryFreeGiB: 0, driverVersion: '580.95.05', computeCapability: '12.1', memoryType: 'unified', utilizationPercent: 37, temperatureCelsius: 54, powerWatts: 41.5 }],
  readiness: { dockerInstalled: true, dockerDaemonReady: true, nvidiaDriverDetected: true, nvidiaToolkitReady: true, issues: [] }
}

const installJobs = [
  {
    id: 'job-download', request: { catalogModelId: 'lfm25-24b-a2b-gguf-q4', providerModelName: 'LiquidAI/LFM2.5-24B-A2B-GGUF:Q4_K_M', runtime: 'llama.cpp', startAfterInstall: true },
    status: 'running', stage: 'model-weights', percent: 63.4, detail: 'Downloading LFM2.5-24B-A2B-Q4_K_M.gguf (1/1)',
    completedBytes: 9_870_000_000, totalBytes: 15_570_000_000, createdAtUtc: '2026-11-12T09:41:00Z'
  },
  {
    id: 'job-failed', request: { catalogModelId: 'custom', providerModelName: 'bartowski/Some-Model-GGUF:Q4_K_M', runtime: 'llama.cpp', startAfterInstall: false },
    status: 'failed', stage: 'failed', percent: null, detail: 'Installation failed', createdAtUtc: '2026-11-12T09:12:00Z', completedAtUtc: '2026-11-12T09:12:04Z',
    error: "No GGUF file in 'bartowski/Some-Model-GGUF' matches quantization 'Q4_K_M'. Available quantizations: IQ4_XS, Q5_K_M, Q8_0. Choose one of them (repository:QUANT) and retry."
  },
  {
    id: 'job-gated', request: { catalogModelId: 'custom', providerModelName: 'meta/muse-glimmer', runtime: 'llama.cpp', startAfterInstall: false },
    status: 'failed', stage: 'failed', percent: null, detail: 'Installation failed', createdAtUtc: '2026-11-12T09:50:00Z', completedAtUtc: '2026-11-12T09:50:02Z',
    error: "Preparation before Docker start failed: Hugging Face requires authentication for 'meta/muse-glimmer' (HTTP 401): the repository is private or gated. Accept its license on huggingface.co, add a Hugging Face access token for this server in LlmProxy Admin and retry."
  }
]

const startJobs = [{
  id: 'start-failed', installationId: 'custom-01ea8a84', status: 'failed',
  detail: 'Runtime start failed. Last Docker startup output is included when available.',
  error: ggufFailure, createdAtUtc: '2026-11-12T09:30:00Z', completedAtUtc: '2026-11-12T09:30:42Z'
}]

export const museGlimmerInspection = {
  inspection: {
    id: 'meta-models/Muse-Glimmer-30B', found: true, gated: false, private: false, license: 'apache-2.0',
    pipelineTag: 'text-generation', libraryName: 'transformers', architecture: 'GlimmerForCausalLM',
    parameterCount: 30_532_000_000, precision: 'BF16', weightsBytes: 61_064_000_000, contextLength: 131072,
    quantization: null, multimodal: false, hasGguf: false, hasSafetensors: true, ggufVariants: [],
    suggestion: { runtime: 'vllm', reason: 'Safetensors weights; vLLM serves them directly. Smaller GGUF or quantized builds from other publishers are listed below when they exist.', contextTokens: 32768 },
    url: 'https://huggingface.co/meta-models/Muse-Glimmer-30B'
  },
  quantizedBuilds: [
    { id: 'unsloth/Muse-Glimmer-30B-GGUF', format: 'gguf', downloads: 184_200, likes: 212, parameterCount: 30_532_000_000, lastModified: '2026-11-10T09:00:00Z' },
    { id: 'meta-models/Muse-Glimmer-30B-AWQ', format: 'awq', downloads: 96_400, likes: 58, parameterCount: 30_532_000_000, lastModified: '2026-11-09T09:00:00Z' },
    { id: 'meta-models/Muse-Glimmer-30B-FP8', format: 'fp8', downloads: 41_300, likes: 31, parameterCount: null, lastModified: '2026-11-09T09:00:00Z' },
    { id: 'mlx-community/Muse-Glimmer-30B-4bit', format: 'mlx', downloads: 8_900, likes: 12, parameterCount: null, lastModified: '2026-11-11T09:00:00Z' }
  ]
}

const metricsSummary = {
  windowHours: 24, sinceUtc: '2026-11-11T10:00:00Z', requestCount: 18420, successCount: 18312, errorCount: 108,
  successRatePercent: 99.4, p50DurationMilliseconds: 1840, p95DurationMilliseconds: 6200, p50TimeToFirstByteMilliseconds: 210,
  p95TimeToFirstByteMilliseconds: 640, averageUpstreamHeaderMilliseconds: 38, inputTokens: 9_200_000, outputTokens: 2_150_000,
  totalTokens: 11_350_000, tokenObservedRequests: 18000, failoverRequests: 14, streamingRequests: 16900,
  byModel: [
    { logicalModel: 'agic-code-fast', requestCount: 17100, errorCount: 80, averageDurationMilliseconds: 1900, averageTimeToFirstByteMilliseconds: 220, outputTokens: 2_000_000 },
    { logicalModel: 'systemone-laya', requestCount: 1320, errorCount: 28, averageDurationMilliseconds: 140, averageTimeToFirstByteMilliseconds: null, outputTokens: 0 }
  ],
  byNode: [{ nodeId: 'node-1', requestCount: 18420, errorCount: 108, averageDurationMilliseconds: 1840, p95DurationMilliseconds: 6200, outputTokens: 2_150_000 }]
}

const requestMetrics = [
  { id: 1, requestId: 'req-7f3a', startedAtUtc: '2026-11-12T09:58:02Z', logicalModel: 'agic-code-fast', surface: 'chat_completions', deploymentId: 'deployment-1', nodeId: 'node-1', apiCredentialId: 'credential-1', statusCode: 200, durationMilliseconds: 2104, attemptCount: 1, isStreaming: true, upstreamHeaderMilliseconds: 35, timeToFirstByteMilliseconds: 198, inputTokens: 1240, outputTokens: 310, totalTokens: 1550, errorCode: null },
  { id: 2, requestId: 'req-7f3b', startedAtUtc: '2026-11-12T09:57:40Z', logicalModel: 'gemma-vision-gguf', surface: 'chat_completions', deploymentId: null, nodeId: null, apiCredentialId: 'credential-1', statusCode: 503, durationMilliseconds: 4, attemptCount: 0, isStreaming: false, upstreamHeaderMilliseconds: null, timeToFirstByteMilliseconds: null, inputTokens: null, outputTokens: null, totalTokens: null, errorCode: 'no_route' }
]

const credentials = [
  { id: 'credential-1', name: 'GitHub Copilot', keyPrefix: 'lp_org_7d2f', enabled: true, kind: 'organization', usageGroupId: 'group-1', enforceCallerGovernance: false, secretAvailable: true, createdAtUtc: '2026-10-01T08:00:00Z', lastUsedAtUtc: '2026-11-12T09:58:02Z' },
  { id: 'credential-2', name: 'CI pipeline', keyPrefix: 'lp_org_19ac', enabled: false, kind: 'organization', usageGroupId: null, enforceCallerGovernance: true, secretAvailable: false, createdAtUtc: '2026-09-02T08:00:00Z', lastUsedAtUtc: '2026-10-20T17:00:00Z' }
]

const usageGroups = [{ id: 'group-1', name: 'Development CRM', description: 'CRM engineering team', credentialCount: 1, userCount: 2, createdAtUtc: '2026-10-03T06:00:00Z', updatedAtUtc: '2026-10-03T06:00:00Z' }]

const platformUsers = [
  { id: 'user-1', tenantId: 'tenant-1', objectId: 'object-1', principalName: 'alessandro@agic.it', displayName: 'Alessandro Rapiti', usageGroupId: 'group-1', usageGroupName: 'Development CRM', enabled: true, provisioningSource: 'automatic', createdAtUtc: '2026-10-03T06:00:00Z', lastSeenAtUtc: '2026-11-12T09:40:00Z', disabledAtUtc: null, credentialCount: 2, activeCredentialCount: 2, lastCredentialUsedAtUtc: '2026-11-12T09:58:00Z', requestCount30d: 1240, errorCount30d: 3 },
  { id: 'user-2', tenantId: 'tenant-1', objectId: 'object-2', principalName: 'giulia@agic.it', displayName: 'Giulia Bianchi', usageGroupId: null, usageGroupName: null, enabled: false, provisioningSource: 'admin', createdAtUtc: '2026-10-05T06:00:00Z', lastSeenAtUtc: '2026-10-30T12:00:00Z', disabledAtUtc: '2026-11-01T08:00:00Z', credentialCount: 1, activeCredentialCount: 0, lastCredentialUsedAtUtc: '2026-10-30T12:00:00Z', requestCount30d: 12, errorCount30d: 0 }
]

const usageSummary = {
  windowDays: 30, sinceUtc: '2026-10-13T00:00:00Z', windowGranularity: 'utc_day', rawRetentionDays: 90, rollupRetentionDays: 730,
  rawRequestCount: 18420, rolledUpRequestCount: 0, historicalRollupsUsed: false, requestCount: 18420, errorCount: 108,
  inputTokens: 9_200_000, outputTokens: 2_150_000, totalTokens: 11_350_000, rateLimitedRequests: 12, capacityExhaustedRequests: 3,
  groups: [{ usageGroupId: 'group-1', name: 'Development CRM', requestCount: 17100, errorCount: 80, inputTokens: 9_000_000, outputTokens: 2_000_000, totalTokens: 11_000_000, rateLimitedRequests: 12, averageTtftMilliseconds: 220, averageDurationMilliseconds: 1900 }],
  credentials: [{ apiCredentialId: 'credential-1', name: 'GitHub Copilot', keyPrefix: 'lp_org_7d2f', usageGroupId: 'group-1', requestCount: 17100, errorCount: 80, inputTokens: 9_000_000, outputTokens: 2_000_000, totalTokens: 11_000_000, rateLimitedRequests: 12 }],
  models: [{ logicalModel: 'agic-code-fast', requestCount: 17100, errorCount: 80, inputTokens: 9_000_000, outputTokens: 2_000_000, totalTokens: 11_000_000, rateLimitedRequests: 12 }]
}

const contentLogItem = { id: 1, requestId: 'req-7f3a', startedAtUtc: '2026-11-12T09:58:02Z', completedAtUtc: '2026-11-12T09:58:04Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: 'credential-1', statusCode: 200 }

const productInfo = {
  product: 'LlmProxy', version: '0.2.64', channel: 'stable', releasedOn: '2026-11-12', buildRevision: '33d01fa0c1d2e3f4', builtAtUtc: FIXED_NOW,
  releases: [{ version: '0.2.64', releasedOn: '2026-11-12', title: 'Pinned GGUF downloads and clearer model lifecycle', sections: { Fixed: ['llama.cpp models start from a verified local GGUF instead of resolving -hf at start.'], Changed: ['Models and Servers are reorganized into single workspaces.'] } }]
}

export async function installMockAdmin(page: Page) {
  await page.route('**/api/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname
    const method = request.method()
    if (/\/model-management\/nodes\/[^/]+\/huggingface-access$/.test(path) && method === 'GET')
      return json(route, { configured: false, source: 'none', account: null, verified: false, updatedAtUtc: null })
    if (method !== 'GET') return json(route, { ok: true })

    switch (path) {
      case '/api/admin/product': return json(route, productInfo)
      case '/api/admin/session': return json(route, { canWrite: true, roles: ['LlmProxy.Admin'] })
      case '/api/admin/overview': return json(route, { nodes: { total: 2, healthy: 1, degraded: 0, unhealthy: 1, draining: 0 }, models: models.length, deployments: deployments.length, activeRequests: 3, requestsToday: 4120 })
      case '/api/admin/routing': return json(route, { strategy: 'WeightedLeastLoaded', supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
      case '/api/admin/routing/tuning': return json(route, { warmupSamples: 3, ttftTargetMilliseconds: 2000, ttftPenaltyWeight: 0.25, failurePenaltyWeight: 1.5, externalLoadPenaltyWeight: 0.4, queuePenaltyWeight: 0.75, kvCacheThreshold: 0.7, kvCachePenaltyWeight: 0.6, degradedNodePenalty: 0.35, unknownNodePenalty: 0.1, updatedAtUtc: '2026-11-01T08:00:00Z' })
      case '/api/admin/routing/performance': return json(route, [{ deploymentId: 'deployment-1', sampleCount: 1840, ewmaTimeToFirstByteMilliseconds: 214, ewmaDurationMilliseconds: 1880, infrastructureFailureScore: 0.01, lastObservedAtUtc: '2026-11-12T09:58:02Z' }])
      case '/api/admin/routing/runtime': return json(route, [{ nodeId: 'node-1', available: true, modelName: 'Qwen/Qwen3-8B', runningRequests: 2, waitingRequests: 1, kvCacheUsageRatio: 0.42, promptTokensTotal: 9_200_000, generationTokensTotal: 2_150_000, collectedAtUtc: FIXED_NOW, lastAttemptAtUtc: FIXED_NOW, error: null }])
      case '/api/admin/hardware': return json(route, [{ nodeId: 'node-1', available: true, gpuCount: 1, averageGpuUtilizationPercent: 37, maxGpuUtilizationPercent: 61, framebufferUsedMiB: 48_000, framebufferFreeMiB: 76_000, framebufferUsageRatio: 0.39, maxTemperatureCelsius: 54, totalPowerUsageWatts: 41.5, collectedAtUtc: FIXED_NOW, lastAttemptAtUtc: FIXED_NOW, error: null }])
      case '/api/admin/capacity': return json(route, {
        nodes: nodes.map(node => ({ id: node.id, name: node.name, maxConcurrency: node.maxConcurrency, activeRequests: node.id === 'node-1' ? 3 : 0, remaining: node.maxConcurrency - (node.id === 'node-1' ? 3 : 0), capacityProvider: 'redis', coordinationAvailable: true, localActiveRequests: 1 })),
        deployments: deployments.map(item => ({ ...item, effectiveMaxConcurrency: item.maxConcurrency ?? 8, activeRequests: item.id === 'deployment-1' ? 3 : 0 }))
      })
      case '/api/admin/nodes': return json(route, nodes)
      case '/api/admin/models': return json(route, models)
      case '/api/admin/deployments': return json(route, deployments)
      case '/api/admin/api-credentials': return json(route, credentials)
      case '/api/admin/governance/credentials': return json(route, credentials)
      case '/api/admin/metrics': return json(route, requestMetrics)
      case '/api/admin/metrics/query': return json(route, { items: requestMetrics, total: requestMetrics.length, page: 1, pageSize: 20 })
      case '/api/admin/metrics/summary': return json(route, metricsSummary)
      case '/api/admin/audit': return json(route, [
        { id: 2, occurredAtUtc: '2026-11-12T09:30:00Z', actor: 'alessandro@agic.it', action: 'model.start.queued', entityType: 'deployment', entityId: 'deployment-2', sourceIp: '10.0.0.5', detailsJson: '{"installationId":"custom-01ea8a84"}' },
        { id: 1, occurredAtUtc: '2026-11-12T09:05:00Z', actor: 'alessandro@agic.it', action: 'routing.update', entityType: 'routing_policy', entityId: '1', sourceIp: '10.0.0.5', detailsJson: '{"strategy":"WeightedLeastLoaded"}' }
      ])
      case '/api/admin/usage-groups': return json(route, usageGroups)
      case '/api/admin/rate-limits': return json(route, [{ id: 'rate-1', apiCredentialId: 'credential-1', credentialName: 'GitHub Copilot', keyPrefix: 'lp_org_7d2f', logicalModel: null, requestsPerWindow: 120, windowSeconds: 60, outputTokensPerWindow: 400000, maxOutputTokensPerRequest: 8192, enabled: true, createdAtUtc: '2026-10-01T08:00:00Z', updatedAtUtc: '2026-10-01T08:00:00Z' }])
      case '/api/admin/user-rate-limits': return json(route, [])
      case '/api/admin/group-rate-limits': return json(route, [])
      case '/api/admin/usage/summary': return json(route, usageSummary)
      case '/api/admin/usage/users': return json(route, [])
      case '/api/admin/users': return json(route, platformUsers)
      case '/api/admin/users/settings': return json(route, { provisioningMode: 'automatic', updatedAtUtc: '2026-10-03T06:00:00Z', configuredTenantId: 'tenant-1' })
      case '/api/admin/identity/users': return json(route, [])
      case '/api/admin/content-logs/settings': return json(route, { retentionDays: 30, updatedAtUtc: '2026-10-01T08:00:00Z', minimumRetentionDays: 10, maximumRetentionDays: 4015, cleanupIntervalHours: 4 })
      case '/api/admin/content-logs/summary-settings': return json(route, { systemPrompt: 'Summarize the request for an auditor.', defaultLogicalModel: 'agic-code-fast', defaultNodeId: null, defaultSystemPrompt: 'Summarize the request for an auditor.', updatedAtUtc: '2026-10-01T08:00:00Z' })
      case '/api/admin/content-logs/query': return json(route, { items: [contentLogItem], total: 1, page: 1, pageSize: 50 })
      case '/api/admin/content-logs': return json(route, [contentLogItem])
      case '/api/admin/testing/systemone': return json(route, { enabled: true, baseAddress: 'http://10.0.0.2:8002', upstreamEndpoint: 'http://10.0.0.2:8002/v1/systemone', publicEndpoint: '/v1/systemone', apiKeyConfigured: true, timeoutSeconds: 30, configurationError: null, models: ['systemone-laya'], defaultModel: 'systemone-laya' })
      case '/api/admin/testing/chat/diagnosis': return json(route, { model: url.searchParams.get('model') ?? 'agic-code-fast', code: 'ready', message: 'Ready: 1 enabled healthy deployment.', nextAction: 'routing', logicalModelEnabled: true, deployments: [] })
      case '/api/admin/systemone/default': return json(route, { configuredModel: null, effectiveModel: 'systemone-laya', options: ['systemone-laya'] })
      case '/api/admin/node-enrollment/nodes': return json(route, [{ nodeId: 'node-1', mode: 'outbound', lastHeartbeatAtUtc: '2026-11-12T09:59:55Z', agentVersion: '0.2.64', desiredAgentVersion: null, agentUpdateStatus: null, tunnelConnected: true, hasRecoverySecret: true, recoverySecretCreatedAtUtc: '2026-10-11T08:00:00Z' }])
      case '/api/admin/node-enrollment/downloads': return json(route, { version: '0.2.64', bootstrap: '/agent/install.sh', bootstrapChecksum: 'a'.repeat(64), x64: '/agent/x64.tar.gz', x64Checksum: 'b'.repeat(64), arm64: '/agent/arm64.tar.gz', arm64Checksum: 'c'.repeat(64) })
      case '/api/admin/updates': return json(route, { currentVersion: '0.2.64', agentAvailable: true, policy: { mode: 'manual', timeZoneId: 'Europe/Rome', localHour: 2, localMinute: 0, dayOfWeek: 0, dayOfMonth: 1, lastCheckedAtUtc: null, lastScheduledAtUtc: null, lastScheduledVersion: null, lastError: null, updatedAtUtc: '2026-11-01T08:00:00Z' }, agent: { installedVersion: '0.2.64', activeJob: null, recentJobs: [] }, releases: [] })
      case '/api/admin/model-management/catalog': return json(route, catalog.map(item => item.model))
      case '/api/me': return json(route, { tenantId: 'tenant-1', objectId: 'object-1', principalName: 'alessandro@agic.it', displayName: 'Alessandro Rapiti', roles: ['LlmProxy.User'] })
      case '/api/me/api-credentials': return json(route, [{ id: 'my-1', name: 'VS Code Copilot', keyPrefix: 'lp_usr_3e1f', enabled: true, createdAtUtc: '2026-10-05T08:00:00Z', lastUsedAtUtc: '2026-11-12T09:58:02Z' }])
      case '/api/me/rate-limits': return json(route, [{ id: 'user-rate-1', scope: 'user', scopeName: 'alessandro@agic.it', logicalModel: null, requestsPerWindow: 300, windowSeconds: 60, outputTokensPerWindow: 100000, maxOutputTokensPerRequest: 4096, enabled: true, updatedAtUtc: '2026-10-21T09:00:00Z' }])
      case '/api/me/requests': return json(route, requestMetrics.slice(0, 1))
      case '/api/me/content-logs': return json(route, { items: [contentLogItem], total: 1, page: 1, pageSize: 20 })
      case '/api/me/usage': return json(route, { windowDays: 30, sinceUtc: '2026-10-13T00:00:00Z', windowGranularity: 'utc_day', historicalRollupsUsed: false, requestCount: 1240, errorCount: 3, inputTokens: 620000, outputTokens: 150000, totalTokens: 770000, rateLimitedRequests: 0, credentials: [{ apiCredentialId: 'my-1', name: 'VS Code Copilot', keyPrefix: 'lp_usr_3e1f', requestCount: 1240, errorCount: 3, inputTokens: 620000, outputTokens: 150000, totalTokens: 770000, rateLimitedRequests: 0 }] })
    }

    const nodeRoute = path.match(/^\/api\/admin\/model-management\/nodes\/([^/]+)\/(overview|install-jobs|start-jobs|upstream-updates|resources)$/)
    if (nodeRoute) {
      const node = nodes.find(item => item.id === nodeRoute[1])
      if (nodeRoute[2] === 'overview') return json(route, node?.id === 'node-1'
        ? { node: { id: node.id, name: node.name, managementBaseAddress: node.managementBaseAddress, hasManagementCredential: true }, agentAvailable: true, inventoryStale: false, agentError: null, hardware: hardwareInventory, catalog, installations }
        : { node: { id: nodeRoute[1], name: node?.name ?? 'unknown', managementBaseAddress: null, hasManagementCredential: false }, agentAvailable: false, inventoryStale: true, agentError: 'No management Agent is paired with this server.', hardware: null, catalog: [], installations: [] })
      if (nodeRoute[2] === 'install-jobs') return json(route, node?.id === 'node-1' ? installJobs : [])
      if (nodeRoute[2] === 'start-jobs') return json(route, node?.id === 'node-1' ? startJobs : [])
      if (nodeRoute[2] === 'upstream-updates') return json(route, [])
      return json(route, { containers: [], gpuProcesses: [], recentVllmMemoryFailures: [], warnings: [], thermal: null })
    }
    if (/^\/api\/admin\/nodes\/[^/]+\/maintenance$/.test(path)) {
      const id = path.split('/')[4]
      const node = nodes.find(item => item.id === id)!
      return json(route, { nodeId: id, nodeName: node.name, nodeStatus: node.status, enabled: true, provider: 'redis', coordinationAvailable: true, admissionBlocked: false, activeRequests: 0, drained: false })
    }
    if (path.startsWith('/api/admin/benchmarks/deployments/')) return json(route, [])
    if (path.startsWith('/api/admin/model-management/capabilities')) return json(route, chatCapabilities('Qwen/Qwen3-8B', 'vllm'))
    if (path === '/api/admin/model-management/hugging-face/inspect') return json(route, museGlimmerInspection)
    if (path === '/api/admin/model-management/hugging-face/gguf-variants') return json(route, {
      repository: url.searchParams.get('repository'),
      variants: [{ quantization: 'Q4_K_M', fileName: 'Muse-Glimmer-30B-Q4_K_M.gguf', parts: 1, sizeBytes: 18_600_000_000 }, { quantization: 'Q8_0', fileName: 'Muse-Glimmer-30B-Q8_0.gguf', parts: 2, sizeBytes: 32_500_000_000 }]
    })
    if (path.startsWith('/api/admin/model-management/hugging-face')) return json(route, { source: 'Hugging Face Hub public models', discoveredAtUtc: FIXED_NOW, verifiedForLocalRuntime: false, models: [] })
    return json(route, { error: `Unhandled visual fixture route ${method} ${path}` }, 500)
  })
}
