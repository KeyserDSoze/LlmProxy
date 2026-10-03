import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { ApiCredential, ContentLogDetail, ContentLogSettings, ContentLogSummary, Node } from './types'

export default function ContentLogs({ credentials, nodes }: { credentials: ApiCredential[]; nodes: Node[] }) {
  const [logs, setLogs] = useState<ContentLogSummary[]>([])
  const [settings, setSettings] = useState<ContentLogSettings | null>(null)
  const [retentionDays, setRetentionDays] = useState(30)
  const [selected, setSelected] = useState<ContentLogDetail | null>(null)
  const [live, setLive] = useState(true)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const credentialNames = useMemo(() => new Map(credentials.map(item => [item.id, item.name])), [credentials])
  const nodeNames = useMemo(() => new Map(nodes.map(item => [item.id, item.name])), [nodes])

  const refresh = useCallback(async () => {
    try {
      const [nextLogs, nextSettings] = await Promise.all([api.contentLogs(100), api.contentLogSettings()])
      setLogs(nextLogs)
      setSettings(nextSettings)
      setRetentionDays(nextSettings.retentionDays)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])
  useEffect(() => {
    if (!live) return
    const timer = window.setInterval(() => void api.contentLogs(100).then(setLogs).catch(() => undefined), 2000)
    return () => window.clearInterval(timer)
  }, [live])

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
      setMessage('Retention updated to ' + next.retentionDays + ' days. Automatic cleanup runs every ' + next.cleanupIntervalHours + ' hours.')
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function runCleanup() {
    try {
      const result = await api.runContentLogRetention()
      setMessage('Cleanup completed: ' + result.deletedLogs + ' old log(s) deleted.')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  return <div className="stack">
    {error && <div className="error">{error}</div>}
    {message && <div className="notice">{message}</div>}
    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Log retention</h2><span>Encrypted payloads · admin only</span></div>
      <div className="retentionBar">
        <label>Days<input aria-label="Content log retention days" type="number" min={settings?.minimumRetentionDays ?? 10} max={settings?.maximumRetentionDays ?? 180} value={retentionDays} onChange={event => setRetentionDays(Number(event.target.value))} /></label>
        <button className="primary" onClick={() => void saveRetention()}>Save retention</button>
        <button className="secondary" onClick={() => void runCleanup()}>Run cleanup now</button>
        <span className="muted">Allowed {settings?.minimumRetentionDays ?? 10}–{settings?.maximumRetentionDays ?? 180} days · automatic every {settings?.cleanupIntervalHours ?? 4}h</span>
      </div>
    </section>

    <section className="panel">
      <div className="panelTitle"><h2>Live request / response log</h2><span><label className="inlineToggle"><input type="checkbox" checked={live} onChange={event => setLive(event.target.checked)} /> Live · 2s</label></span></div>
      <table><thead><tr><th>Time</th><th>Surface</th><th>Model</th><th>Credential</th><th>Status</th><th>Request ID</th><th>Action</th></tr></thead><tbody>
        {logs.map(log => <tr key={log.id}>
          <td>{new Date(log.startedAtUtc).toLocaleString()}</td>
          <td>{friendlySurface(log.surface)}</td>
          <td><strong>{log.logicalModel ?? '—'}</strong></td>
          <td>{log.apiCredentialId ? credentialNames.get(log.apiCredentialId) ?? short(log.apiCredentialId) : '—'}</td>
          <td>{log.statusCode}</td>
          <td className="mono">{short(log.requestId)}</td>
          <td><button className="secondary" onClick={() => void openLog(log.id)}>Inspect</button></td>
        </tr>)}
        {logs.length === 0 && <tr><td colSpan={7} className="muted">No full-body inference logs yet.</td></tr>}
      </tbody></table>
    </section>

    {selected && <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Request detail</h2><span>{friendlySurface(selected.surface)} · HTTP {selected.statusCode}</span></div>
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
