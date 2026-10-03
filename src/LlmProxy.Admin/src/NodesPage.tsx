import { FormEvent, useMemo, useState } from 'react'
import { api } from './api'
import { Modal, Tabs } from './UiPrimitives'
import type { Node, NodeConnectionTest } from './types'

export default function NodesPage({ nodes, canWrite, refresh }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void> }) {
  const [tab, setTab] = useState<'active' | 'disabled'>('active')
  const [addOpen, setAddOpen] = useState(false)
  const [credentialNode, setCredentialNode] = useState<Node | null>(null)
  const [name, setName] = useState('')
  const [baseAddress, setBaseAddress] = useState('http://')
  const [weight, setWeight] = useState(1)
  const [maxConcurrency, setMaxConcurrency] = useState(4)
  const [upstreamBearerToken, setUpstreamBearerToken] = useState('')
  const [credentialSecret, setCredentialSecret] = useState('')
  const [connectionTests, setConnectionTests] = useState<Record<string, NodeConnectionTest>>({})
  const [testingNode, setTestingNode] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const active = useMemo(() => nodes.filter(node => node.enabled), [nodes])
  const disabled = useMemo(() => nodes.filter(node => !node.enabled), [nodes])
  const visible = tab === 'active' ? active : disabled

  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy('add'); setError(null)
    try {
      await api.createNode({ name, baseAddress, weight, maxConcurrency, upstreamBearerToken: upstreamBearerToken || null })
      setName(''); setBaseAddress('http://'); setWeight(1); setMaxConcurrency(4); setUpstreamBearerToken(''); setAddOpen(false)
      await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function saveCredential(event: FormEvent) {
    event.preventDefault(); if (!credentialNode || !credentialSecret) return
    setBusy('credential'); setError(null)
    try { await api.setNodeUpstreamCredential(credentialNode.id, credentialSecret); setCredentialSecret(''); setCredentialNode(null); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function clearCredential() {
    if (!credentialNode) return
    setBusy('credential'); setError(null)
    try { await api.clearNodeUpstreamCredential(credentialNode.id); setCredentialSecret(''); setCredentialNode(null); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function testConnection(node: Node) {
    setTestingNode(node.id); setError(null)
    try { const result = await api.testNodeConnection(node.id); setConnectionTests(current => ({ ...current, [node.id]: result })); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setTestingNode(null) }
  }

  async function deleteNode(node: Node) {
    if (!window.confirm(`Delete ${node.name}? The node must remain disabled and have no active work.`)) return
    setBusy(`delete:${node.id}`); setError(null)
    try { await api.deleteNode(node.id); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div><h2>Inference fleet</h2><p className="muted">Active nodes stay front and center. Add, credentials and destructive actions open only when needed.</p></div>
      {canWrite && <button className="primary" onClick={() => setAddOpen(true)}>Add inference node</button>}
    </section>

    {error && <div className="error">{error}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'active', label: 'Active nodes', count: active.length }, { value: 'disabled', label: 'Disabled nodes', count: disabled.length }]} />

    <section className="panel">
      <div className="panelTitle"><h2>{tab === 'active' ? 'Active nodes' : 'Disabled nodes'}</h2><span>{visible.length} shown</span></div>
      <div className="tableScroll"><table><thead><tr><th>Name</th><th>Status</th><th>Service root</th><th>Health</th><th>Capacity</th><th>Actions</th></tr></thead><tbody>
        {visible.map(node => <tr key={node.id}>
          <td><strong>{node.name}</strong><div className="muted">weight {node.weight} · upstream auth {node.hasUpstreamCredential ? 'configured' : 'none'}</div></td>
          <td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td>
          <td><div>{formatLatency(node.lastHealthLatencyMilliseconds)} · {healthStreak(node)}</div><div className="muted">{node.lastHealthError ?? `last healthy ${formatDate(node.lastHealthyAtUtc)}`}</div></td>
          <td>{node.maxConcurrency}</td>
          <td className="actions">
            <button onClick={() => void testConnection(node)}>{testingNode === node.id ? 'Testing…' : 'Test'}</button>
            {canWrite && <button onClick={() => { setCredentialNode(node); setCredentialSecret('') }}>Credentials</button>}
            {canWrite && node.enabled && node.status !== 'Draining' && <button onClick={() => void api.drainNode(node.id).then(refresh)}>Drain</button>}
            {canWrite && node.enabled && node.status !== 'Draining' && <button onClick={() => void api.disableNode(node.id).then(refresh)}>Disable</button>}
            {canWrite && (!node.enabled || node.status === 'Draining') && <button onClick={() => void api.enableNode(node.id).then(refresh)}>Enable</button>}
            {canWrite && !node.enabled && <button className="danger" disabled={busy === `delete:${node.id}`} onClick={() => void deleteNode(node)}>{busy === `delete:${node.id}` ? 'Deleting…' : 'Delete'}</button>}
          </td>
        </tr>)}
        {visible.length === 0 && <tr><td colSpan={6} className="muted">No nodes in this view.</td></tr>}
      </tbody></table></div>
      {Object.values(connectionTests).map(result => <div className="secretBox" key={result.nodeId}>
        <strong>{result.success ? '✓' : '✕'} Connection test: {result.nodeName}</strong><p className="mono">Root: {result.serviceRoot}</p><p>Health: {probeSummary(result.health)}</p><p>OpenAI models: {probeSummary(result.openAi)}</p><p className="mono">Chat: {result.chatCompletionsUrl}</p><p className="mono">Responses: {result.responsesUrl}</p>
      </div>)}
    </section>

    <Modal open={addOpen} title="Add inference node" description="Register the inference service root and optional provider bearer. The bearer remains write-only." onClose={() => setAddOpen(false)}>
      <form className="formPanel" onSubmit={submit}>
        <label>Name<input value={name} onChange={event => setName(event.target.value)} required placeholder="inference-02" /></label>
        <label>Base address / service root<input value={baseAddress} onChange={event => setBaseAddress(event.target.value)} required placeholder="http://10.0.0.12:8000/vllm" /></label>
        <div className="formGridTwo"><label>Weight<input type="number" min="1" value={weight} onChange={event => setWeight(Number(event.target.value))} /></label><label>Max concurrency<input type="number" min="1" value={maxConcurrency} onChange={event => setMaxConcurrency(Number(event.target.value))} /></label></div>
        <label>Upstream bearer token (optional)<input type="password" autoComplete="new-password" value={upstreamBearerToken} onChange={event => setUpstreamBearerToken(event.target.value)} placeholder="provider token" /></label>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setAddOpen(false)}>Cancel</button><button className="primary" disabled={busy === 'add'}>{busy === 'add' ? 'Adding…' : 'Add node'}</button></div>
      </form>
    </Modal>

    <Modal open={Boolean(credentialNode)} title={`Upstream authentication · ${credentialNode?.name ?? ''}`} description="Provider credentials are encrypted at rest and are never returned by the API." onClose={() => setCredentialNode(null)}>
      <form className="formPanel" onSubmit={saveCredential}>
        <label>New bearer token<input type="password" autoComplete="new-password" value={credentialSecret} onChange={event => setCredentialSecret(event.target.value)} placeholder="write-only secret" /></label>
        <div className="modalActions"><button type="button" className="danger" disabled={!credentialNode?.hasUpstreamCredential || busy === 'credential'} onClick={() => void clearCredential()}>Clear bearer</button><button className="primary" disabled={!credentialSecret || busy === 'credential'}>Set / rotate bearer</button></div>
      </form>
    </Modal>
  </div>
}

function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function formatLatency(value?: number | null) { return value === null || value === undefined ? '—' : `${value} ms` }
function healthStreak(node: Node) { return node.consecutiveHealthFailures > 0 ? `${node.consecutiveHealthFailures} fail` : `${node.consecutiveHealthSuccesses} ok` }
function probeSummary(probe: NodeConnectionTest['health']) { return probe.success ? `✓ HTTP ${probe.statusCode} in ${probe.latencyMilliseconds} ms` : `✕ ${probe.error ?? `HTTP ${probe.statusCode}`} (${probe.latencyMilliseconds} ms)` }
