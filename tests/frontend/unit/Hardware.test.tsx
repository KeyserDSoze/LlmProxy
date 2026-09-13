import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({
  capacity: vi.fn(),
  models: vi.fn(),
  updateNodeHardwareMetrics: vi.fn(),
  updateCapacityProfile: vi.fn(),
  applyCapacityProfile: vi.fn(),
  clearCapacityProfile: vi.fn()
}))

vi.mock('../../../src/LlmProxy.Admin/src/api', () => ({ api: mockedApi }))

import Hardware from '../../../src/LlmProxy.Admin/src/Hardware'

const node = {
  id: 'node-1', name: 'dgx-01', baseAddress: 'http://10.0.0.21:8000/vllm', hardwareMetricsBaseAddress: null,
  enabled: true, status: 'Healthy', weight: 1, maxConcurrency: 4,
  consecutiveHealthSuccesses: 4, consecutiveHealthFailures: 0
}

const capacity = {
  nodes: [{ id: 'node-1', name: 'dgx-01', maxConcurrency: 4, activeRequests: 1, remaining: 3 }],
  deployments: [{
    id: 'deployment-1', nodeId: 'node-1', modelId: 'model-1', enabled: true, maxConcurrency: 2,
    effectiveMaxConcurrency: 2, activeRequests: 1, recommendedMaxConcurrency: null,
    benchmarkP95TtftMilliseconds: null, benchmarkP95DurationMilliseconds: null,
    sustainableOutputTokensPerSecond: null, benchmarkSource: null, benchmarkMeasuredAtUtc: null
  }]
}

describe('DGX capacity administration', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    capacity.deployments[0].effectiveMaxConcurrency = 2
    capacity.deployments[0].recommendedMaxConcurrency = null
    capacity.deployments[0].benchmarkSource = null
    mockedApi.capacity.mockImplementation(async () => capacity)
    mockedApi.models.mockResolvedValue([{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'provider', supportsStreaming: true, supportsTools: true, enabled: true }])
    mockedApi.updateNodeHardwareMetrics.mockResolvedValue({ id: 'node-1', hardwareMetricsBaseAddress: null })
    mockedApi.updateCapacityProfile.mockImplementation(async (_id: string, body: { recommendedMaxConcurrency: number; benchmarkSource: string }) => {
      capacity.deployments[0].recommendedMaxConcurrency = body.recommendedMaxConcurrency
      capacity.deployments[0].benchmarkSource = body.benchmarkSource
      return { ...capacity.deployments[0] }
    })
    mockedApi.applyCapacityProfile.mockImplementation(async () => {
      capacity.deployments[0].effectiveMaxConcurrency = capacity.deployments[0].recommendedMaxConcurrency ?? 2
      return { ...capacity.deployments[0] }
    })
    mockedApi.clearCapacityProfile.mockResolvedValue(undefined)
  })

  it('stores a recommendation without changing the active limit, then applies it explicitly', async () => {
    const user = userEvent.setup()
    const refresh = vi.fn().mockResolvedValue(undefined)
    render(<Hardware nodes={[node]} hardware={[]} refresh={refresh} />)

    expect(await screen.findByRole('heading', { name: 'Physical DGX capacity' })).toBeInTheDocument()
    expect(screen.getByText('HTTP 429 · Retry-After: 1')).toBeInTheDocument()
    expect(screen.getByText('Deployment active limit: 2')).toBeInTheDocument()

    const recommended = screen.getByLabelText('Recommended max concurrency')
    await user.click(recommended)
    await user.keyboard('{Control>}a{/Control}3')
    await user.type(screen.getByLabelText('Capacity P95 TTFT'), '420')
    await user.type(screen.getByLabelText('Benchmark source'), 'benchmark-results/run-001.json')
    await user.click(screen.getByRole('button', { name: 'Save recommendation' }))

    expect(mockedApi.updateCapacityProfile).toHaveBeenCalledWith('deployment-1', expect.objectContaining({
      recommendedMaxConcurrency: 3,
      p95TtftMilliseconds: 420,
      benchmarkSource: 'benchmark-results/run-001.json'
    }))
    expect(await screen.findByText('Capacity recommendation saved. Active production limits were not changed.')).toBeInTheDocument()
    expect(screen.getByText('Deployment active limit: 2')).toBeInTheDocument()

    await waitFor(() => expect(screen.getByRole('button', { name: 'Apply recommended' })).toBeEnabled())
    await user.click(screen.getByRole('button', { name: 'Apply recommended' }))

    expect(mockedApi.applyCapacityProfile).toHaveBeenCalledWith('deployment-1')
    expect(await screen.findByText('Recommended deployment capacity applied explicitly.')).toBeInTheDocument()
    expect(screen.getByText('Deployment active limit: 3')).toBeInTheDocument()
  })

  it('prevents applying a recommendation that exceeds the physical node limit', async () => {
    capacity.deployments[0].recommendedMaxConcurrency = 8
    capacity.deployments[0].benchmarkSource = 'run-high.json'
    render(<Hardware nodes={[node]} hardware={[]} refresh={vi.fn().mockResolvedValue(undefined)} />)

    expect(await screen.findByText(/Recommendation exceeds the current physical node limit/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Apply recommended' })).toBeDisabled()
  })
})
