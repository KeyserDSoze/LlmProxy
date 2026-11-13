import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mockedApi = vi.hoisted(() => ({ scheduleProductUpdate: vi.fn(), productUpdates: vi.fn() }))
vi.mock('../../../src/LlmProxy.Admin/src/api', () => ({ api: mockedApi }))

import UpdateBanner, { availableUpdateCount, latestAvailableRelease } from '../../../src/LlmProxy.Admin/src/UpdateBanner'
import type { ProductUpdateOverview } from '../../../src/LlmProxy.Admin/src/types'

const release = (version: string, isNewer = true) => ({
  version, title: 'r', publishedAtUtc: '2026-11-13T00:00:00Z', releaseUrl: '', isNewer,
  updateMode: 'standard', updateTitle: '', updateDescription: '', requiresHostRestart: false, operatorCommand: ''
})

const overview = (patch: Partial<ProductUpdateOverview> = {}): ProductUpdateOverview => ({
  currentVersion: '0.2.66', agentAvailable: true,
  policy: { mode: 'manual', timeZoneId: 'UTC', localHour: 2, localMinute: 0, dayOfWeek: 0, dayOfMonth: 1, updatedAtUtc: '2026-11-13T00:00:00Z' },
  agent: { installedVersion: '0.2.66', activeJob: null, recentJobs: [] },
  releases: [release('0.2.67'), release('0.2.68'), release('0.2.66', false)],
  ...patch
})

describe('update availability helpers', () => {
  it('finds the newest newer release whatever the API order and counts them', () => {
    const value = overview({ releases: [release('0.2.9'), release('0.2.10'), release('0.2.8', false)] })
    expect(latestAvailableRelease(value)?.version).toBe('0.2.10')
    expect(availableUpdateCount(value)).toBe(2)
    expect(latestAvailableRelease(null)).toBeNull()
  })
})

describe('UpdateBanner', () => {
  beforeEach(() => { window.localStorage.clear(); mockedApi.scheduleProductUpdate.mockReset() })
  afterEach(() => vi.restoreAllMocks())

  const renderBanner = (value: ProductUpdateOverview | null, canWrite = true, extra: Partial<Parameters<typeof UpdateBanner>[0]> = {}) =>
    render(<UpdateBanner overview={value} loadedVersion="0.2.66" canWrite={canWrite}
      onOpenReleases={vi.fn()} onRefresh={vi.fn().mockResolvedValue(undefined)} {...extra} />)

  it('announces the newest version and starts the update from the web app', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    mockedApi.scheduleProductUpdate.mockResolvedValue({})
    const onRefresh = vi.fn().mockResolvedValue(undefined)
    renderBanner(overview(), true, { onRefresh })

    expect(screen.getByText(/New version available: v0\.2\.68/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Update now' }))
    await waitFor(() => expect(mockedApi.scheduleProductUpdate).toHaveBeenCalledWith({ version: '0.2.68', force: true }))
    expect(onRefresh).toHaveBeenCalled()
  })

  it('does not start anything when the confirmation is declined', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderBanner(overview())
    await userEvent.click(screen.getByRole('button', { name: 'Update now' }))
    expect(mockedApi.scheduleProductUpdate).not.toHaveBeenCalled()
  })

  it('disables the action and explains why when the host update agent is down', () => {
    renderBanner(overview({ agentAvailable: false }))
    expect(screen.getByRole('button', { name: 'Update now' })).toBeDisabled()
    expect(screen.getByText(/update agent on the host is not reachable/i)).toBeInTheDocument()
  })

  it('offers no launch button to read-only administrators', () => {
    renderBanner(overview(), false)
    expect(screen.queryByRole('button', { name: 'Update now' })).not.toBeInTheDocument()
    expect(screen.getByText(/administrator with write access/i)).toBeInTheDocument()
  })

  it('can be dismissed until a newer version appears', async () => {
    const { rerender } = renderBanner(overview())
    await userEvent.click(screen.getByRole('button', { name: /Dismiss/ }))
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()

    rerender(<UpdateBanner overview={overview({ releases: [release('0.2.69'), release('0.2.68')] })} loadedVersion="0.2.66"
      canWrite onOpenReleases={vi.fn()} onRefresh={vi.fn()} />)
    expect(screen.getByText(/v0\.2\.69/)).toBeInTheDocument()
  })

  it('shows progress while an update runs and ignores dismissal', () => {
    window.localStorage.setItem('llmproxy.updateBanner.dismissedVersion', '0.2.68')
    renderBanner(overview({ agent: { installedVersion: '0.2.66', recentJobs: [], activeJob: {
      id: 'j', version: '0.2.68', scheduledForUtc: '2026-11-13T00:00:00Z', status: 'Running', currentStep: '0.2.67' } as never } }))
    expect(screen.getByText(/Update in progress: v0\.2\.68/)).toBeInTheDocument()
  })

  it('asks for a reload once the running version differs from the loaded interface', () => {
    renderBanner(overview({ currentVersion: '0.2.68', releases: [] }), true, { loadedVersion: '0.2.66' })
    expect(screen.getByText(/Updated to v0\.2\.68/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument()
  })

  it('renders nothing when there is no update', () => {
    renderBanner(overview({ releases: [release('0.2.66', false)] }))
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument()
  })
})
