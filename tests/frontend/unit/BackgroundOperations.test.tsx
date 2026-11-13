import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import {
  agentUpdateOperation, installJobOperation, mergeOperations, parseAgentUpdateStatus, sortOperations, startJobOperation, summarizeOperations
} from '../../../src/LlmProxy.Admin/src/backgroundOperations'
import type { BackgroundOperation } from '../../../src/LlmProxy.Admin/src/backgroundOperations'
import ActivityCenter from '../../../src/LlmProxy.Admin/src/ActivityCenter'
import type { ModelInstallJob, ModelStartJob, PairedNodeStatus } from '../../../src/LlmProxy.Admin/src/types'

const pair = (patch: Partial<PairedNodeStatus> = {}): PairedNodeStatus => ({ nodeId: 'n1', mode: 'outbound', ...patch })
const installJob = (patch: Partial<ModelInstallJob> = {}): ModelInstallJob => ({
  id: 'j1', request: { catalogModelId: 'c', providerModelName: 'org/model', runtime: 'vllm' }, status: 'running',
  stage: 'downloading', percent: 42, detail: 'Downloading', createdAtUtc: '2026-11-13T00:00:00Z', ...patch
})
const startJob = (patch: Partial<ModelStartJob> = {}): ModelStartJob => ({
  id: 's1', installationId: 'i1', status: 'starting', detail: 'Starting', createdAtUtc: '2026-11-13T00:00:00Z', ...patch
})
const operation = (patch: Partial<BackgroundOperation> = {}): BackgroundOperation => ({
  id: 'op', kind: 'agent-update', title: 'Agent update · spark', detail: 'Installing', status: 'running', href: '/admin/infrastructure', firstSeenAtMs: 1, ...patch
})

describe('parseAgentUpdateStatus', () => {
  it('parses the updater status and rejects unknown states', () => {
    expect(parseAgentUpdateStatus('running:0.2.68:downloading:42')).toEqual({ state: 'running', target: '0.2.68', phase: 'downloading', detail: '42' })
    expect(parseAgentUpdateStatus('failed:0.2.68:verify:checksum_mismatch')?.state).toBe('failed')
    expect(parseAgentUpdateStatus('weird:1')).toBeNull()
    expect(parseAgentUpdateStatus(null)).toBeNull()
  })
})

describe('operation derivation', () => {
  it('reports an Agent upgrade in maintenance as running with the stage text, and needs_attention as attention', () => {
    const running = agentUpdateOperation(pair({ agentUpgradeTargetVersion: '0.2.68', agentUpgradeMaintenanceStage: 'draining' }), 'spark', 5).active
    expect(running?.status).toBe('running')
    expect(running?.title).toContain('v0.2.68')
    const attention = agentUpdateOperation(pair({ agentUpgradeTargetVersion: '0.2.68', agentUpgradeMaintenanceStage: 'needs_attention' }), 'spark', 5).active
    expect(attention?.status).toBe('attention')
  })

  it('uses the real download percentage and never invents one', () => {
    const withPercent = agentUpdateOperation(pair({ agentUpgradeTargetVersion: '0.2.68', agentUpdateStatus: 'running:0.2.68:downloading:61' }), 'spark', 5).active
    expect(withPercent?.progress).toBe(61)
    const without = agentUpdateOperation(pair({ agentUpgradeTargetVersion: '0.2.68', agentUpgradeMaintenanceStage: 'updating' }), 'spark', 5).active
    expect(without?.progress).toBeNull()
  })

  it('maps a finished updater state to a terminal operation', () => {
    expect(agentUpdateOperation(pair({ agentUpdateStatus: 'succeeded:0.2.68' }), 'spark', 5).terminal?.status).toBe('done')
    const failed = agentUpdateOperation(pair({ agentUpdateStatus: 'failed:0.2.68:verify:checksum_mismatch' }), 'spark', 5).terminal
    expect(failed?.status).toBe('failed')
    expect(failed?.detail).toContain('checksum mismatch')
  })

  it('maps model downloads and starts', () => {
    expect(installJobOperation(installJob(), 'n1', 'spark', 5).active?.progress).toBe(42)
    expect(installJobOperation(installJob({ status: 'completed' }), 'n1', 'spark', 5).terminal?.status).toBe('done')
    expect(installJobOperation(installJob({ status: 'failed', error: 'disk full' }), 'n1', 'spark', 5).terminal?.detail).toContain('disk full')
    expect(installJobOperation(installJob({ status: 'cancelled' }), 'n1', 'spark', 5)).toEqual({})
    expect(startJobOperation(startJob(), 'n1', 'spark', 'Model', 5).active?.status).toBe('running')
    expect(startJobOperation(startJob({ status: 'failed', error: 'oom\nstack' }), 'n1', 'spark', 'Model', 5).terminal?.detail).toContain('oom')
  })
})

