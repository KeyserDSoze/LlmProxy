import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({
  overview: vi.fn(),
  routing: vi.fn(),
  updateRouting: vi.fn(),
  nodes: vi.fn(),
  models: vi.fn(),
  deployments: vi.fn(),
  apiCredentials: vi.fn(),
  metrics: vi.fn(),
  metricsSummary: vi.fn(),
  audit: vi.fn(),
  createNode: vi.fn(),
  updateNode: vi.fn(),
  testNodeConnection: vi.fn(),
  drainNode: vi.fn(),
  enableNode: vi.fn(),
  disableNode: vi.fn(),
  createModel: vi.fn(),
  createDeployment: vi.fn(),
  updateDeployment: vi.fn(),
  createApiCredential: vi.fn(),
  revokeApiCredential: vi.fn()
}))

vi.mock('../../../src/LlmProxy.Admin/src/api', () => ({ api: mockedApi }))

import App from '../../../src/LlmProxy.Admin/src/App'

describe('admin application', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedApi.overview.mockResolvedValue({
      nodes: { total: 1, healthy: 1, degraded: 0, unhealthy: 0, draining: 0 },
      models: 1,
      deployments: 1,
      activeRequests: 2,
      requestsToday: 42
    })
    mockedApi.routing.mockResolvedValue({
      strategy: 'WeightedLeastLoaded',
      supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin']
    })
    mockedApi.updateRouting.mockResolvedValue({
      strategy: 'RoundRobin',
      supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin']
    })
    mockedApi.nodes.mockResolvedValue([{
      id: 'node-1',
      name: 'dgx-01',
      baseAddress: 'http://10.0.0.21:8000/vllm',
      weight: 1,
      maxConcurrency: 4,
      enabled: true,
      status: 'Healthy',
      lastHealthCheckUtc: '2026-09-09T10:00:00Z',
      lastHealthyAtUtc: '2026-09-09T10:00:00Z',
      lastHealthLatencyMilliseconds: 12,
      lastHealthError: null,
      consecutiveHealthSuccesses: 4,
      consecutiveHealthFailures: 0
    }])
    mockedApi.models.mockResolvedValue([])
    mockedApi.deployments.mockResolvedValue([])
    mockedApi.apiCredentials.mockResolvedValue([])
    mockedApi.metrics.mockResolvedValue([{
      id: 1,
      requestId: 'req-1',
      startedAtUtc: '2026-09-09T10:02:00Z',
      logicalModel: 'agic-code-fast',
      surface: 'chat_completions',
      deploymentId: 'deployment-1',
      nodeId: 'node-1',
      apiCredentialId: null,
      statusCode: 200,
      durationMilliseconds: 1040,
      attemptCount: 2,
      isStreaming: true,
      upstreamHeaderMilliseconds: 38,
      timeToFirstByteMilliseconds: 120,
      inputTokens: 17,
      outputTokens: 6,
      totalTokens: 23,
      errorCode: null
    }])
    mockedApi.metricsSummary.mockResolvedValue({
      windowHours: 24,
      sinceUtc: '2026-09-08T10:00:00Z',
      requestCount: 125,
      successCount: 124,
      errorCount: 1,
      successRatePercent: 99.2,
      p50DurationMilliseconds: 900,
      p95DurationMilliseconds: 1800,
      p50TimeToFirstByteMilliseconds: 120,
      p95TimeToFirstByteMilliseconds: 350,
      averageUpstreamHeaderMilliseconds: 40,
      inputTokens: 1000,
      outputTokens: 500,
      totalTokens: 1500,
      tokenObservedRequests: 100,
      failoverRequests: 2,
      streamingRequests: 90,
      byModel: [{
        logicalModel: 'agic-code-fast',
        requestCount: 125,
        errorCount: 1,
        averageDurationMilliseconds: 900,
        averageTimeToFirstByteMilliseconds: 120,
        outputTokens: 500
      }],
      byNode: [{
        nodeId: 'node-1',
        requestCount: 125,
        errorCount: 1,
        averageDurationMilliseconds: 900,
        p95DurationMilliseconds: 1800,
        outputTokens: 500
      }]
    })
    mockedApi.audit.mockResolvedValue([{
      id: 1,
      occurredAtUtc: '2026-09-09T10:01:00Z',
      actor: 'admin@agic.it',
      action: 'routing.update',
      entityType: 'routing_policy',
      entityId: '1',
      sourceIp: '10.0.0.5',
      detailsJson: '{"previous":"WeightedLeastLoaded","current":"RoundRobin"}'
    }])
    mockedApi.testNodeConnection.mockResolvedValue({
      nodeId: 'node-1',
      nodeName: 'dgx-01',
      serviceRoot: 'http://10.0.0.21:8000/vllm',
      healthUrl: 'http://10.0.0.21:8000/vllm/health',
      modelsUrl: 'http://10.0.0.21:8000/vllm/v1/models',
      chatCompletionsUrl: 'http://10.0.0.21:8000/vllm/v1/chat/completions',
      responsesUrl: 'http://10.0.0.21:8000/vllm/v1/responses',
      success: true,
      health: { url: 'http://10.0.0.21:8000/vllm/health', success: true, statusCode: 200, latencyMilliseconds: 12 },
      openAi: { url: 'http://10.0.0.21:8000/vllm/v1/models', success: true, statusCode: 200, latencyMilliseconds: 15 }
    })
  })

  it('renders fleet and inference observability on the dashboard', async () => {
    render(<App />)

    expect(await screen.findByText('dgx-01')).toBeInTheDocument()
    expect(screen.getByText('1 H / 0 D')).toBeInTheDocument()
    expect(screen.getByText('12 ms')).toBeInTheDocument()
    expect(screen.getByText('4 ok')).toBeInTheDocument()
    expect(screen.getByText('42')).toBeInTheDocument()
    expect(screen.getByText('Routing: Weighted least loaded')).toBeInTheDocument()
    expect(screen.getByText('99.2%')).toBeInTheDocument()
    expect(screen.getByText('350 ms')).toBeInTheDocument()
    expect(screen.getByText('500')).toBeInTheDocument()
  })

  it('navigates to the DGX management view and tests the complete service root', async () => {
    const user = userEvent.setup()
    render(<App />)

    await screen.findByText('dgx-01')
    await user.click(screen.getByRole('button', { name: 'DGX Nodes' }))
    await user.click(screen.getByRole('button', { name: 'Test' }))

    expect(await screen.findByText('✓ Connection test: dgx-01')).toBeInTheDocument()
    expect(screen.getByText(/vllm\/v1\/chat\/completions/)).toBeInTheDocument()
  })

  it('exposes live routing policy management', async () => {
    const user = userEvent.setup()
    render(<App />)

    await screen.findByText('dgx-01')
    await user.click(screen.getByRole('button', { name: 'Routing' }))
    await user.selectOptions(screen.getByLabelText('Routing strategy'), 'RoundRobin')
    await user.click(screen.getByRole('button', { name: 'Apply routing strategy' }))

    expect(mockedApi.updateRouting).toHaveBeenCalledWith('RoundRobin')
  })

  it('shows inference observability by model, node and request', async () => {
    const user = userEvent.setup()
    render(<App />)

    await screen.findByText('dgx-01')
    await user.click(screen.getByRole('button', { name: 'Request Metrics' }))

    expect(screen.getByRole('heading', { name: 'Inference observability', exact: true })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'By logical model' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'By DGX node' })).toBeInTheDocument()
    expect(screen.getByText('Chat Completions · SSE')).toBeInTheDocument()
    expect(screen.getByText('2 · failover')).toBeInTheDocument()
    expect(screen.getByText('23')).toBeInTheDocument()
  })

  it('shows the administrative audit trail', async () => {
    const user = userEvent.setup()
    render(<App />)

    await screen.findByText('dgx-01')
    await user.click(screen.getByRole('button', { name: 'Audit Trail' }))

    expect(screen.getByText('admin@agic.it')).toBeInTheDocument()
    expect(screen.getByText('routing.update')).toBeInTheDocument()
  })
})
