import { useState } from 'react'
import { Modal } from './UiPrimitives'

export type RequestAuditDetailData = {
  id?: number
  requestId: string
  startedAtUtc: string
  completedAtUtc: string
  surface: string
  method: string
  path: string
  logicalModel?: string | null
  apiCredentialId?: string | null
  statusCode: number
  requestContentType?: string | null
  responseContentType?: string | null
  requestBody: string
  responseBody: string
  deploymentId?: string | null
  nodeId?: string | null
  usageGroupId?: string | null
  attemptCount?: number | null
  isStreaming?: boolean | null
  timeToFirstByteMilliseconds?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  errorCode?: string | null
}

type RequestAuditDetailModalProps = {
  detail: RequestAuditDetailData | null
  title?: string
  onClose: () => void
  userLabel?: string | null
  credentialLabel?: string | null
  nodeLabel?: string | null
}

export default function RequestAuditDetailModal({ detail, title = 'Request detail', onClose, userLabel, credentialLabel, nodeLabel }: RequestAuditDetailModalProps) {
  const [responseView, setResponseView] = useState<'text' | 'json'>('text')
  if (!detail) return null
  const streamAudit = parseStreamingAudit(detail.responseBody)

  return <Modal
    open
    title={title}
    description={`${friendlySurface(detail.surface)} · HTTP ${detail.statusCode}`}
    onClose={onClose}
    className="requestAuditModal"
  >
    <div className="requestAuditDetail">
      <div className="requestAuditToolbar">
        <div>
          <strong>{detail.logicalModel ?? 'Unknown model'}</strong>
          <span className="mono">{detail.requestId}</span>
        </div>
        <div className="actions">
          <button type="button" className="secondary" onClick={() => downloadJson(detail, { userLabel, credentialLabel, nodeLabel })}>Download JSON</button>
          <button type="button" className="secondary" onClick={() => downloadMarkdown(detail, { userLabel, credentialLabel, nodeLabel })}>Download Markdown</button>
        </div>
      </div>

      <div className="statusGrid requestAuditStatusGrid">
        <Meta label="Started" value={formatDate(detail.startedAtUtc)} />
        <Meta label="Completed" value={formatDate(detail.completedAtUtc)} />
        <Meta label="User" value={userLabel ?? '—'} />
        <Meta label="Credential" value={credentialLabel ?? detail.apiCredentialId ?? '—'} mono={!credentialLabel} />
        <Meta label="Method / path" value={`${detail.method} ${detail.path}`} mono />
        <Meta label="Model" value={detail.logicalModel ?? '—'} />
        <Meta label="Node" value={nodeLabel ?? detail.nodeId ?? '—'} mono={!nodeLabel} />
        <Meta label="Deployment" value={detail.deploymentId ?? '—'} mono />
        <Meta label="Attempts" value={detail.attemptCount ?? '—'} />
        <Meta label="Streaming" value={detail.isStreaming == null ? '—' : detail.isStreaming ? 'Yes' : 'No'} />
        <Meta label="TTFT" value={detail.timeToFirstByteMilliseconds == null ? '—' : `${detail.timeToFirstByteMilliseconds} ms`} />
        <Meta label="Tokens" value={tokenSummary(detail)} />
        <Meta label="Request type" value={detail.requestContentType ?? '—'} mono />
        <Meta label="Response type" value={detail.responseContentType ?? '—'} mono />
        <Meta label="Usage group" value={detail.usageGroupId ?? '—'} mono />
        <Meta label="Error" value={detail.errorCode ?? '—'} mono />
        {streamAudit && <>
          <Meta label="Stream state" value={streamAudit.state} />
          <Meta label="SSE events" value={streamAudit.eventsProcessed} />
          <Meta label="Partial output" value={!streamAudit.complete || streamAudit.truncated ? 'Yes' : 'No'} />
          <Meta label="Stream reason" value={streamAudit.reason ?? '—'} />
        </>}
      </div>

      <div className="requestAuditPayloadGrid">
        <PayloadBlock title="Request body" body={detail.requestBody} contentType={detail.requestContentType} />
        <div>
          {streamAudit && <div className="actions requestAuditStreamTabs">
            <button type="button" className={responseView === 'text' ? '' : 'secondary'} onClick={() => setResponseView('text')}>Readable response</button>
            <button type="button" className={responseView === 'json' ? '' : 'secondary'} onClick={() => setResponseView('json')}>Reconstructed JSON</button>
          </div>}
          <PayloadBlock
            title={streamAudit && responseView === 'text' ? 'Generated content' : 'Response body'}
            body={streamAudit && responseView === 'text' ? readableStreamResponse(streamAudit) : detail.responseBody}
            contentType={streamAudit && responseView === 'text' ? 'text/plain' : streamAudit ? 'application/json' : detail.responseContentType}
          />
        </div>
      </div>
    </div>
  </Modal>
}

