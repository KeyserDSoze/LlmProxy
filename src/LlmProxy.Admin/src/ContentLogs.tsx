import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { ApiCredential, ContentLogDetail, ContentLogPage, ContentLogSettings, Node } from './types'

type StatusFilter = 'all' | 'success' | 'error'

export default function ContentLogs({ credentials, nodes }: { credentials: ApiCredential[]; nodes: Node[] }) {
  const [result, setResult] = useState<ContentLogPage>({ items: [], total: 0, page: 1, pageSize: 50 })
  const [settings, setSettings] = useState<ContentLogSettings | null>(null)
  const [retentionDays, setRetentionDays] = useState(30)
  const [selected, setSelected] = useState<ContentLogDetail | null>(null)
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
      const next = await api.contentLogSettings()
      setSettings(next)
      setRetentionDays(next.retentionDays)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [])

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
      setSelected(await api.contentLog(id))
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
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
        {result.items.map(log => {
          const credential = log.apiCredentialId ? credentialById.get(log.apiCredentialId) : undefined
          return <tr key={log.id}>
            <td>{new Date(log.startedAtUtc).toLocaleString()}</td>
            <td>{credential?.ownerPrincipalName ?? (credential ? 'Organization / shared' : 'Internal / diagnostic')}</td>
            <td>{log.apiCredentialId ? credentialNames.get(log.apiCredentialId) ?? short(log.apiCredentialId) : '—'}</td>
            <td>{friendlySurface(log.surface)}</td>
            <td><strong>{log.logicalModel ?? '—'}</strong></td>
            <td>{log.statusCode}</td>
            <td className="mono">{short(log.requestId)}</td>
            <td><button className="secondary" onClick={() => void openLog(log.id)}>Inspect</button></td>
          </tr>
        })}
        {!loading && result.items.length === 0 && <tr><td colSpan={8} className="muted">No retained request/response logs match these filters.</td></tr>}
        {loading && <tr><td colSpan={8} className="muted">Loading request audit…</td></tr>}
      </tbody></table></div>

      <div className="pagination"><span>{result.total === 0 ? '0 requests' : `${(result.page - 1) * result.pageSize + 1}–${Math.min(result.page * result.pageSize, result.total)} of ${result.total}`}</span><div className="actions"><button disabled={page <= 1 || loading} onClick={() => setPage(current => Math.max(1, current - 1))}>Previous</button><span>Page {page} / {pageCount}</span><button disabled={page >= pageCount || loading} onClick={() => setPage(current => current + 1)}>Next</button></div></div>
    </section>

    {selected && <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><div><h2>Request detail</h2><span>{friendlySurface(selected.surface)} · HTTP {selected.statusCode}</span></div><button className="secondary" onClick={() => setSelected(null)}>Close</button></div>
      <div className="statusGrid">
        <div><span>Request ID</span><strong className="mono">{selected.requestId}</strong></div>
        <div><span>Model</span><strong>{selected.logicalModel ?? '—'}</strong></div>
        <div><span>Node</span><strong>{selected.nodeId ? nodeNames.get(selected.nodeId) ?? short(selected.nodeId) : '—'}</strong></div>
        <div><span>Attempts</span><strong>{selected.attemptCount ?? '—'}</strong></div>
        <div><span>TTFT</span><strong>{selected.timeToFirstByteMilliseconds == null ? '—' : String(selected.timeToFirstByteMilliseconds) + ' ms'}</strong></div>
        <div><span>Error</span><strong className="mono">{selected.errorCode ?? '—'}</strong></div>
      </div>
      <PayloadBlock title="Request body" body={selected.requestBody} />
      <PayloadBlock title="Response body" body={selected.responseBody} />
    </section>}
  </div>
}

function PayloadBlock({ title, body }: { title: string; body: string }) {
  return <div className="payloadBlock">
    <div className="payloadHeader"><h3>{title}</h3><button className="secondary" onClick={() => void navigator.clipboard.writeText(body)}>Copy</button></div>
    <pre className="payload">{pretty(body)}</pre>
  </div>
}

function pretty(value: string) { try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value } }
function friendlySurface(value: string) { return ({ chat_completions: 'Chat Completions', responses: 'Responses', systemone: 'System One', systemone_test: 'System One test', model_test: 'Model test' } as Record<string,string>)[value] ?? value }
function short(value: string) { return value.length > 18 ? value.slice(0, 14) + '…' : value }
function ownerValue(tenantId: string, objectId: string) { return tenantId + '::' + objectId }
function localToIso(value: string) { if (!value) return undefined; const parsed = new Date(value); return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString() }
