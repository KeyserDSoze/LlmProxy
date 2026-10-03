import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { ApiCredential, MetricsSummary, Node, RequestMetric } from './types'

type PageResult = { items: RequestMetric[]; total: number; page: number; pageSize: number }
type StatusFilter = 'all' | 'success' | 'error'

export default function MetricsPage({ summary, nodes, credentials }: { summary: MetricsSummary; nodes: Node[]; credentials: ApiCredential[] }) {
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [model, setModel] = useState('')
  const [nodeId, setNodeId] = useState('')
  const [credentialId, setCredentialId] = useState('')
  const [status, setStatus] = useState<StatusFilter>('all')
  const [result, setResult] = useState<PageResult>({ items: [], total: 0, page: 1, pageSize: 20 })
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const nodeNames = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const credentialNames = useMemo(() => new Map(credentials.map(item => [item.id, item.name])), [credentials])

  const load = useCallback(async () => {
    setLoading(true); setError(null)
    try { setResult(await api.metricsQuery({ page, pageSize, model: model || undefined, nodeId: nodeId || undefined, apiCredentialId: credentialId || undefined, status })) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setLoading(false) }
  }, [page, pageSize, model, nodeId, credentialId, status])

  useEffect(() => { const timer = window.setTimeout(() => void load(), 150); return () => window.clearTimeout(timer) }, [load])
  const pageCount = Math.max(1, Math.ceil(result.total / result.pageSize))
  function resetPage() { setPage(1) }

  return <div className="stack compactPage">
    <section className="panel"><div className="panelTitle"><div><h2>Inference observability</h2><span>Latency, token accounting, failover and request-level diagnostics.</span></div><span>Window: {summary.windowHours}h</span></div><div className="cards cardsFive"><Metric label="Requests" value={summary.requestCount} /><Metric label="Success" value={`${summary.successRatePercent.toFixed(1)}%`} /><Metric label="P95 total" value={formatMs(summary.p95DurationMilliseconds)} /><Metric label="P95 TTFT" value={formatMs(summary.p95TimeToFirstByteMilliseconds)} /><Metric label="Output tokens" value={formatNumber(summary.outputTokens)} /></div><div className="cards cardsFive"><Metric label="P50 total" value={formatMs(summary.p50DurationMilliseconds)} /><Metric label="P50 TTFT" value={formatMs(summary.p50TimeToFirstByteMilliseconds)} /><Metric label="Avg upstream headers" value={formatMs(summary.averageUpstreamHeaderMilliseconds)} /><Metric label="Failover requests" value={summary.failoverRequests} /><Metric label="Streaming" value={summary.streamingRequests} /></div></section>

    <div className="gridTwo"><section className="panel"><div className="panelTitle"><h2>By logical model</h2><span>Gateway alias</span></div><table><thead><tr><th>Model</th><th>Requests</th><th>Errors</th><th>Avg total</th><th>Avg TTFT</th><th>Output tokens</th></tr></thead><tbody>{summary.byModel.map(item => <tr key={item.logicalModel}><td><strong>{item.logicalModel}</strong></td><td>{item.requestCount}</td><td>{item.errorCount}</td><td>{formatMs(item.averageDurationMilliseconds)}</td><td>{formatMs(item.averageTimeToFirstByteMilliseconds)}</td><td>{formatNumber(item.outputTokens)}</td></tr>)}</tbody></table></section><section className="panel"><div className="panelTitle"><h2>By inference node</h2><span>Physical execution target</span></div><table><thead><tr><th>Node</th><th>Requests</th><th>Errors</th><th>Avg total</th><th>P95 total</th><th>Output tokens</th></tr></thead><tbody>{summary.byNode.map(item => <tr key={item.nodeId}><td><strong>{nodeNames.get(item.nodeId) ?? item.nodeId}</strong></td><td>{item.requestCount}</td><td>{item.errorCount}</td><td>{formatMs(item.averageDurationMilliseconds)}</td><td>{formatMs(item.p95DurationMilliseconds)}</td><td>{formatNumber(item.outputTokens)}</td></tr>)}</tbody></table></section></div>

    <section className="panel">
      <div className="panelTitle"><div><h2>Requests</h2><span>Starts with the newest 20; filter and page through the retained request metrics.</span></div><button className="secondary" onClick={() => void load()}>Refresh</button></div>
      <div className="filterBar">
        <label>Model<input aria-label="Filter requests by model" value={model} onChange={event => { setModel(event.target.value); resetPage() }} placeholder="agic-code-fast" /></label>
        <label>Node<select aria-label="Filter requests by node" value={nodeId} onChange={event => { setNodeId(event.target.value); resetPage() }}><option value="">All nodes</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label>
        <label>Credential<select aria-label="Filter requests by credential" value={credentialId} onChange={event => { setCredentialId(event.target.value); resetPage() }}><option value="">All credentials</option>{credentials.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
        <label>Status<select aria-label="Filter requests by status" value={status} onChange={event => { setStatus(event.target.value as StatusFilter); resetPage() }}><option value="all">All</option><option value="success">Success</option><option value="error">Errors</option></select></label>
        <label>Rows<select aria-label="Request page size" value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1) }}><option value="20">20</option><option value="50">50</option><option value="100">100</option></select></label>
      </div>
      {error && <div className="error">{error}</div>}
      <div className="tableScroll"><table><thead><tr><th>Started</th><th>Model</th><th>Surface</th><th>Node</th><th>Credential</th><th>Status</th><th>Total</th><th>TTFT</th><th>Attempts</th><th>Tokens</th></tr></thead><tbody>
        {result.items.map(item => <tr key={item.id}><td>{formatDate(item.startedAtUtc)}</td><td><strong>{item.logicalModel}</strong></td><td>{surfaceLabel(item)}</td><td>{item.nodeId ? nodeNames.get(item.nodeId) ?? item.nodeId : '—'}</td><td>{item.apiCredentialId ? credentialNames.get(item.apiCredentialId) ?? item.apiCredentialId : '—'}</td><td>{item.statusCode}{item.errorCode ? <div className="muted">{item.errorCode}</div> : null}</td><td>{item.durationMilliseconds} ms</td><td>{formatMs(item.timeToFirstByteMilliseconds)}</td><td>{item.attemptCount}{item.attemptCount > 1 ? ' · failover' : ''}</td><td>{item.totalTokens ?? '—'}</td></tr>)}
        {!loading && result.items.length === 0 && <tr><td colSpan={10} className="muted">No requests match these filters.</td></tr>}
        {loading && <tr><td colSpan={10} className="muted">Loading requests…</td></tr>}
      </tbody></table></div>
      <div className="pagination"><span>{result.total === 0 ? '0 requests' : `${(result.page - 1) * result.pageSize + 1}–${Math.min(result.page * result.pageSize, result.total)} of ${result.total}`}</span><div className="actions"><button disabled={page <= 1 || loading} onClick={() => setPage(current => Math.max(1, current - 1))}>Previous</button><span>Page {page} / {pageCount}</span><button disabled={page >= pageCount || loading} onClick={() => setPage(current => current + 1)}>Next</button></div></div>
    </section>
  </div>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function formatNumber(value: number) { return new Intl.NumberFormat().format(value) }
function formatMs(value?: number | null) { return value === null || value === undefined ? '—' : `${Math.round(value)} ms` }
function formatDate(value: string) { return new Date(value).toLocaleString() }
function surfaceLabel(item: RequestMetric) { return `${item.surface.replaceAll('_', ' ')}${item.isStreaming ? ' · SSE' : ''}`.replace(/\b\w/g, c => c.toUpperCase()).replace('Sse', 'SSE') }
