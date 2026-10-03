import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({
  usageGroups: vi.fn(),
  governanceCredentials: vi.fn(),
  rateLimits: vi.fn(),
  platformUsers: vi.fn(),
  userRateLimits: vi.fn(),
  groupRateLimits: vi.fn(),
  usageUsers: vi.fn(),
  createUserRateLimit: vi.fn(),
  updateUserRateLimit: vi.fn(),
  deleteUserRateLimit: vi.fn(),
  clearUserOutputTokenBudget: vi.fn(),
  createGroupRateLimit: vi.fn(),
  updateGroupRateLimit: vi.fn(),
  clearGroupOutputTokenBudget: vi.fn(),
  deleteGroupRateLimit: vi.fn(),
  updateCredentialCallerGovernance: vi.fn(),
  usageSummary: vi.fn(),
  models: vi.fn(),
  createUsageGroup: vi.fn(),
  assignCredentialUsageGroup: vi.fn(),
  clearCredentialUsageGroup: vi.fn(),
  createRateLimit: vi.fn(),
  updateRateLimit: vi.fn(),
  deleteRateLimit: vi.fn(),
  rotateApiCredential: vi.fn(),
  setOutputTokenBudget: vi.fn(),
  clearOutputTokenBudget: vi.fn()
}))

vi.mock('../../../src/LlmProxy.Admin/src/api', () => ({ api: mockedApi }))

import Governance from '../../../src/LlmProxy.Admin/src/Governance'

const group = {
  id: 'group-1', name: 'Development CRM', description: 'CRM team',
  createdAtUtc: '2026-09-14T10:00:00Z', updatedAtUtc: '2026-09-14T10:00:00Z', credentialCount: 1, userCount: 1
}

const credential = {
  id: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', enabled: true,
  createdAtUtc: '2026-09-14T10:00:00Z', usageGroupId: 'group-1', kind: 'organization', enforceCallerGovernance: false
}

const usage = {
  windowDays: 30,
  sinceUtc: '2026-08-18T00:00:00Z',
  windowGranularity: 'utc_day',
  rawRetentionDays: 90,
  rollupRetentionDays: 730,
  rawRequestCount: 12,
  rolledUpRequestCount: 30,
  historicalRollupsUsed: true,
  requestCount: 42,
  errorCount: 2,
  inputTokens: 1000,
  outputTokens: 500,
  totalTokens: 1500,
  rateLimitedRequests: 3,
  capacityExhaustedRequests: 1,
  groups: [{ usageGroupId: 'group-1', name: 'Development CRM', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3, averageTtftMilliseconds: 210, averageDurationMilliseconds: 1200 }],
  credentials: [{ apiCredentialId: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', usageGroupId: 'group-1', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }],
  models: [{ logicalModel: 'agic-code-fast', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }]
}

describe('Usage governance', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedApi.usageGroups.mockResolvedValue([group])
    mockedApi.governanceCredentials.mockResolvedValue([credential])
    mockedApi.rateLimits.mockResolvedValue([])
    mockedApi.identityUsers.mockResolvedValue([{ tenantId: 'tenant-1', objectId: 'object-1', principalName: 'user@example.com', credentialCount: 1, activeCredentialCount: 1, firstCredentialCreatedAtUtc: '2026-09-17T06:00:00Z' }])
    mockedApi.userRateLimits.mockResolvedValue([])
    mockedApi.createUserRateLimit.mockResolvedValue({ id: 'user-rate-1' })
    mockedApi.updateUserRateLimit.mockResolvedValue({})
    mockedApi.deleteUserRateLimit.mockResolvedValue(undefined)
    mockedApi.usageSummary.mockResolvedValue(usage)
    mockedApi.models.mockResolvedValue([{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'provider', supportsStreaming: true, supportsTools: true, enabled: true }])
    mockedApi.createUsageGroup.mockResolvedValue(group)
    mockedApi.assignCredentialUsageGroup.mockResolvedValue(undefined)
    mockedApi.clearCredentialUsageGroup.mockResolvedValue(undefined)
    mockedApi.createRateLimit.mockResolvedValue({ id: 'rate-1' })
    mockedApi.updateRateLimit.mockResolvedValue({})
    mockedApi.deleteRateLimit.mockResolvedValue(undefined)
  })

  it('shows consolidated usage and makes historical rollup coverage explicit', async () => {
    render(<Governance />)

    expect(await screen.findByRole('heading', { name: 'Usage & Governance' })).toBeInTheDocument()
    expect(screen.getAllByText('Development CRM').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Copilot CRM').length).toBeGreaterThan(0)
    expect(screen.getAllByText('agic-code-fast').length).toBeGreaterThan(0)
    expect(screen.getByTestId('historical-rollup-notice')).toHaveTextContent('30 rolled-up requests + 12 raw requests')
    expect(screen.getByText(/raw request metrics 90d · daily usage rollups 730d/)).toBeInTheDocument()
    expect(mockedApi.usageSummary).toHaveBeenCalledWith(30)
  })

  it('supports long-term UTC-day reporting windows', async () => {
    const user = userEvent.setup()
    render(<Governance />)
    await screen.findByRole('heading', { name: 'Usage & Governance' })

    await user.selectOptions(screen.getByLabelText('Usage window'), '365')
    await waitFor(() => expect(mockedApi.usageSummary).toHaveBeenLastCalledWith(365))
  })

  it('creates groups and rate-limit policies through the admin API', async () => {
    const user = userEvent.setup()
    render(<Governance />)
    await screen.findByRole('heading', { name: 'Create usage group' })

    await user.type(screen.getByPlaceholderText('Development CRM'), 'Platform')
    await user.type(screen.getByPlaceholderText('Copilot usage for the CRM team'), 'Platform developers')
    await user.click(screen.getByRole('button', { name: 'Create group' }))

    await waitFor(() => expect(mockedApi.createUsageGroup).toHaveBeenCalledWith({ name: 'Platform', description: 'Platform developers' }))

    const credentialLimitForm = screen.getByRole('heading', { name: 'Add rate limit' }).closest('section')!
    const requests = credentialLimitForm.querySelector('input[type="number"]') as HTMLInputElement
    await user.clear(requests)
    await user.type(requests, '2')
    const windowSeconds = credentialLimitForm.querySelectorAll('input[type="number"]')[1] as HTMLInputElement
    await user.click(windowSeconds)
    await user.keyboard('{Control>}a{/Control}10')
    await user.click(screen.getByRole('button', { name: 'Add rate limit' }))

    await waitFor(() => expect(mockedApi.createRateLimit).toHaveBeenCalledWith(expect.objectContaining({
      apiCredentialId: 'credential-1', requestsPerWindow: 2, windowSeconds: 10, enabled: true
    })))
  })
})
