import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import InstalledModelCards from '../../../src/LlmProxy.Admin/src/InstalledModelCards'
import type { VerificationState } from '../../../src/LlmProxy.Admin/src/InstalledModelCards'
import type { ManagedInstallation } from '../../../src/LlmProxy.Admin/src/types'

const installation = (patch: Partial<ManagedInstallation> = {}): ManagedInstallation => ({
  id: 'dep-1', modelId: 'm-1', logicalModel: 'muse', providerModelName: 'meta-models/Muse-Glimmer-30B', runtime: 'vllm',
  managedInstallationId: 'custom-1', enabled: false, agentStatus: 'running', capabilities: null,
  agentState: {
    installationId: 'custom-1', status: 'running', runtime: 'vllm', port: 18000,
    familyProfile: {
      id: 'muse-glimmer', title: 'Meta Muse Glimmer', source: 'https://dev.meta.ai/docs/muse-glimmer/deploy', verifiedOn: '2026-11-13',
      appliedArguments: ['--tool-call-parser', 'muse_glimmer'], overriddenByOperator: ['--reasoning-parser'],
      requirement: 'Needs vllm/vllm-openai:muse-glimmer.', enabled: true
    }
  },
  ...patch
})

function renderCards(options: { verifications?: Record<string, VerificationState>; canWrite?: boolean; item?: ManagedInstallation; onVerify?: () => void } = {}) {
  return render(<InstalledModelCards nodeId="n-1" installations={[options.item ?? installation()]} startJobs={[]}
    upstreamRevisions={[]} canWrite={options.canWrite ?? true} busy={null} agentAvailable
    verifications={options.verifications ?? {}} onVerify={options.onVerify ?? vi.fn()}
    onAction={vi.fn()} onCancelStart={vi.fn()} onEdit={vi.fn()} onCapabilities={vi.fn()} onBenchmark={vi.fn()} />)
}

describe('installed model card profile and verification', () => {
  it('shows which launch flags the family profile applied, which the operator overrode and where it came from', () => {
    renderCards()

    expect(screen.getByText('Meta Muse Glimmer')).toBeInTheDocument()
    expect(screen.getByText('--tool-call-parser muse_glimmer')).toBeInTheDocument()
    expect(screen.getByText('--reasoning-parser')).toBeInTheDocument()
    expect(screen.getByText(/Needs vllm\/vllm-openai:muse-glimmer/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /dev\.meta\.ai/ })).toHaveAttribute('href', 'https://dev.meta.ai/docs/muse-glimmer/deploy')
  })

  it('says so when no curated profile exists for the model', () => {
    renderCards({ item: installation({ agentState: { installationId: 'custom-1', status: 'running', runtime: 'vllm', familyProfile: null } }) })

    expect(screen.getByText(/No curated profile for this model/)).toBeInTheDocument()
  })

  it('lets a writer verify a running model and blocks it for read-only users and stopped models', async () => {
    const onVerify = vi.fn()
    const { rerender } = renderCards({ onVerify })
    await userEvent.click(screen.getByRole('button', { name: 'Verify capabilities' }))
    expect(onVerify).toHaveBeenCalledWith('dep-1')

    rerender(<InstalledModelCards nodeId="n-1" installations={[installation()]} startJobs={[]} upstreamRevisions={[]}
      canWrite={false} busy={null} agentAvailable verifications={{}} onVerify={onVerify}
      onAction={vi.fn()} onCancelStart={vi.fn()} onEdit={vi.fn()} onCapabilities={vi.fn()} onBenchmark={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Verify capabilities' })).toBeDisabled()

    rerender(<InstalledModelCards nodeId="n-1" installations={[installation({ agentStatus: 'stopped' })]} startJobs={[]} upstreamRevisions={[]}
      canWrite busy={null} agentAvailable verifications={{}} onVerify={onVerify}
      onAction={vi.fn()} onCancelStart={vi.fn()} onEdit={vi.fn()} onCapabilities={vi.fn()} onBenchmark={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Verify capabilities' })).toBeDisabled()
  })

  it('shows each verified capability with its own status and the reason', () => {
    renderCards({ verifications: { 'dep-1': {
      deploymentId: 'dep-1', model: 'muse', checkedAtUtc: '2026-11-13T10:00:00Z', durationMilliseconds: 900,
      checks: [
        { id: 'chat', status: 'ok', detail: 'Answered (finish_reason=stop).' },
        { id: 'tools', status: 'failed', detail: 'The model wrote tool-call syntax as plain text.' },
        { id: 'reasoning', status: 'inconclusive', detail: 'No reasoning trace appeared.' },
        { id: 'streaming', status: 'warning', detail: 'SSE events arrived but no [DONE].' }
      ]
    } } })

    const results = screen.getByRole('region', { name: 'Verified on the running model' })
    expect(within(results).getByText('Tool calling')).toBeInTheDocument()
    expect(within(results).getByText('The model wrote tool-call syntax as plain text.')).toBeInTheDocument()
    expect(within(results).getByText('Failed')).toBeInTheDocument()
    expect(within(results).getByText('Inconclusive')).toBeInTheDocument()
    expect(within(results).getByText('OK')).toBeInTheDocument()
  })

  it('shows a verification error instead of silently dropping it', () => {
    renderCards({ verifications: { 'dep-1': { error: 'HTTP 409: runtime is not running' } } })

    expect(screen.getByRole('alert')).toHaveTextContent('HTTP 409: runtime is not running')
  })

  it('shows progress while the checks run', () => {
    renderCards({ verifications: { 'dep-1': 'loading' } })

    expect(screen.getByRole('button', { name: 'Verifying…' })).toBeDisabled()
  })
})