describe('mergeOperations', () => {
  const none = new Set<string>()

  it('ignores old finished jobs that were never seen in flight', () => {
    const finished = operation({ status: 'done' })
    expect(mergeOperations({}, { active: [], terminal: [finished] }, none, 10)).toEqual({})
  })

  it('keeps the first-seen time while active, then becomes finished with the reported outcome', () => {
    const first = mergeOperations({}, { active: [operation({ firstSeenAtMs: 100 })], terminal: [] }, none, 100)
    const again = mergeOperations(first, { active: [operation({ firstSeenAtMs: 900 })], terminal: [] }, none, 900)
    expect(again.op.firstSeenAtMs).toBe(100)
    const failed = mergeOperations(again, { active: [], terminal: [operation({ status: 'failed', detail: 'boom' })] }, none, 1000)
    expect(failed.op).toMatchObject({ status: 'failed', detail: 'boom', firstSeenAtMs: 100, finishedAtMs: 1000 })
  })

  it('treats an operation that vanished without an outcome as finished, and keeps finished ones', () => {
    const first = mergeOperations({}, { active: [operation({ progress: 50 })], terminal: [] }, none, 100)
    const gone = mergeOperations(first, { active: [], terminal: [] }, none, 200)
    expect(gone.op).toMatchObject({ status: 'done', progress: 100, detail: 'Finished' })
    expect(mergeOperations(gone, { active: [], terminal: [] }, none, 300).op.status).toBe('done')
  })

  it('drops dismissed finished operations but always shows an active one with the same id', () => {
    const finished = { op: operation({ status: 'done' }) }
    expect(mergeOperations(finished, { active: [], terminal: [] }, new Set(['op']), 10)).toEqual({})
    expect(mergeOperations({}, { active: [operation()], terminal: [] }, new Set(['op']), 10).op.status).toBe('running')
  })
})

describe('sorting and summary', () => {
  it('puts attention first and summarises by severity', () => {
    const list = [operation({ id: 'a', status: 'done' }), operation({ id: 'b', status: 'running' }), operation({ id: 'c', status: 'attention' }), operation({ id: 'd', status: 'failed' })]
    expect(sortOperations(list).map(item => item.id)).toEqual(['c', 'd', 'b', 'a'])
    expect(summarizeOperations(list)).toMatchObject({ running: 1, attention: 1, failed: 1, done: 1, tone: 'attention', total: 4 })
    expect(summarizeOperations([operation()]).tone).toBe('running')
    expect(summarizeOperations([operation({ status: 'done' })]).tone).toBe('done')
  })
})

describe('ActivityCenter', () => {
  it('renders nothing without operations', () => {
    const { container } = render(<ActivityCenter operations={[]} onOpen={vi.fn()} onDismiss={vi.fn()} onClearFinished={vi.fn()} />)
    expect(container).toBeEmptyDOMElement()
  })

  it('shows the running count, opens the panel and navigates', async () => {
    const onOpen = vi.fn()
    render(<ActivityCenter operations={[operation(), operation({ id: 'two', title: 'Download · model' })]} onOpen={onOpen} onDismiss={vi.fn()} onClearFinished={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: /2 running/ }))
    expect(screen.getByText('Agent update · spark')).toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'Open' })[0])
    expect(onOpen).toHaveBeenCalledWith('/admin/infrastructure')
    expect(screen.queryByText('Agent update · spark')).not.toBeInTheDocument()
  })

  it('lets the operator dismiss and clear finished items, but not running ones', async () => {
    const onDismiss = vi.fn(); const onClear = vi.fn()
    render(<ActivityCenter operations={[operation({ id: 'done1', status: 'done' }), operation({ id: 'run1' })]} onOpen={vi.fn()} onDismiss={onDismiss} onClearFinished={onClear} />)
    await userEvent.click(screen.getByRole('button', { name: /running/ }))
    expect(screen.getAllByRole('button', { name: 'Dismiss' })).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    expect(onDismiss).toHaveBeenCalledWith('done1')
    await userEvent.click(screen.getByRole('button', { name: 'Clear finished' }))
    expect(onClear).toHaveBeenCalled()
  })

  it('closes with Escape', async () => {
    render(<ActivityCenter operations={[operation()]} onOpen={vi.fn()} onDismiss={vi.fn()} onClearFinished={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: /running/ }))
    expect(screen.getByRole('region', { name: 'Background activity' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('region', { name: 'Background activity' })).not.toBeInTheDocument()
  })
})
