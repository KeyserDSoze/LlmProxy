import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({
  overview: vi.fn(),
  nodes: vi.fn(),
  models: vi.fn(),
  deployments: vi.fn(),
  apiCredentials: vi.fn(),
  metrics: vi.fn(),
  createNode: vi.fn(),
  updateNode: vi.fn(),
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
    mockedApi.overview.mockResolvedValue({
      nodes: { total: 1, healthy: 1, unhealthy: 0, draining: 0 },
      models: 1,
      deployments: 1,
      activeRequests: 2,
      requestsToday: 42
    })
    mockedApi.nodes.mockResolvedValue([{
      id: 'node-1',
      name: 'dgx-01',
      baseAddress: 'http://10.0.0.21:8000',
      weight: 1,
      maxConcurrency: 4,
      enabled: true,
      status: 'Healthy',
      lastHealthCheckUtc: '2026-09-09T10:00:00Z'
    }])
    mockedApi.models.mockResolvedValue([])
    mockedApi.deployments.mockResolvedValue([])
    mockedApi.apiCredentials.mockResolvedValue([])
    mockedApi.metrics.mockResolvedValue([])
  })

  it('renders fleet information returned by the API', async () => {
    render(<App />)

    expect(await screen.findByText('dgx-01')).toBeInTheDocument()
    expect(screen.getByText('1/1')).toBeInTheDocument()
    expect(screen.getByText('42')).toBeInTheDocument()
  })

  it('navigates to the DGX management view', async () => {
    const user = userEvent.setup()
    render(<App />)

    await screen.findByText('dgx-01')
    await user.click(screen.getByRole('button', { name: 'DGX Nodes' }))

    expect(screen.getByRole('heading', { name: 'DGX nodes' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Add DGX node' })).toBeInTheDocument()
  })
})
