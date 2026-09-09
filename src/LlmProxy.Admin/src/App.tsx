import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { Deployment, Model, Node, Overview } from './types'

type View = 'dashboard' | 'nodes' | 'models' | 'deployments'

const emptyOverview: Overview = {
  nodes: { total: 0, healthy: 0, unhealthy: 0, draining: 0 },
  models: 0,
  deployments: 0,
  activeRequests: 0
}

export default function App() {
  const [view, setView] = useState<View>('dashboard')
  const [overview, setOverview] = useState<Overview>(emptyOverview)
  const [nodes, setNodes] = useState<Node[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [deployments, setDeployments] = useState<Deployment[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [authRequired, setAuthRequired] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextOverview, nextNodes, nextModels, nextDeployments] = await Promise.all([
        api.overview(), api.nodes(), api.models(), api.deployments()
      ])
      setOverview(nextOverview)
      setNodes(nextNodes)
      setModels(nextModels)
      setDeployments(nextDeployments)
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
          </>
        )}
      </main>
    </div>
  )
}

function Dashboard({ overview, nodes }: { overview: Overview; nodes: Node[] }) {
  return <>
    <section className="cards">
      <Metric label="Healthy DGX" value={`${overview.nodes.healthy}/${overview.nodes.total}`} />
      <Metric label="Logical models" value={overview.models} />
      <Metric label="Deployments" value={overview.deployments} />
      <Metric label="Active requests" value={overview.activeRequests} />
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
      <table><thead><tr><th>Name</th><th>Status</th><th>Endpoint</th><th>Action</th></tr></thead><tbody>
        {nodes.map(node => <tr key={node.id}><td><strong>{node.name}</strong></td><td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td><td className="actions"><button onClick={() => void api.drainNode(node.id).then(refresh)}>Drain</button><button onClick={() => void api.enableNode(node.id).then(refresh)}>Enable</button></td></tr>)}
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
      <table><thead><tr><th>Model</th><th>Node</th><th>Weight</th><th>Concurrency override</th></tr></thead><tbody>
        {deployments.map(deployment => <tr key={deployment.id}><td><strong>{modelNames.get(deployment.modelId) ?? deployment.modelId}</strong></td><td>{nodeNames.get(deployment.nodeId) ?? deployment.nodeId}</td><td>{deployment.weight}</td><td>{deployment.maxConcurrency ?? 'node default'}</td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Create deployment</h2><form onSubmit={submit}>
      <label>Model<select value={modelId} onChange={e => setModelId(e.target.value)} required><option value="">Select model</option>{models.map(model => <option key={model.id} value={model.id}>{model.publicName}</option>)}</select></label>
      <label>Node<select value={nodeId} onChange={e => setNodeId(e.target.value)} required><option value="">Select node</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label>
      <button className="primary">Create deployment</button>
    </form></section>
  </div>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
function NavItem({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) { return <button className={active ? 'active' : ''} onClick={onClick}>{children}</button> }
function title(view: View) { return ({ dashboard: 'Gateway dashboard', nodes: 'DGX nodes', models: 'Logical models', deployments: 'Model deployments' } as const)[view] }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
