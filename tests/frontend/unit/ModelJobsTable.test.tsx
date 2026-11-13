import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import ModelJobsTable, { filterJobs, paginate } from '../../../src/LlmProxy.Admin/src/ModelJobsTable'
import type { JobRow } from '../../../src/LlmProxy.Admin/src/ModelJobsTable'

const row = (patch: Partial<JobRow> = {}): JobRow => ({
  id: 'r' + Math.random(), type: 'download', title: 'model', status: 'completed', detail: 'done',
  startedAtUtc: '2026-11-13T10:00:00Z', actions: [], ...patch
})

describe('paginate', () => {
  it('slices, clamps the page and reports the range', () => {
    const rows = Array.from({ length: 23 }, (_, index) => index)
    expect(paginate(rows, 1, 10)).toMatchObject({ pages: 3, page: 1, from: 1, to: 10 })
    expect(paginate(rows, 3, 10)).toMatchObject({ items: [20, 21, 22], from: 21, to: 23 })
    expect(paginate(rows, 99, 10).page).toBe(3)
    expect(paginate([], 1, 10)).toMatchObject({ pages: 1, from: 0, to: 0, items: [] })
  })
})

describe('filterJobs', () => {
  const running = row({ status: 'running' })
  const failed = row({ status: 'failed' })
  const waiting = row({ status: 'completed', actions: [{ key: 'start', label: 'Start', primary: true, onClick: vi.fn() }] })
  const done = row({ status: 'completed' })

  it('keeps running, failed and waiting-for-the-operator rows in the default view', () => {
    expect(filterJobs([running, failed, waiting, done], 'attention')).toEqual([running, failed, waiting])
    expect(filterJobs([running, failed, waiting, done], 'active')).toEqual([running])
    expect(filterJobs([running, failed, waiting, done], 'done')).toEqual([done])
    expect(filterJobs([running, failed, waiting, done], 'all')).toHaveLength(4)
  })
})

describe('ModelJobsTable', () => {
  it('lists newest first, runs row actions and pages through the rows', async () => {
    const onStart = vi.fn()
    const rows = [
      row({ id: 'old', title: 'old-model', status: 'running', startedAtUtc: '2026-11-13T08:00:00Z', percent: 40 }),
      row({ id: 'new', title: 'new-model', status: 'running', startedAtUtc: '2026-11-13T11:00:00Z', actions: [{ key: 'start', label: 'Start', onClick: onStart }] }),
      ...Array.from({ length: 12 }, (_, index) => row({ id: 'm' + index, title: 'extra-' + index, status: 'running', startedAtUtc: `2026-11-12T0${index % 10}:00:00Z` }))
    ]
    render(<ModelJobsTable rows={rows} />)

    const first = screen.getAllByRole('row')[1]
    expect(within(first).getByText('new-model')).toBeInTheDocument()
    await userEvent.click(within(first).getByRole('button', { name: 'Start' }))
    expect(onStart).toHaveBeenCalled()

    expect(screen.getByText('1–10 of 14')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Next' }))
    expect(screen.getByText('11–14 of 14')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Rows per page'), '25')
    expect(screen.getByText('1–14 of 14')).toBeInTheDocument()
  })

  it('opens a failure on its own and folds it away on request', async () => {
    render(<ModelJobsTable rows={[row({ status: 'failed', expanded: <p>Agent said no</p> })]} />)

    expect(screen.getByText('Agent said no')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Hide' }))
    expect(screen.queryByText('Agent said no')).not.toBeInTheDocument()
  })

  it('keeps finished history behind its filter', async () => {
    render(<ModelJobsTable rows={[row({ title: 'finished-model' }), row({ title: 'live-model', status: 'running' })]} />)

    expect(screen.queryByText('finished-model')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Completed/ }))
    expect(screen.getByText('finished-model')).toBeInTheDocument()
  })
})
