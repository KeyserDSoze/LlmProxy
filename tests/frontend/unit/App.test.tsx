import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({
  adminSession: vi.fn(),
  overview: vi.fn(),
  routing: vi.fn(),
  routingTuning: vi.fn(),
  routingPerformance: vi.fn(),
  routingRuntime: vi.fn(),
  hardware: vi.fn(),
  updateRouting: vi.fn(),
  updateRoutingTuning: vi.fn(),
  nodes: vi.fn(),
  models: vi.fn(),
  deployments: vi.fn(),
  apiCredentials: vi.fn(),
  metrics: vi.fn(),
  metricsQuery: vi.fn(),
  metricsSummary: vi.fn(),
  audit: vi.fn(),
  createNode: vi.fn(),
  updateNode: vi.fn(),
  setNodeUpstreamCredential: vi.fn(),
  clearNodeUpstreamCredential: vi.fn(),
  updateNodeHardwareMetrics: vi.fn(),
  testNodeConnection: vi.fn(),
  drainNode: vi.fn(),
  enableNode: vi.fn(),
  disableNode: vi.fn(),
  deleteNode: vi.fn(),
  createModel: vi.fn(),
  createDeployment: vi.fn(),
  updateDeployment: vi.fn(),
  createApiCredential: vi.fn(),
  rotateApiCredential: vi.fn(),
  revealApiCredential: vi.fn(),
  revokeApiCredential: vi.fn(),
  systemOneStatus: vi.fn(),
  testSystemOne: vi.fn(),
  testChat: vi.fn(),
  contentLogs: vi.fn(),
  contentLog: vi.fn(),
  contentLogSettings: vi.fn(),
  updateContentLogSettings: vi.fn(),
  runContentLogRetention: vi.fn(),
  platformUsers: vi.fn(),
  platformUserAccessSettings: vi.fn(),
  updatePlatformUserAccessSettings: vi.fn(),
  createPlatformUser: vi.fn(),
  disablePlatformUser: vi.fn(),
  enablePlatformUser: vi.fn(),
  usageGroups: vi.fn(),
  assignPlatformUserUsageGroup: vi.fn()
}))

vi.mock('../../../src/LlmProxy.Admin/src/api', () => ({ api: mockedApi }))

import App from '../../../src/LlmProxy.Admin/src/App'

const tuning = {
  warmupSamples: 3,
  ttftTargetMilliseconds: 2000,
  ttftPenaltyWeight: 0.25,
  failurePenaltyWeight: 1.5,
  externalLoadPenaltyWeight: 0.4,
  queuePenaltyWeight: 0.75,
  kvCacheThreshold: 0.7,
  kvCachePenaltyWeight: 0.6,
  degradedNodePenalty: 0.35,
  unknownNodePenalty: 0.1,
  updatedAtUtc: '2026-09-09T10:04:00Z'
}

