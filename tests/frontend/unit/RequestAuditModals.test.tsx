import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import RequestAuditDetailModal from '../../../src/LlmProxy.Admin/src/RequestAuditDetail'
import RequestAuditSummaryModal from '../../../src/LlmProxy.Admin/src/RequestAuditSummaryModal'

const detail = {
  id: 7,
  requestId: '52f92b07-dcca-41db-9746-2fc841d71a3c',
  startedAtUtc: '2026-10-04T08:00:00Z',
  completedAtUtc: '2026-10-04T08:00:01Z',
  surface: 'chat_completions',
  method: 'POST',
  path: '/v1/chat/completions',
  logicalModel: 'agic-code-fast',
  apiCredentialId: 'credential-1',
  statusCode: 200,
  requestContentType: 'application/json',
  responseContentType: 'application/json',
  requestBody: '{"model":"agic-code-fast","messages":[{"role":"user","content":"Build an API"}]}',
  responseBody: '{"choices":[{"message":{"content":"Implemented the API"}}]}',
  deploymentId: 'deployment-1',
  nodeId: 'node-1',
  attemptCount: 1,
  isStreaming: false,
  timeToFirstByteMilliseconds: 42,
  inputTokens: 12,
  outputTokens: 8,
  totalTokens: 20,
  errorCode: null
}

describe('request audit modals', () => {
  const createObjectUrl = vi.fn(() => 'blob:request-audit')
  const revokeObjectUrl = vi.fn()
  const anchorClick = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: createObjectUrl,
      revokeObjectURL: revokeObjectUrl
    })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(anchorClick)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('opens request detail as a modal and offers complete JSON and Markdown exports', async () => {
    const user = userEvent.setup()
    render(<RequestAuditDetailModal
      detail={detail}
      onClose={vi.fn()}
      userLabel="admin@example.com"
      credentialLabel="Project Alpha"
      nodeLabel="inference-01"
    />)

    expect(screen.getByRole('dialog', { name: 'Request detail' })).toBeInTheDocument()
    expect(screen.getAllByText('agic-code-fast')).toHaveLength(2)
    expect(screen.getByText(/Build an API/)).toBeInTheDocument()
    expect(screen.getByText(/Implemented the API/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Download JSON' }))
    await user.click(screen.getByRole('button', { name: 'Download Markdown' }))

    expect(createObjectUrl).toHaveBeenCalledTimes(2)
    expect(anchorClick).toHaveBeenCalledTimes(2)
    const jsonBlob = createObjectUrl.mock.calls[0][0] as Blob
    const markdownBlob = createObjectUrl.mock.calls[1][0] as Blob
    expect(jsonBlob.type).toContain('application/json')
    expect(markdownBlob.type).toContain('text/markdown')
  })

  it('shows a persisted administrator summary and can regenerate it with model/node overrides', async () => {
    const user = userEvent.setup()
    const onRegenerate = vi.fn(async () => undefined)
    render(<RequestAuditSummaryModal
      summary={{
        contentLogId: 7,
        summary: 'Tipo progetto: sviluppo software.\n- Implementata una API in modo sintetico.',
        logicalModel: 'agic-code-fast',
        nodeId: 'node-1',
        deploymentId: 'deployment-1',
        generatedBy: 'admin@example.com',
        generatedAtUtc: '2026-10-04T08:05:00Z',
        updatedAtUtc: '2026-10-04T08:05:00Z'
      }}
      models={[
        { id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'provider-a', supportsStreaming: true, supportsTools: true, surface: 'OpenAi', enabled: true },
        { id: 'model-2', publicName: 'summary-fast', providerModelName: 'provider-b', supportsStreaming: true, supportsTools: false, surface: 'OpenAi', enabled: true }
      ]}
      nodes={[
        { id: 'node-1', name: 'inference-01', baseAddress: 'http://node-1', enabled: true, status: 'Healthy', weight: 1, maxConcurrency: 4, consecutiveHealthSuccesses: 1, consecutiveHealthFailures: 0 },
        { id: 'node-2', name: 'inference-02', baseAddress: 'http://node-2', enabled: true, status: 'Healthy', weight: 1, maxConcurrency: 4, consecutiveHealthSuccesses: 1, consecutiveHealthFailures: 0 }
      ]}
      busy={false}
      onClose={vi.fn()}
      onRegenerate={onRegenerate}
    />)

    expect(screen.getByRole('dialog', { name: 'Request summary' })).toBeInTheDocument()
    expect(screen.getByText(/Tipo progetto: sviluppo software/)).toBeInTheDocument()

    await user.selectOptions(screen.getByLabelText('Summary regeneration model'), 'summary-fast')
    await user.selectOptions(screen.getByLabelText('Summary regeneration node'), 'node-2')
    await user.click(screen.getByRole('button', { name: 'Regenerate summary' }))

    expect(onRegenerate).toHaveBeenCalledWith('summary-fast', 'node-2')
  })
})
