import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { ApiCredential, CreatedApiCredential, Deployment, Model, Node, Overview, RequestMetric } from './types'

type View = 'dashboard' | 'nodes' | 'models' | 'deployments' | 'credentials' | 'metrics'

const emptyOverview: Overview = {
  nodes: { total: 0, healthy: 0, unhealthy: 0, draining: 0 },
  models: 0,
  deployments: 0,
  activeRequests: 0,
  requestsToday: 0
}

export default function App() {
  const [view, setView] = useState<View>('dashboard')
  const [overview, setOverview] = useState<Overview>(emptyOverview)
  const [nodes, setNodes] = useState<Node[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [deployments, setDeployments] = useState<Deployment[]>([])
  const [credentials, setCredentials] = useState<ApiCredential[]>([])
  const [metrics, setMetrics] = useState<RequestMetric[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [authRequired, setAuthRequired] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextOverview, nextNodes, nextModels, nextDeployments, nextCredentials, nextMetrics] = await Promise.all([
        api.overview(), api.nodes(), api.models(), api.deployments(), api.apiCredentials(), api.metrics(100)
      ])
      setOverview(nextOverview)
      setNodes(nextNodes)
      setModels(nextModels)
      setDeployments(nextDeployments)
      setCredentials(nextCredentials)
      setMetrics(nextMetrics)
      setAuthRequired(false)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      if (message === 'AUTH_REQUIRED') setAuthRequired(true)
      else setError(message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
    const timer = window.setInterval(() => void refresh(), 10000)
    return () => window.clearInterval(timer)
  }, [refresh])

  const nodeNames = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const modelNames = useMemo(() => new Map(models.map(model => [model.id, model.publicName])), [models])
  const credentialNames = useMemo(() => new Map(credentials.map(item => [item.id, item.name])), [credentials])

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brandMark">LP</div>
          <div><strong>LlmProxy</strong><span>AI Gateway</span></div>
        </div>
        <nav>
          <NavItem active={view === 'dashboard'} onClick={() => setView('dashboard')}>Dashboard</NavItem>
          <NavItem active={view === 'nodes'} onClick={() => setView('nodes')}>DGX Nodes</NavItem>
          <NavItem active={view === 'models'} onClick={() => setView('models')}>Models</NavItem>
          <NavItem active={view === 'deployments'} onClick={() => setView('deployments')}>Deployments</NavItem>
          <NavItem active={view === 'credentials'} onClick={() => setView('credentials')}>API Credentials</NavItem>
          <NavItem active={view === 'metrics'} onClick={() => setView('metrics')}>Request Metrics</NavItem>
        </nav>
        <div className="sidebarFooter">
          <span className="dot" /> OpenAI-compatible gateway
        </div>
      </aside>

      <main>
        <header>
          <div><h1>{title(view)}</h1><p>On-premises LLM control plane</p></div>
          <button className="secondary" onClick={() => void refresh()}>Refresh</button>
        </header>

        {authRequired && <div className="notice">Authentication is required. <a href="/auth/login">Sign in with Entra ID</a>.</div>}
        {error && <div className="error">{error}</div>}
        {loading ? <div className="loading">Loading gateway state…</div> : (
          <>
            {view === 'dashboard' && <Dashboard overview={overview} nodes={nodes} />}
            {view === 'nodes' && <Nodes nodes={nodes} refresh={refresh} />}
            {view === 'models' && <Models models={models} refresh={refresh} />}
            {view === 'deployments' && <Deployments deployments={deployments} nodes={nodes} models={models} nodeNames={nodeNames} modelNames={modelNames} refresh={refresh} />}
            {view === 'credentials' && <Credentials credentials={credentials} refresh={refresh} />}
            {view === 'metrics' && <Metrics metrics={metrics} nodeNames={nodeNames} credentialNames={credentialNames} />}
          </>
        )}
      </main>
    </div>
  )
}