function Meta({ label, value, mono = false }: { label: string; value: string | number; mono?: boolean }) {
  return <div><span>{label}</span><strong className={mono ? 'mono' : undefined}>{value}</strong></div>
}

function PayloadBlock({ title, body, contentType }: { title: string; body: string; contentType?: string | null }) {
  return <div className="payloadBlock requestAuditPayloadBlock">
    <div className="payloadHeader"><div><h3>{title}</h3>{contentType && <span className="muted">{contentType}</span>}</div><button type="button" className="secondary" onClick={() => void navigator.clipboard.writeText(body)}>Copy</button></div>
    <pre className="payload">{pretty(body)}</pre>
  </div>
}

type ExportContext = { userLabel?: string | null; credentialLabel?: string | null; nodeLabel?: string | null }

function downloadJson(detail: RequestAuditDetailData, context: ExportContext) {
  const payload = buildExportObject(detail, context)
  downloadText(
    `llmproxy-request-audit-${safeFilePart(detail.requestId)}.json`,
    JSON.stringify(payload, null, 2) + '\n',
    'application/json;charset=utf-8'
  )
}

function downloadMarkdown(detail: RequestAuditDetailData, context: ExportContext) {
  const lines = [
    '# LlmProxy request audit',
    '',
    `- **Request ID:** ${mdValue(detail.requestId)}`,
    `- **Started:** ${mdValue(detail.startedAtUtc)}`,
    `- **Completed:** ${mdValue(detail.completedAtUtc)}`,
    `- **Surface:** ${mdValue(friendlySurface(detail.surface))}`,
    `- **HTTP status:** ${detail.statusCode}`,
    `- **Method / path:** ${mdValue(`${detail.method} ${detail.path}`)}`,
    `- **Model:** ${mdValue(detail.logicalModel ?? '—')}`,
    `- **User:** ${mdValue(context.userLabel ?? '—')}`,
    `- **Credential:** ${mdValue(context.credentialLabel ?? detail.apiCredentialId ?? '—')}`,
    `- **Node:** ${mdValue(context.nodeLabel ?? detail.nodeId ?? '—')}`,
    `- **Deployment:** ${mdValue(detail.deploymentId ?? '—')}`,
    `- **Attempts:** ${mdValue(detail.attemptCount ?? '—')}`,
    `- **Streaming:** ${detail.isStreaming == null ? '—' : detail.isStreaming ? 'yes' : 'no'}`,
    `- **TTFT:** ${detail.timeToFirstByteMilliseconds == null ? '—' : `${detail.timeToFirstByteMilliseconds} ms`}`,
    `- **Tokens:** ${mdValue(tokenSummary(detail))}`,
    `- **Error:** ${mdValue(detail.errorCode ?? '—')}`,
    '',
    '## Request body',
    '',
    fencedBody(detail.requestBody, detail.requestContentType),
    '',
    '## Response body',
    '',
    fencedBody(detail.responseBody, detail.responseContentType),
    ''
  ]
  downloadText(
    `llmproxy-request-audit-${safeFilePart(detail.requestId)}.md`,
    lines.join('\n'),
    'text/markdown;charset=utf-8'
  )
}

function buildExportObject(detail: RequestAuditDetailData, context: ExportContext) {
  return {
    format: 'llmproxy.request-audit.v1',
    exportedAtUtc: new Date().toISOString(),
    requestId: detail.requestId,
    startedAtUtc: detail.startedAtUtc,
    completedAtUtc: detail.completedAtUtc,
    surface: detail.surface,
    method: detail.method,
    path: detail.path,
    logicalModel: detail.logicalModel ?? null,
    apiCredentialId: detail.apiCredentialId ?? null,
    credential: context.credentialLabel ?? null,
    user: context.userLabel ?? null,
    statusCode: detail.statusCode,
    deploymentId: detail.deploymentId ?? null,
    nodeId: detail.nodeId ?? null,
    node: context.nodeLabel ?? null,
    usageGroupId: detail.usageGroupId ?? null,
    attemptCount: detail.attemptCount ?? null,
    isStreaming: detail.isStreaming ?? null,
    timeToFirstByteMilliseconds: detail.timeToFirstByteMilliseconds ?? null,
    inputTokens: detail.inputTokens ?? null,
    outputTokens: detail.outputTokens ?? null,
    totalTokens: detail.totalTokens ?? null,
    errorCode: detail.errorCode ?? null,
    request: exportBody(detail.requestBody, detail.requestContentType),
    response: exportBody(detail.responseBody, detail.responseContentType)
  }
}

