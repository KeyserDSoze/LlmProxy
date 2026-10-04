import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import RequestAuditDetailModal from './RequestAuditDetail'
import RequestAuditSummaryModal from './RequestAuditSummaryModal'
import { requestAuditApi } from './requestAuditApi'
import type { RequestAuditSummary, RequestAuditSummarySettings, RequestAuditSummaryState } from './requestAuditTypes'
import type { ApiCredential, ContentLogDetail, ContentLogPage, ContentLogSettings, Model, Node } from './types'

type StatusFilter = 'all' | 'success' | 'error'
type AuditedContentLogSummary = ContentLogPage['items'][number] & RequestAuditSummaryState

export default function ContentLogs({ credentials, nodes, models: providedModels }: { credentials: ApiCredential[]; nodes: Node[]; models?: Model[] }) {
  const [result, setResult] = useState<ContentLogPage>({ items: [], total: 0, page: 1, pageSize: 50 })
  const [availableModels, setAvailableModels] = useState<Model[]>(providedModels ?? [])
  const [settings, setSettings] = useState<ContentLogSettings | null>(null)
  const [retentionDays, setRetentionDays] = useState(30)
  const [summarySettings, setSummarySettings] = useState<RequestAuditSummarySettings | null>(null)
  const [summarySystemPrompt, setSummarySystemPrompt] = useState('')
  const [summaryDefaultModel, setSummaryDefaultModel] = useState('')
  const [summaryDefaultNodeId, setSummaryDefaultNodeId] = useState('')
  const [selected, setSelected] = useState<ContentLogDetail | null>(null)
  const [selectedSummary, setSelectedSummary] = useState<RequestAuditSummary | null>(null)
  const [summaryLoadingId, setSummaryLoadingId] = useState<number | null>(null)
  const [summaryRegenerating, setSummaryRegenerating] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [model, setModel] = useState('')
  const [surface, setSurface] = useState('')
  const [credentialId, setCredentialId] = useState('')
  const [ownerKey, setOwnerKey] = useState('')
  const [status, setStatus] = useState<StatusFilter>('all')
  const [requestId, setRequestId] = useState('')
  const [fromLocal, setFromLocal] = useState('')
  const [toLocal, setToLocal] = useState('')
  const [live, setLive] = useState(true)
  const [loading, setLoading] = useState(true)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const credentialNames = useMemo(() => new Map(credentials.map(item => [item.id, item.name])), [credentials])
  const credentialById = useMemo(() => new Map(credentials.map(item => [item.id, item])), [credentials])
  const nodeNames = useMemo(() => new Map(nodes.map(item => [item.id, item.name])), [nodes])
  const openAiModels = useMemo(() => availableModels.filter(item => item.enabled && item.surface === 'OpenAi'), [availableModels])
  const owners = useMemo(() => {
    const byKey = new Map<string, { key: string; tenantId: string; objectId: string; label: string }>()
    for (const credential of credentials) {
      if (!credential.ownerTenantId || !credential.ownerObjectId) continue
      const key = ownerValue(credential.ownerTenantId, credential.ownerObjectId)
      if (!byKey.has(key)) {
        byKey.set(key, {
          key,
          tenantId: credential.ownerTenantId,
          objectId: credential.ownerObjectId,
          label: credential.ownerPrincipalName ?? credential.ownerObjectId
        })
      }
    }
    return Array.from(byKey.values()).sort((a, b) => a.label.localeCompare(b.label))
  }, [credentials])

  const selectedOwner = owners.find(item => item.key === ownerKey)
  const visibleCredentials = ownerKey
    ? credentials.filter(item => item.ownerTenantId === selectedOwner?.tenantId && item.ownerObjectId === selectedOwner?.objectId)
    : credentials

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const owner = owners.find(item => item.key === ownerKey)
      const next = await api.contentLogsQuery({
        page,
        pageSize,
        model: model || undefined,
        surface: surface || undefined,
        apiCredentialId: credentialId || undefined,
        ownerTenantId: owner?.tenantId,
        ownerObjectId: owner?.objectId,
        status,
        requestId: requestId || undefined,
        fromUtc: localToIso(fromLocal),
        toUtc: localToIso(toLocal)
      })
      setResult(next)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setLoading(false)
    }
  }, [page, pageSize, model, surface, credentialId, ownerKey, status, requestId, fromLocal, toLocal, owners])

  const loadSettings = useCallback(async () => {
    try {
      const [nextRetention, nextSummary, nextModels] = await Promise.all([
        api.contentLogSettings(),
        requestAuditApi.summarySettings(),
        api.models()
      ])
      setSettings(nextRetention)
      setRetentionDays(nextRetention.retentionDays)
      setSummarySettings(nextSummary)
      setSummarySystemPrompt(nextSummary.systemPrompt)
      setSummaryDefaultModel(nextSummary.defaultLogicalModel ?? '')
      setSummaryDefaultNodeId(nextSummary.defaultNodeId ?? '')
      setAvailableModels(nextModels)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [])

  useEffect(() => {
    if (providedModels) setAvailableModels(providedModels)
  }, [providedModels])
  useEffect(() => { void loadSettings() }, [loadSettings])
  useEffect(() => {
    const timer = window.setTimeout(() => void load(), 150)
    return () => window.clearTimeout(timer)
  }, [load])
  useEffect(() => {
    if (!live) return
    const timer = window.setInterval(() => void load(), 2000)
    return () => window.clearInterval(timer)
  }, [live, load])

  const pageCount = Math.max(1, Math.ceil(result.total / result.pageSize))
  function resetPage() { setPage(1) }

  async function openLog(id: number) {
    try {
      setSelectedSummary(null)
      setSelected(await api.contentLog(id))
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function openSummary(log: AuditedContentLogSummary) {
    setSummaryLoadingId(log.id)
    try {
      setSelected(null)
      const next = log.hasSummary
        ? await requestAuditApi.summary(log.id)
        : await requestAuditApi.generateSummary(log.id)
      setSelectedSummary(next)
      setError(null)
      if (!log.hasSummary) await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setSummaryLoadingId(null)
    }
  }

  async function regenerateSummary(logicalModel?: string | null, nodeId?: string | null) {
    if (!selectedSummary) return
    setSummaryRegenerating(true)
    try {
      const next = await requestAuditApi.generateSummary(selectedSummary.contentLogId, { logicalModel, nodeId })
      setSelectedSummary(next)
      setMessage('A new administrator summary was generated and saved for this request audit.')
      setError(null)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setSummaryRegenerating(false)
    }
  }

  async function saveRetention() {
    try {
      const next = await api.updateContentLogSettings(retentionDays)
      setSettings(next)
      setRetentionDays(next.retentionDays)
      setMessage('Request-audit retention updated to ' + next.retentionDays + ' days. Automatic cleanup runs every ' + next.cleanupIntervalHours + ' hours.')
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function saveSummarySettings() {
    try {
      const next = await requestAuditApi.updateSummarySettings({
        systemPrompt: summarySystemPrompt,
        defaultLogicalModel: summaryDefaultModel || null,
        defaultNodeId: summaryDefaultNodeId || null
      })
      setSummarySettings(next)
      setSummarySystemPrompt(next.systemPrompt)
      setSummaryDefaultModel(next.defaultLogicalModel ?? '')
      setSummaryDefaultNodeId(next.defaultNodeId ?? '')
      setMessage('Administrator request-summary policy updated.')
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function runCleanup() {
    try {
      const cleanup = await api.runContentLogRetention()
      setMessage('Cleanup completed: ' + cleanup.deletedLogs + ' old request-audit log(s) deleted.')
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  function clearFilters() {
    setModel('')
    setSurface('')
    setCredentialId('')
    setOwnerKey('')
    setStatus('all')
    setRequestId('')
    setFromLocal('')
    setToLocal('')
    setPage(1)
  }

  const selectedCredential = selected?.apiCredentialId ? credentialById.get(selected.apiCredentialId) : undefined
  const selectedUserLabel = selectedCredential?.ownerPrincipalName ?? (selectedCredential ? 'Organization / shared' : 'Internal / diagnostic')

  return <div className="stack compactPage">
    {error && <div className="error">{error}</div>}
    {message && <div className="notice">{message}</div>}

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><div><h2>Request audit retention</h2><span>Application-encrypted request and response bodies</span></div><span>Administrator policy</span></div>
      <div className="retentionBar">
        <label>Days<input aria-label="Request audit retention days" type="number" min={settings?.minimumRetentionDays ?? 10} max={settings?.maximumRetentionDays ?? 4015} value={retentionDays} onChange={event => setRetentionDays(Number(event.target.value))} /></label>
        <button className="primary" onClick={() => void saveRetention()}>Save retention</button>
        <button className="secondary" onClick={() => void runCleanup()}>Run cleanup now</button>
        <span className="muted">Allowed {settings?.minimumRetentionDays ?? 10} days–11 years ({settings?.maximumRetentionDays ?? 4015} days) · automatic cleanup every {settings?.cleanupIntervalHours ?? 4}h</span>
      </div>
    </section>

    <section className="panel formPanel summaryPolicyPanel">
      <div className="panelTitle tuningTitle"><div><h2>AI request summaries</h2><span>Administrator-only prompt, model and routing defaults</span></div><span>{summarySettings?.updatedAtUtc ? 'Updated ' + new Date(summarySettings.updatedAtUtc).toLocaleString() : 'Administrator policy'}</span></div>
      <div className="summaryPolicyGrid">
        <label className="summaryPromptField">System prompt<textarea aria-label="Request summary system prompt" rows={9} value={summarySystemPrompt} onChange={event => setSummarySystemPrompt(event.target.value)} /></label>
        <div className="summaryPolicyOptions">
          <label>Default model<select aria-label="Default request summary model" value={summaryDefaultModel} onChange={event => setSummaryDefaultModel(event.target.value)}><option value="">Automatic: first enabled OpenAI model</option>{openAiModels.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}</select></label>
          <label>Default node<select aria-label="Default request summary node" value={summaryDefaultNodeId} onChange={event => setSummaryDefaultNodeId(event.target.value)}><option value="">Automatic routing</option>{nodes.filter(item => item.enabled).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
          <p className="muted">If a node is selected, the summary model must be deployed there. With automatic routing, LlmProxy selects a healthy eligible node for the chosen logical model.</p>
          <div className="actions">
            <button className="primary" onClick={() => void saveSummarySettings()}>Save summary policy</button>
            <button className="secondary" disabled={!summarySettings} onClick={() => setSummarySystemPrompt(summarySettings?.defaultSystemPrompt ?? '')}>Reset default prompt</button>
          </div>
        </div>
      </div>
    </section>

    <section className="panel">
      <div className="panelTitle"><div><h2>Request / response audit</h2><span>Inspect all retained inference payloads across users, credentials and models.</span></div><label className="inlineToggle"><input type="checkbox" checked={live} onChange={event => setLive(event.target.checked)} /> Live · 2s</label></div>
      <div className="filterBar">
        <label>User<select aria-label="Filter request audit by user" value={ownerKey} onChange={event => { setOwnerKey(event.target.value); setCredentialId(''); resetPage() }}><option value="">All users / organization keys</option>{owners.map(owner => <option key={owner.key} value={owner.key}>{owner.label}</option>)}</select></label>
        <label>Credential<select aria-label="Filter request audit by credential" value={credentialId} onChange={event => { setCredentialId(event.target.value); resetPage() }}><option value="">All credentials</option>{visibleCredentials.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
        <label>Model<input aria-label="Filter request audit by model" value={model} onChange={event => { setModel(event.target.value); resetPage() }} placeholder="agic-code-fast" /></label>
        <label>Surface<select aria-label="Filter request audit by surface" value={surface} onChange={event => { setSurface(event.target.value); resetPage() }}><option value="">All surfaces</option><option value="chat_completions">Chat Completions</option><option value="responses">Responses</option><option value="systemone">System One</option><option value="model_test">Model test</option><option value="systemone_test">System One test</option></select></label>
        <label>Status<select aria-label="Filter request audit by status" value={status} onChange={event => { setStatus(event.target.value as StatusFilter); resetPage() }}><option value="all">All</option><option value="success">Success</option><option value="error">Errors</option></select></label>
        <label>Request ID<input aria-label="Filter request audit by request ID" value={requestId} onChange={event => { setRequestId(event.target.value); resetPage() }} placeholder="UUID" /></label>
        <label>From<input aria-label="Filter request audit from" type="datetime-local" value={fromLocal} onChange={event => { setFromLocal(event.target.value); resetPage() }} /></label>
        <label>To<input aria-label="Filter request audit to" type="datetime-local" value={toLocal} onChange={event => { setToLocal(event.target.value); resetPage() }} /></label>
        <label>Rows<select aria-label="Request audit page size" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}><option value="20">20</option><option value="50">50</option><option value="100">100</option></select></label>
        <button className="secondary" onClick={clearFilters}>Clear filters</button>
        <button className="secondary" onClick={() => void load()}>Refresh</button>
      </div>

      <div className="tableScroll"><table><thead><tr><th>Time</th><th>User</th><th>Credential</th><th>Surface</th><th>Model</th><th>Status</th><th>Request ID</th><th>Action</th></tr></thead><tbody>
        {(result.items as AuditedContentLogSummary[]).map(log => {
          const credential = log.apiCredentialId ? credentialById.get(log.apiCredentialId) : undefined
          const summaryBusy = summaryLoadingId === log.id
          return <tr key={log.id}>
            <td>{new Date(log.startedAtUtc).toLocaleString()}</td>
            <td>{credential?.ownerPrincipalName ?? (credential ? 'Organization / shared' : 'Internal / diagnostic')}</td>
            <td>{log.apiCredentialId ? credentialNames.get(log.apiCredentialId) ?? short(log.apiCredentialId) : '—'}</td>
            <td>{friendlySurface(log.surface)}</td>
            <td><strong>{log.logicalModel ?? '—'}</strong></td>
            <td>{log.statusCode}</td>
            <td className="mono">{short(log.requestId)}</td>
            <td><div className="actions auditRowActions"><button className="secondary" onClick={() => void openLog(log.id)}>Inspect</button><button className="secondary" disabled={summaryBusy} title={log.hasSummary ? 'Open saved administrator summary' : 'Generate and save administrator summary'} onClick={() => void openSummary(log)}>{summaryBusy && <span className="miniSpinner buttonSpinner" aria-hidden="true" />}Summary{log.hasSummary ? ' ✓' : ''}</button></div></td>
          </tr>
        })}
        {!loading && result.items.length === 0 && <tr><td colSpan={8} className="muted">No retained request/response logs match these filters.</td></tr>}
      </tbody></table></div>

      <div className="pagination"><span className="paginationStatus"><span>{result.total === 0 ? '0 requests' : `${(result.page - 1) * result.pageSize + 1}–${Math.min(result.page * result.pageSize, result.total)} of ${result.total}`}</span><span className={loading ? 'miniSpinner' : 'miniSpinner idle'} aria-label={loading ? 'Refreshing request audit' : undefined} /></span><div className="actions"><button disabled={page <= 1 || loading} onClick={() => setPage(current => Math.max(1, current - 1))}>Previous</button><span>Page {page} / {pageCount}</span><button disabled={page >= pageCount || loading} onClick={() => setPage(current => current + 1)}>Next</button></div></div>
    </section>

    <RequestAuditDetailModal
      detail={selected}
      onClose={() => setSelected(null)}
      userLabel={selectedUserLabel}
      credentialLabel={selectedCredential?.name}
      nodeLabel={selected?.nodeId ? nodeNames.get(selected.nodeId) : undefined}
    />

    <RequestAuditSummaryModal
      summary={selectedSummary}
      models={availableModels}
      nodes={nodes}
      busy={summaryRegenerating}
      onClose={() => setSelectedSummary(null)}
      onRegenerate={regenerateSummary}
    />
  </div>
}

function friendlySurface(value: string) { return ({ chat_completions: 'Chat Completions', responses: 'Responses', systemone: 'System One', systemone_test: 'System One test', model_test: 'Model test' } as Record<string,string>)[value] ?? value }
function short(value: string) { return value.length > 18 ? value.slice(0, 14) + '…' : value }
function ownerValue(tenantId: string, objectId: string) { return tenantId + '::' + objectId }
function localToIso(value: string) { if (!value) return undefined; const parsed = new Date(value); return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString() }