function Dashboard({ overview, nodes }: { overview: Overview; nodes: Node[] }) {
  return <>
    <section className="cards cardsFive">
      <Metric label="Healthy DGX" value={`${overview.nodes.healthy}/${overview.nodes.total}`} />
      <Metric label="Logical models" value={overview.models} />
      <Metric label="Deployments" value={overview.deployments} />
      <Metric label="Active requests" value={overview.activeRequests} />
      <Metric label="Requests today" value={overview.requestsToday} />
    </section>
    <section className="panel">
      <div className="panelTitle"><h2>Inference fleet</h2><span>Health refreshes every 10 seconds</span></div>
      <table><thead><tr><th>Node</th><th>Status</th><th>Endpoint</th><th>Capacity</th><th>Last check</th></tr></thead>
        <tbody>{nodes.map(node => <tr key={node.id}><td><strong>{node.name}</strong></td><td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td><td>{node.maxConcurrency} concurrent</td><td>{formatDate(node.lastHealthCheckUtc)}</td></tr>)}</tbody>
      </table>
    </section>
  </>
}

function Nodes({ nodes, refresh }: { nodes: Node[]; refresh: () => Promise<void> }) {
  const [name, setName] = useState('')
  const [baseAddress, setBaseAddress] = useState('http://')
  const [maxConcurrency, setMaxConcurrency] = useState(4)

  async function submit(event: FormEvent) {
    event.preventDefault()
    await api.createNode({ name, baseAddress, weight: 1, maxConcurrency })
    setName(''); setBaseAddress('http://'); await refresh()
  }

  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Nodes</h2><span>{nodes.length} registered</span></div>
      <table><thead><tr><th>Name</th><th>Status</th><th>Endpoint</th><th>Capacity</th><th>Action</th></tr></thead><tbody>
        {nodes.map(node => <tr key={node.id}><td><strong>{node.name}</strong></td><td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td><td>{node.maxConcurrency}</td><td className="actions"><button onClick={() => void api.drainNode(node.id).then(refresh)}>Drain</button><button onClick={() => void api.enableNode(node.id).then(refresh)}>Enable</button><button onClick={() => void api.disableNode(node.id).then(refresh)}>Disable</button></td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Add DGX node</h2><form onSubmit={submit}>
      <label>Name<input value={name} onChange={e => setName(e.target.value)} required placeholder="dgx-02" /></label>
      <label>Base address<input value={baseAddress} onChange={e => setBaseAddress(e.target.value)} required placeholder="http://10.0.0.12:8000" /></label>
      <label>Max concurrency<input type="number" min="1" value={maxConcurrency} onChange={e => setMaxConcurrency(Number(e.target.value))} /></label>
      <button className="primary">Add node</button>
    </form></section>
  </div>
}

function Models({ models, refresh }: { models: Model[]; refresh: () => Promise<void> }) {
  const [publicName, setPublicName] = useState('')
  const [providerModelName, setProviderModelName] = useState('')

  async function submit(event: FormEvent) {
    event.preventDefault()
    await api.createModel({ publicName, providerModelName, supportsStreaming: true, supportsTools: true })
    setPublicName(''); setProviderModelName(''); await refresh()
  }

  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Logical models</h2><span>Client-facing aliases</span></div>
      <table><thead><tr><th>Public name</th><th>Provider model</th><th>Capabilities</th></tr></thead><tbody>
        {models.map(model => <tr key={model.id}><td><strong>{model.publicName}</strong></td><td className="mono">{model.providerModelName}</td><td>{model.supportsStreaming ? 'stream ' : ''}{model.supportsTools ? 'tools' : ''}</td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Publish model</h2><form onSubmit={submit}>
      <label>Logical name<input value={publicName} onChange={e => setPublicName(e.target.value)} required placeholder="agic-code-fast" /></label>
      <label>vLLM model name<input value={providerModelName} onChange={e => setProviderModelName(e.target.value)} required placeholder="Qwen/..." /></label>
      <button className="primary">Publish model</button>
    </form></section>
  </div>
}

function Deployments({ deployments, nodes, models, nodeNames, modelNames, refresh }: { deployments: Deployment[]; nodes: Node[]; models: Model[]; nodeNames: Map<string,string>; modelNames: Map<string,string>; refresh: () => Promise<void> }) {
  const [nodeId, setNodeId] = useState('')
  const [modelId, setModelId] = useState('')

  async function submit(event: FormEvent) {
    event.preventDefault()
    await api.createDeployment({ nodeId, modelId, weight: 1 })
    await refresh()
  }

  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Deployments</h2><span>Logical model → DGX</span></div>
      <table><thead><tr><th>Model</th><th>Node</th><th>Weight</th><th>Concurrency</th><th>State</th></tr></thead><tbody>
        {deployments.map(deployment => <tr key={deployment.id}><td><strong>{modelNames.get(deployment.modelId) ?? deployment.modelId}</strong></td><td>{nodeNames.get(deployment.nodeId) ?? deployment.nodeId}</td><td>{deployment.weight}</td><td>{deployment.maxConcurrency ?? 'node default'}</td><td>{deployment.enabled ? 'Enabled' : 'Disabled'}</td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Create deployment</h2><form onSubmit={submit}>
      <label>Model<select value={modelId} onChange={e => setModelId(e.target.value)} required><option value="">Select model</option>{models.map(model => <option key={model.id} value={model.id}>{model.publicName}</option>)}</select></label>
      <label>Node<select value={nodeId} onChange={e => setNodeId(e.target.value)} required><option value="">Select node</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label>
      <button className="primary">Create deployment</button>
    </form></section>
  </div>
}

function Credentials({ credentials, refresh }: { credentials: ApiCredential[]; refresh: () => Promise<void> }) {
  const [name, setName] = useState('GitHub Copilot')
  const [created, setCreated] = useState<CreatedApiCredential | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const result = await api.createApiCredential({ name })
    setCreated(result)
    await refresh()
  }

  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Inference API credentials</h2><span>Raw secrets are never stored</span></div>
      <table><thead><tr><th>Name</th><th>Prefix</th><th>State</th><th>Created</th><th>Last used</th><th>Action</th></tr></thead><tbody>
        {credentials.map(item => <tr key={item.id}><td><strong>{item.name}</strong></td><td className="mono">{item.keyPrefix}…</td><td>{item.enabled ? 'Enabled' : 'Revoked'}</td><td>{formatDate(item.createdAtUtc)}</td><td>{formatDate(item.lastUsedAtUtc)}</td><td className="actions">{item.enabled && <button onClick={() => void api.revokeApiCredential(item.id).then(refresh)}>Revoke</button>}</td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Create credential</h2><form onSubmit={submit}>
      <label>Name<input value={name} onChange={e => setName(e.target.value)} required placeholder="GitHub Copilot Production" /></label>
      <button className="primary">Generate API key</button>
    </form>
      {created && <div className="secretBox"><strong>Copy this key now</strong><p>It will not be shown again.</p><code>{created.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(created.secret)}>Copy</button></div>}
    </section>
  </div>
}

function Metrics({ metrics, nodeNames, credentialNames }: { metrics: RequestMetric[]; nodeNames: Map<string,string>; credentialNames: Map<string,string> }) {
  return <section className="panel"><div className="panelTitle"><h2>Latest inference requests</h2><span>No prompt or generated content is stored</span></div>
    <table><thead><tr><th>Time</th><th>Model</th><th>Node</th><th>Credential</th><th>Status</th><th>Duration</th><th>Error</th></tr></thead><tbody>
      {metrics.map(metric => <tr key={metric.requestId}><td>{formatDate(metric.startedAtUtc)}</td><td><strong>{metric.logicalModel}</strong></td><td>{metric.nodeId ? nodeNames.get(metric.nodeId) ?? short(metric.nodeId) : '—'}</td><td>{metric.apiCredentialId ? credentialNames.get(metric.apiCredentialId) ?? short(metric.apiCredentialId) : '—'}</td><td>{metric.statusCode}</td><td>{metric.durationMilliseconds} ms</td><td className="mono">{metric.errorCode ?? '—'}</td></tr>)}
    </tbody></table>
  </section>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
function NavItem({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) { return <button className={active ? 'active' : ''} onClick={onClick}>{children}</button> }
function title(view: View) { return ({ dashboard: 'Gateway dashboard', nodes: 'DGX nodes', models: 'Logical models', deployments: 'Model deployments', credentials: 'API credentials', metrics: 'Request metrics' } as const)[view] }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function short(value: string) { return `${value.slice(0, 8)}…` }