function exportBody(rawBody: string, contentType?: string | null) {
  const json = parseJson(rawBody)
  return {
    contentType: contentType ?? null,
    rawBody,
    ...(json === undefined ? {} : { json })
  }
}

function downloadText(filename: string, content: string, contentType: string) {
  const blob = new Blob([content], { type: contentType })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 0)
}

function fencedBody(body: string, contentType?: string | null) {
  const language = isJson(body, contentType) ? 'json' : 'text'
  const longestRun = Math.max(0, ...Array.from(body.matchAll(/`+/g), match => match[0].length))
  const fence = '`'.repeat(Math.max(3, longestRun + 1))
  return `${fence}${language}\n${pretty(body)}\n${fence}`
}

function isJson(body: string, contentType?: string | null) {
  return contentType?.toLowerCase().includes('json') === true || parseJson(body) !== undefined
}

function parseJson(value: string): unknown | undefined {
  try { return JSON.parse(value) as unknown } catch { return undefined }
}

function pretty(value: string) {
  const parsed = parseJson(value)
  return parsed === undefined ? value : JSON.stringify(parsed, null, 2)
}

function friendlySurface(value: string) {
  return ({ chat_completions: 'Chat Completions', responses: 'Responses', systemone: 'System One', systemone_test: 'System One test', model_test: 'Model test' } as Record<string, string>)[value] ?? value
}

function tokenSummary(detail: RequestAuditDetailData) {
  if (detail.inputTokens == null && detail.outputTokens == null && detail.totalTokens == null) return '—'
  const parts = []
  if (detail.inputTokens != null) parts.push(`${detail.inputTokens} in`)
  if (detail.outputTokens != null) parts.push(`${detail.outputTokens} out`)
  if (detail.totalTokens != null) parts.push(`${detail.totalTokens} total`)
  return parts.join(' · ')
}

function formatDate(value: string) {
  return new Date(value).toLocaleString()
}

function safeFilePart(value: string) {
  return value.replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'request'
}

function mdValue(value: string | number) {
  return String(value).replace(/\\/g, '\\\\').replace(/\|/g, '\\|').replace(/\r?\n/g, ' ')
}

type StreamingAuditBody = {
  format: 'llmproxy.audit.stream.v1'
  streaming: true
  state: 'completed' | 'interrupted' | 'cancelled' | 'incomplete'
  complete: boolean
  reason?: string | null
  truncated: boolean
  eventsProcessed: number
  response: unknown
}

function parseStreamingAudit(body: string): StreamingAuditBody | null {
  try {
    const value: unknown = JSON.parse(body)
    if (!value || typeof value !== 'object' || !('format' in value) || value.format !== 'llmproxy.audit.stream.v1')
      return null
    return value as StreamingAuditBody
  } catch {
    return null
  }
}

function readableStreamResponse(audit: StreamingAuditBody): string {
  const response = audit.response as Record<string, unknown> | null
  if (!response || typeof response !== 'object') return '(No response content was received)'
  const result: string[] = []
  const choices = response.choices
  if (Array.isArray(choices)) {
    for (const choice of choices) {
      const message = choice?.message
      if (typeof message?.content === 'string') result.push(message.content)
      if (typeof message?.reasoning_content === 'string') result.push('Reasoning:\n' + message.reasoning_content)
      if (Array.isArray(message?.tool_calls)) result.push('Tool calls:\n' + JSON.stringify(message.tool_calls, null, 2))
      if (message?.function_call) result.push('Function call:\n' + JSON.stringify(message.function_call, null, 2))
      if (typeof message?.refusal === 'string') result.push('Refusal:\n' + message.refusal)
    }
  }
  const output = response.output
  if (Array.isArray(output)) {
    for (const item of output) {
      if (Array.isArray(item?.content)) {
        for (const part of item.content) {
          if (typeof part?.text === 'string') result.push(part.text)
          if (typeof part?.refusal === 'string') result.push('Refusal:\n' + part.refusal)
        }
      }
      if (typeof item?.arguments === 'string') result.push('Function call (' + (item.name ?? 'unknown') + '):\n' + item.arguments)
      if (Array.isArray(item?.summary)) {
        for (const part of item.summary) if (typeof part?.text === 'string') result.push('Reasoning summary:\n' + part.text)
      }
    }
  }
  return result.length ? result.join('\n\n') : JSON.stringify(response, null, 2)
}