describe('admin application', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedApi.adminSession.mockResolvedValue({ canWrite: true, roles: ['LlmProxy.Admin'] })
    mockedApi.overview.mockResolvedValue({
      nodes: { total: 1, healthy: 1, degraded: 0, unhealthy: 0, draining: 0 },
      models: 1,
      deployments: 1,
      activeRequests: 2,
      requestsToday: 42
    })
    mockedApi.routing.mockResolvedValue({ strategy: 'WeightedLeastLoaded', supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    mockedApi.routingTuning.mockResolvedValue(tuning)
    mockedApi.updateRoutingTuning.mockResolvedValue(tuning)
    mockedApi.routingPerformance.mockResolvedValue([{
      deploymentId: 'deployment-1', sampleCount: 14, ewmaTimeToFirstByteMilliseconds: 145, ewmaDurationMilliseconds: 980,
      infrastructureFailureScore: 0.05, lastObservedAtUtc: '2026-09-09T10:03:00Z'
    }])
    mockedApi.routingRuntime.mockResolvedValue([{
      nodeId: 'node-1', available: true, modelName: 'Qwen/Test', runningRequests: 2, waitingRequests: 1, kvCacheUsageRatio: 0.72,
      promptTokensTotal: 1200, generationTokensTotal: 650, collectedAtUtc: '2026-09-09T10:03:00Z', lastAttemptAtUtc: '2026-09-09T10:03:00Z', error: null
    }])
    mockedApi.hardware.mockResolvedValue([{
      nodeId: 'node-1', available: true, gpuCount: 2, averageGpuUtilizationPercent: 60, maxGpuUtilizationPercent: 80,
      framebufferUsedMiB: 4096, framebufferFreeMiB: 12288, framebufferUsageRatio: 0.25,
      maxTemperatureCelsius: 67, totalPowerUsageWatts: 261, collectedAtUtc: '2026-09-09T10:03:00Z', lastAttemptAtUtc: '2026-09-09T10:03:00Z', error: null
    }])
    mockedApi.updateRouting.mockResolvedValue({ strategy: 'RoundRobin', supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    mockedApi.nodes.mockResolvedValue([{
      id: 'node-1', name: 'inference-01', baseAddress: 'http://10.0.0.21:8000/vllm', hardwareMetricsBaseAddress: 'http://10.0.0.21:9400/dcgm',
      hasUpstreamCredential: true, weight: 1, maxConcurrency: 4, enabled: true, status: 'Healthy',
      lastHealthCheckUtc: '2026-09-09T10:00:00Z', lastHealthyAtUtc: '2026-09-09T10:00:00Z', lastHealthLatencyMilliseconds: 12,
      lastHealthError: null, consecutiveHealthSuccesses: 4, consecutiveHealthFailures: 0
    }])
    mockedApi.setNodeUpstreamCredential.mockResolvedValue({ id: 'node-1', hasUpstreamCredential: true })
    mockedApi.clearNodeUpstreamCredential.mockResolvedValue(undefined)
    mockedApi.updateNodeHardwareMetrics.mockResolvedValue({ id: 'node-1', hardwareMetricsBaseAddress: 'http://10.0.0.21:9400/dcgm' })
    mockedApi.models.mockResolvedValue([{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'Qwen/Test', supportsStreaming: true, supportsTools: true, enabled: true }])
    mockedApi.deployments.mockResolvedValue([{ id: 'deployment-1', nodeId: 'node-1', modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4 }])
    mockedApi.apiCredentials.mockResolvedValue([])
    mockedApi.systemOneStatus.mockResolvedValue({ enabled: true, baseAddress: 'http://classifier:8001', upstreamEndpoint: 'http://classifier:8001/v1/systemone', publicEndpoint: '/v1/systemone', apiKeyConfigured: true, timeoutSeconds: 30, configurationError: null })
    mockedApi.testChat.mockResolvedValue({ requestId: 'test-chat', success: true, statusCode: 200, latencyMilliseconds: 20, nodeName: 'inference-01', requestBody: '{}', responseBody: '{"ok":true}' })
    mockedApi.testSystemOne.mockResolvedValue({ requestId: 'test-classifier', success: true, statusCode: 200, latencyMilliseconds: 10, requestBody: '{}', responseBody: '{"billing":true}' })
    mockedApi.contentLogs.mockResolvedValue([{ id: 1, requestId: 'req-log-1', startedAtUtc: '2026-09-09T10:02:00Z', completedAtUtc: '2026-09-09T10:02:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200 }])
    mockedApi.contentLogSettings.mockResolvedValue({ retentionDays: 30, updatedAtUtc: '2026-09-09T10:00:00Z', minimumRetentionDays: 10, maximumRetentionDays: 180, cleanupIntervalHours: 4 })
    mockedApi.platformUserAccessSettings.mockResolvedValue({ provisioningMode: 'manual', updatedAtUtc: '2026-10-03T06:00:00Z', configuredTenantId: 'tenant-1' })
    mockedApi.platformUsers.mockResolvedValue([{ id: 'user-1', tenantId: 'tenant-1', objectId: 'object-1', principalName: 'user@example.com', displayName: 'Example User', enabled: true, provisioningSource: 'admin', createdAtUtc: '2026-10-03T06:00:00Z', lastSeenAtUtc: null, disabledAtUtc: null, credentialCount: 1, activeCredentialCount: 1, lastCredentialUsedAtUtc: null, requestCount30d: 12, errorCount30d: 1 }])
    mockedApi.updatePlatformUserAccessSettings.mockResolvedValue({ provisioningMode: 'automatic', updatedAtUtc: '2026-10-03T06:10:00Z', configuredTenantId: 'tenant-1' })
    mockedApi.usageGroups.mockResolvedValue([{ id: 'group-1', name: 'Development CRM', description: 'CRM team', createdAtUtc: '2026-10-03T06:00:00Z', updatedAtUtc: '2026-10-03T06:00:00Z', credentialCount: 1, userCount: 1 }])
    mockedApi.assignPlatformUserUsageGroup.mockResolvedValue(undefined)
    mockedApi.metrics.mockResolvedValue([{
      id: 1, requestId: 'req-1', startedAtUtc: '2026-09-09T10:02:00Z', logicalModel: 'agic-code-fast', surface: 'chat_completions',
      deploymentId: 'deployment-1', nodeId: 'node-1', apiCredentialId: null, statusCode: 200, durationMilliseconds: 1040,
      attemptCount: 2, isStreaming: true, upstreamHeaderMilliseconds: 38, timeToFirstByteMilliseconds: 120,
      inputTokens: 17, outputTokens: 6, totalTokens: 23, errorCode: null
    }])
    mockedApi.metricsQuery.mockResolvedValue({
      items: [{
        id: 1, requestId: 'req-1', startedAtUtc: '2026-09-09T10:02:00Z', logicalModel: 'agic-code-fast', surface: 'chat_completions',
        deploymentId: 'deployment-1', nodeId: 'node-1', apiCredentialId: null, statusCode: 200, durationMilliseconds: 1040,
        attemptCount: 2, isStreaming: true, upstreamHeaderMilliseconds: 38, timeToFirstByteMilliseconds: 120,
        inputTokens: 17, outputTokens: 6, totalTokens: 23, errorCode: null
      }],
      total: 1, page: 1, pageSize: 20
    })
    mockedApi.metricsSummary.mockResolvedValue({
      windowHours: 24, sinceUtc: '2026-09-08T10:00:00Z', requestCount: 125, successCount: 124, errorCount: 1, successRatePercent: 99.2,
      p50DurationMilliseconds: 900, p95DurationMilliseconds: 1800, p50TimeToFirstByteMilliseconds: 120, p95TimeToFirstByteMilliseconds: 350,
      averageUpstreamHeaderMilliseconds: 40, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, tokenObservedRequests: 100,
      failoverRequests: 2, streamingRequests: 90,
      byModel: [{ logicalModel: 'agic-code-fast', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, averageTimeToFirstByteMilliseconds: 120, outputTokens: 500 }],
      byNode: [{ nodeId: 'node-1', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, p95DurationMilliseconds: 1800, outputTokens: 500 }]
    })
    mockedApi.audit.mockResolvedValue([{ id: 1, occurredAtUtc: '2026-09-09T10:01:00Z', actor: 'admin@agic.it', action: 'routing.update', entityType: 'routing_policy', entityId: '1', sourceIp: '10.0.0.5', detailsJson: '{}' }])
    mockedApi.testNodeConnection.mockResolvedValue({
      nodeId: 'node-1', nodeName: 'inference-01', serviceRoot: 'http://10.0.0.21:8000/vllm', healthUrl: 'http://10.0.0.21:8000/vllm/health',
      modelsUrl: 'http://10.0.0.21:8000/vllm/v1/models', chatCompletionsUrl: 'http://10.0.0.21:8000/vllm/v1/chat/completions', responsesUrl: 'http://10.0.0.21:8000/vllm/v1/responses',
      success: true, health: { url: 'health', success: true, statusCode: 200, latencyMilliseconds: 12 }, openAi: { url: 'models', success: true, statusCode: 200, latencyMilliseconds: 15 }
    })
  })

  it('renders fleet and inference observability on the dashboard', async () => {
    render(<App />)
    expect(await screen.findByText('inference-01')).toBeInTheDocument()
    expect(screen.getByText('1 H / 0 D')).toBeInTheDocument()
    expect(screen.getByText('12 ms')).toBeInTheDocument()
    expect(screen.getByText('4 ok')).toBeInTheDocument()
    expect(screen.getByText('42')).toBeInTheDocument()
    expect(screen.getByText('Routing: Weighted least loaded')).toBeInTheDocument()
    expect(screen.getByText('99.2%')).toBeInTheDocument()
    expect(screen.getByText('350 ms')).toBeInTheDocument()
  })

  it('navigates to the inference node management view and tests the complete service root', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01')
    await user.click(screen.getByRole('button', { name: 'Inference Nodes' })); await user.click(screen.getByRole('button', { name: 'Test' }))
    expect(await screen.findByText('✓ Connection test: inference-01')).toBeInTheDocument()
    expect(screen.getByText(/vllm\/v1\/chat\/completions/)).toBeInTheDocument()
    expect(screen.getByText(/upstream auth configured/)).toBeInTheDocument()
  })

  it('shows hardware telemetry and can update the separate DCGM root', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01')
    await user.click(screen.getByRole('button', { name: 'Hardware' }))
    expect(screen.getByRole('heading', { name: 'Hardware telemetry', exact: true })).toBeInTheDocument()
    expect(screen.getByText('60.0% avg · 80.0% max')).toBeInTheDocument()
    expect(screen.getByText('4.0 GiB used · 25.0%')).toBeInTheDocument()
    expect(screen.getAllByText('67 °C')).toHaveLength(2)
    expect(screen.getByText('261 W')).toBeInTheDocument()
    expect(screen.getByText('Routing isolation')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Configure telemetry endpoint' }))
    const input = screen.getByLabelText('Hardware metrics service root')
    await user.clear(input)
    await user.type(input, 'http://10.0.0.21:9400/new-dcgm')
    await user.click(screen.getByRole('button', { name: 'Save endpoint' }))
    expect(mockedApi.updateNodeHardwareMetrics).toHaveBeenCalledWith('node-1', 'http://10.0.0.21:9400/new-dcgm')
  })

  it('exposes live routing strategy, tuning and capacity signals', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01'); await user.click(screen.getByRole('button', { name: 'Routing' }))
    expect(screen.getByRole('heading', { name: 'Smart-routing tuning' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Performance feedback' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Live vLLM capacity' })).toBeInTheDocument()
    expect(screen.getByText('72.0%')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('TTFT target')); await user.type(screen.getByLabelText('TTFT target'), '1500')
    await user.click(screen.getByRole('button', { name: 'Apply smart-routing tuning' }))
    expect(mockedApi.updateRoutingTuning).toHaveBeenCalledWith(expect.objectContaining({ ttftTargetMilliseconds: 1500, kvCacheThreshold: 0.7 }))

    await user.selectOptions(screen.getByLabelText('Routing strategy'), 'RoundRobin')
    await user.click(screen.getByRole('button', { name: 'Apply routing strategy' }))
    expect(mockedApi.updateRouting).toHaveBeenCalledWith('RoundRobin')
  })

  it('shows inference observability by model, node and request', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01'); await user.click(screen.getByRole('button', { name: 'Request Metrics' }))
    expect(screen.getByRole('heading', { name: 'Inference observability', exact: true })).toBeInTheDocument()
    expect(await screen.findByText('Chat Completions · SSE')).toBeInTheDocument()
    expect(screen.getByText('2 · failover')).toBeInTheDocument()
    expect(mockedApi.metricsQuery).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 20, status: 'all' }))
  })


  it('shows contextual documentation, playground and administrator-only live content logs', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01')
    expect(screen.getByText(/Page documentation · Dashboard/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Playground' }))
    expect(screen.getByRole('heading', { name: 'Model chat test' })).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: 'System One classifier' }))
    expect(screen.getByRole('heading', { name: 'System One classifier' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Content Logs' }))
    expect(await screen.findByRole('heading', { name: 'Live request / response log' })).toBeInTheDocument()
    expect(screen.getByText('Chat Completions')).toBeInTheDocument()
    expect(screen.getByText(/10–180 days/)).toBeInTheDocument()
  })

  it('manages end-user provisioning and access', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01')
    await user.click(screen.getByRole('button', { name: 'Users & Access' }))
    expect(await screen.findByText('Example User')).toBeInTheDocument()
    expect(screen.getByText('12', { exact: true })).toBeInTheDocument()
    await user.click(screen.getByRole('tab', { name: 'Provisioning & identity' }))
    expect(screen.getByRole('heading', { name: 'User provisioning policy' })).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Provisioning mode'), 'automatic')
    await user.click(screen.getByRole('button', { name: 'Save provisioning mode' }))
    expect(mockedApi.updatePlatformUserAccessSettings).toHaveBeenCalledWith('automatic')
  })

  it('shows the administrative audit trail', async () => {
    const user = userEvent.setup(); render(<App />); await screen.findByText('inference-01'); await user.click(screen.getByRole('button', { name: 'Audit Trail' }))
    expect(screen.getByText('admin@agic.it')).toBeInTheDocument()
    expect(screen.getByText('routing.update')).toBeInTheDocument()
  })
})