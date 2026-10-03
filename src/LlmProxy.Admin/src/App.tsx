import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import GovernanceExperience from './GovernanceExperience'
import HardwareExperience from './HardwareExperience'
import ModelHardwareExperience from './ModelHardwareExperience'
import NodesPage from './NodesPage'
import ModelsDeployments from './ModelsDeployments'
import CredentialsPage from './CredentialsPage'
import MetricsPage from './MetricsPage'
import ReleaseNotesPage from './ReleaseNotesPage'
import ContentLogs from './ContentLogs'
import HelpPage from './HelpPage'
import PageDocumentation from './PageDocumentation'
import PlaygroundExperience from './PlaygroundExperience'
import UsersAccessExperience from './UsersAccessExperience'
import type { ApiCredential, AuditEvent, CreatedApiCredential, Deployment, DeploymentPerformanceSnapshot, MetricsSummary, Model, Node, NodeConnectionTest, NodeHardwareMetricsSnapshot, NodeRuntimeMetricsSnapshot, Overview, RequestMetric, RoutingSettings, RoutingTuningSettings } from './types'

export type View = 'dashboard' | 'nodes' | 'hardware' | 'model-management' | 'models' | 'deployments' | 'routing' | 'credentials' | 'users' | 'metrics' | 'playground' | 'logs' | 'audit' | 'help' | 'governance' | 'releases'

const emptyOverview: Overview = {
  nodes: { total: 0, healthy: 0, degraded: 0, unhealthy: 0, draining: 0 },
  models: 0,
  deployments: 0,
  activeRequests: 0,
  requestsToday: 0
}

const emptyRouting: RoutingSettings = {
  strategy: 'WeightedLeastLoaded',
  supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin']
}

const emptyRoutingTuning: RoutingTuningSettings = {
  warmupSamples: 3,
  ttftTargetMilliseconds: 2000,
  ttftPenaltyWeight: 0.25,
  failurePenaltyWeight: 1.5,
  externalLoadPenaltyWeight: 0.4,
  queuePenaltyWeight: 0.75,
  kvCacheThreshold: 0.7,
  kvCachePenaltyWeight: 0.6,
  degradedNodePenalty: 0.35,
  unknownNodePenalty: 0.1
}

const emptyMetricsSummary: MetricsSummary = {
  windowHours: 24,
  sinceUtc: '',
  requestCount: 0,
  successCount: 0,
  errorCount: 0,
  successRatePercent: 0,
  p50DurationMilliseconds: null,
  p95DurationMilliseconds: null,
  p50TimeToFirstByteMilliseconds: null,
  p95TimeToFirstByteMilliseconds: null,
  averageUpstreamHeaderMilliseconds: null,
  inputTokens: 0,
  outputTokens: 0,
  totalTokens: 0,
  tokenObservedRequests: 0,
  failoverRequests: 0,
  streamingRequests: 0,
  byModel: [],
  byNode: []
}

export default function App({ initialView = 'dashboard' }: { initialView?: View }) {
  const [view, setView] = useState<View>(initialView)
  const [overview, setOverview] = useState<Overview>(emptyOverview)
  const [routing, setRouting] = useState<RoutingSettings>(emptyRouting)
  const [routingTuning, setRoutingTuning] = useState<RoutingTuningSettings>(emptyRoutingTuning)
  const [routingPerformance, setRoutingPerformance] = useState<DeploymentPerformanceSnapshot[]>([])
  const [routingRuntime, setRoutingRuntime] = useState<NodeRuntimeMetricsSnapshot[]>([])
  const [hardware, setHardware] = useState<NodeHardwareMetricsSnapshot[]>([])
  const [metricsSummary, setMetricsSummary] = useState<MetricsSummary>(emptyMetricsSummary)
  const [nodes, setNodes] = useState<Node[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [deployments, setDeployments] = useState<Deployment[]>([])
  const [credentials, setCredentials] = useState<ApiCredential[]>([])
  const [metrics, setMetrics] = useState<RequestMetric[]>([])
  const [audit, setAudit] = useState<AuditEvent[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [authRequired, setAuthRequired] = useState(false)
  const [canWrite, setCanWrite] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextSession, nextOverview, nextRouting, nextTuning, nextPerformance, nextRuntime, nextHardware, nextNodes, nextModels, nextDeployments, nextCredentials, nextMetrics, nextMetricsSummary, nextAudit] = await Promise.all([
        api.adminSession(), api.overview(), api.routing(), api.routingTuning(), api.routingPerformance(), api.routingRuntime(), api.hardware(), api.nodes(), api.models(), api.deployments(), api.apiCredentials(), api.metrics(100), api.metricsSummary(24), api.audit(100)
      ])
      setCanWrite(nextSession.canWrite)
      setOverview(nextOverview)
      setRouting(nextRouting)
      setRoutingTuning(nextTuning)
      setRoutingPerformance(nextPerformance)
      setRoutingRuntime(nextRuntime)
      setHardware(nextHardware)
      setNodes(nextNodes)
      setModels(nextModels)
      setDeployments(nextDeployments)
      setCredentials(nextCredentials)
      setMetrics(nextMetrics)
      setMetricsSummary(nextMetricsSummary)
      setAudit(nextAudit)
      setAuthRequired(false)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      if (message === 'AUTH_REQUIRED') setAuthRequired(true)
      else if (message === 'FORBIDDEN') setError('Access denied. Your Entra account is signed in but does not have an LlmProxy.Admin or LlmProxy.Reader role.')
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
          <div className="brandMark"><img src="/admin/favicon.svg" alt="" /></div>
          <div><strong>LlmProxy</strong><span>AI Gateway</span></div>
        </div>
        <nav>
          <NavItem icon="dashboard" active={view === 'dashboard'} onClick={() => setView('dashboard')}>Dashboard</NavItem>
          <NavItem icon="nodes" active={view === 'nodes'} onClick={() => setView('nodes')}>Inference Nodes</NavItem>
          <NavItem icon="hardware" active={view === 'hardware'} onClick={() => setView('hardware')}>Hardware</NavItem>
          {canWrite && <NavItem icon="control" active={view === 'model-management'} onClick={() => setView('model-management')}>Model & Hardware</NavItem>}
          <NavItem icon="models" active={view === 'models' || view === 'deployments'} onClick={() => setView('models')}>Models & Deployments</NavItem>
          <NavItem icon="routing" active={view === 'routing'} onClick={() => setView('routing')}>Routing</NavItem>
          <NavItem icon="key" active={view === 'credentials'} onClick={() => setView('credentials')}>API Credentials</NavItem>
          <NavItem icon="metrics" active={view === 'metrics'} onClick={() => setView('metrics')}>Request Metrics</NavItem>
          {canWrite && <NavItem icon="play" active={view === 'playground'} onClick={() => setView('playground')}>Playground</NavItem>}
          {canWrite && <NavItem icon="logs" active={view === 'logs'} onClick={() => setView('logs')}>Request Audit</NavItem>}
          <NavItem icon="audit" active={view === 'audit'} onClick={() => setView('audit')}>Administrative Audit</NavItem>
          <NavItem icon="help" active={view === 'help'} onClick={() => setView('help')}>Help & Endpoints</NavItem>
          <div className="navSecondary">
            <NavItem icon="governance" active={view === 'governance'} onClick={() => setView('governance')}>Usage & Governance</NavItem>
            {canWrite && <NavItem icon="users" active={view === 'users'} onClick={() => setView('users')}>Users & Access</NavItem>}
            <ProductNavItem active={view === 'releases'} onClick={() => setView('releases')} />
          </div>
        </nav>
        <div className="sidebarFooter"><span className="dot" /> OpenAI-compatible gateway</div>
      </aside>

      <main>
        <header>
          <div><h1>{title(view)}</h1><p>On-premises LLM control plane</p></div>
          <button className="secondary" onClick={() => void refresh()}>Refresh</button>
        </header>

        <PageDocumentation page={view} />

        {authRequired && <div className="notice">Authentication is required. <a href="/auth/login">Sign in with Entra ID</a>. Normal users can open <a href="/admin/me">My API Keys</a>.</div>}
        {error && <div className="error">{error}</div>}
        {loading ? <div className="loading">Loading gateway state…</div> : (
          <>
            {view === 'dashboard' && <Dashboard overview={overview} nodes={nodes} routing={routing} metricsSummary={metricsSummary} />}
            {view === 'nodes' && <NodesPage nodes={nodes} canWrite={canWrite} refresh={refresh} />}
            {view === 'hardware' && <HardwareExperience nodes={nodes} hardware={hardware} refresh={refresh} />}
            {view === 'model-management' && canWrite && <ModelHardwareExperience nodes={nodes} canWrite={canWrite} refresh={refresh} />}
            {(view === 'models' || view === 'deployments') && <ModelsDeployments models={models} deployments={deployments} nodes={nodes} canWrite={canWrite} refresh={refresh} />}
            {view === 'routing' && <Routing routing={routing} tuning={routingTuning} performance={routingPerformance} runtime={routingRuntime} deployments={deployments} nodes={nodes} models={models} refresh={refresh} />}
            {view === 'credentials' && <CredentialsPage credentials={credentials} canWrite={canWrite} refresh={refresh} />}
            {view === 'users' && canWrite && <UsersAccessExperience />}
            {view === 'metrics' && <MetricsPage summary={metricsSummary} nodes={nodes} credentials={credentials} />}
            {view === 'playground' && canWrite && <PlaygroundExperience models={models} />}
            {view === 'logs' && canWrite && <ContentLogs credentials={credentials} nodes={nodes} />}
            {view === 'audit' && <Audit events={audit} />}
            {view === 'help' && <HelpPage models={models} />}
            {view === 'governance' && <GovernanceExperience />}
            {view === 'releases' && <ReleaseNotesPage embedded canWrite={canWrite} />}
          </>
        )}
      </main>
    </div>
  )
}

function Dashboard({ overview, nodes, routing, metricsSummary }: { overview: Overview; nodes: Node[]; routing: RoutingSettings; metricsSummary: MetricsSummary }) {
  return <>
    <section className="cards cardsFive">
      <Metric label="Fleet health" value={`${overview.nodes.healthy} H / ${overview.nodes.degraded} D`} />
      <Metric label="Logical models" value={overview.models} />
      <Metric label="Deployments" value={overview.deployments} />
      <Metric label="Active requests" value={overview.activeRequests} />
      <Metric label="Requests today" value={overview.requestsToday} />
    </section>
    <section className="cards cardsFive">
      <Metric label={`${metricsSummary.windowHours}h requests`} value={formatNumber(metricsSummary.requestCount)} />
      <Metric label="Success rate" value={formatPercent(metricsSummary.successRatePercent)} />
      <Metric label="P50 TTFT" value={formatMetricLatency(metricsSummary.p50TimeToFirstByteMilliseconds)} />
      <Metric label="P95 TTFT" value={formatMetricLatency(metricsSummary.p95TimeToFirstByteMilliseconds)} />
      <Metric label="Output tokens" value={formatNumber(metricsSummary.outputTokens)} />
    </section>
    <section className="panel">
      <div className="panelTitle"><h2>Inference fleet</h2><span>Routing: {friendlyStrategy(routing.strategy)}</span></div>
      <table><thead><tr><th>Node</th><th>Status</th><th>Service root</th><th>Latency</th><th>Health streak</th><th>Last check</th></tr></thead>
        <tbody>{nodes.map(node => <tr key={node.id}>
          <td><strong>{node.name}</strong></td><td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td>
          <td>{formatLatency(node.lastHealthLatencyMilliseconds)}</td><td>{healthStreak(node)}</td><td>{formatDate(node.lastHealthCheckUtc)}</td>
        </tr>)}</tbody>
      </table>
    </section>
  </>
}

function Nodes({ nodes, refresh }: { nodes: Node[]; refresh: () => Promise<void> }) {
  const [name, setName] = useState('')
  const [baseAddress, setBaseAddress] = useState('http://')
  const [weight, setWeight] = useState(1)
  const [maxConcurrency, setMaxConcurrency] = useState(4)
  const [upstreamBearerToken, setUpstreamBearerToken] = useState('')
  const [credentialNodeId, setCredentialNodeId] = useState('')
  const [credentialSecret, setCredentialSecret] = useState('')
  const [connectionTests, setConnectionTests] = useState<Record<string, NodeConnectionTest>>({})
  const [testingNode, setTestingNode] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    await api.createNode({ name, baseAddress, weight, maxConcurrency, upstreamBearerToken: upstreamBearerToken || null })
    setName(''); setBaseAddress('http://'); setWeight(1); setUpstreamBearerToken(''); await refresh()
  }

  async function setCredential(event: FormEvent) {
    event.preventDefault()
    if (!credentialNodeId || !credentialSecret) return
    await api.setNodeUpstreamCredential(credentialNodeId, credentialSecret)
    setCredentialSecret('')
    await refresh()
  }

  async function clearCredential() {
    if (!credentialNodeId) return
    await api.clearNodeUpstreamCredential(credentialNodeId)
    setCredentialSecret('')
    await refresh()
  }

  async function testConnection(node: Node) {
    setTestingNode(node.id)
    try {
      const result = await api.testNodeConnection(node.id)
      setConnectionTests(current => ({ ...current, [node.id]: result }))
      await refresh()
    } finally {
      setTestingNode(null)
    }
  }

  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Nodes</h2><span>{nodes.length} registered</span></div>
      <table><thead><tr><th>Name</th><th>Status</th><th>Service root</th><th>Health</th><th>Capacity</th><th>Action</th></tr></thead><tbody>
        {nodes.map(node => <tr key={node.id}>
          <td><strong>{node.name}</strong><div className="muted">weight {node.weight} · upstream auth {node.hasUpstreamCredential ? 'configured' : 'none'}</div></td>
          <td><Status value={node.status} /></td>
          <td className="mono">{node.baseAddress}</td>
          <td><div>{formatLatency(node.lastHealthLatencyMilliseconds)} · {healthStreak(node)}</div><div className="muted">{node.lastHealthError ?? `last healthy ${formatDate(node.lastHealthyAtUtc)}`}</div></td>
          <td>{node.maxConcurrency}</td>
          <td className="actions"><button onClick={() => void testConnection(node)}>{testingNode === node.id ? 'Testing…' : 'Test'}</button><button onClick={() => void api.drainNode(node.id).then(refresh)}>Drain</button><button onClick={() => void api.enableNode(node.id).then(refresh)}>Enable</button><button onClick={() => void api.disableNode(node.id).then(refresh)}>Disable</button></td>
        </tr>)}
      </tbody></table>
      {Object.values(connectionTests).map(result => <div className="secretBox" key={result.nodeId}>
        <strong>{result.success ? '✓' : '✕'} Connection test: {result.nodeName}</strong>
        <p className="mono">Root: {result.serviceRoot}</p>
        <p>Health: {probeSummary(result.health)}</p>
        <p>OpenAI models: {probeSummary(result.openAi)}</p>
        <p className="mono">Chat: {result.chatCompletionsUrl}</p>
        <p className="mono">Responses: {result.responsesUrl}</p>
      </div>)}
    </section>
    <section className="panel formPanel"><h2>Add inference node</h2><form onSubmit={submit}>
      <label>Name<input value={name} onChange={e => setName(e.target.value)} required placeholder="inference-02" /></label>
      <label>Base address / service root<input value={baseAddress} onChange={e => setBaseAddress(e.target.value)} required placeholder="http://10.0.0.12:8000/vllm" /></label>
      <label>Weight<input type="number" min="1" value={weight} onChange={e => setWeight(Number(e.target.value))} /></label>
      <label>Max concurrency<input type="number" min="1" value={maxConcurrency} onChange={e => setMaxConcurrency(Number(e.target.value))} /></label>
      <label>Upstream bearer token (optional)<input type="password" autoComplete="new-password" value={upstreamBearerToken} onChange={e => setUpstreamBearerToken(e.target.value)} placeholder="llama-local" /></label>
      <p className="muted">The token is write-only: LlmProxy encrypts it and never returns it from the API.</p>
      <button className="primary">Add node</button>
    </form>
    <h2>Upstream authentication</h2>
    <form onSubmit={setCredential}>
      <label>Node<select value={credentialNodeId} onChange={e => setCredentialNodeId(e.target.value)} required><option value="">Select node</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name} · {node.hasUpstreamCredential ? 'configured' : 'none'}</option>)}</select></label>
      <label>New bearer token<input type="password" autoComplete="new-password" value={credentialSecret} onChange={e => setCredentialSecret(e.target.value)} placeholder="write-only secret" /></label>
      <div className="actions"><button className="primary" disabled={!credentialNodeId || !credentialSecret}>Set / rotate bearer</button><button type="button" className="secondary" disabled={!credentialNodeId} onClick={() => void clearCredential()}>Clear bearer</button></div>
    </form></section>
  </div>
}

function Routing({ routing, tuning, performance, runtime, deployments, nodes, models, refresh }: { routing: RoutingSettings; tuning: RoutingTuningSettings; performance: DeploymentPerformanceSnapshot[]; runtime: NodeRuntimeMetricsSnapshot[]; deployments: Deployment[]; nodes: Node[]; models: Model[]; refresh: () => Promise<void> }) {
  const [strategy, setStrategy] = useState<RoutingSettings['strategy']>(routing.strategy)
  const [saved, setSaved] = useState(false)
  const [tuningDraft, setTuningDraft] = useState<RoutingTuningSettings>(tuning)
  const [tuningSaved, setTuningSaved] = useState(false)
  useEffect(() => setStrategy(routing.strategy), [routing.strategy])
  useEffect(() => setTuningDraft(tuning), [tuning])

  const nodeNames = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const modelNames = useMemo(() => new Map(models.map(model => [model.id, model.publicName])), [models])
  const deploymentMap = useMemo(() => new Map(deployments.map(deployment => [deployment.id, deployment])), [deployments])

  async function submit(event: FormEvent) {
    event.preventDefault()
    await api.updateRouting(strategy)
    setSaved(true)
    await refresh()
  }

  function setTuning<K extends keyof RoutingTuningSettings>(key: K, value: number) {
    setTuningDraft(current => ({ ...current, [key]: value }))
    setTuningSaved(false)
  }

  async function submitTuning(event: FormEvent) {
    event.preventDefault()
    await api.updateRoutingTuning({
      warmupSamples: tuningDraft.warmupSamples,
      ttftTargetMilliseconds: tuningDraft.ttftTargetMilliseconds,
      ttftPenaltyWeight: tuningDraft.ttftPenaltyWeight,
      failurePenaltyWeight: tuningDraft.failurePenaltyWeight,
      externalLoadPenaltyWeight: tuningDraft.externalLoadPenaltyWeight,
      queuePenaltyWeight: tuningDraft.queuePenaltyWeight,
      kvCacheThreshold: tuningDraft.kvCacheThreshold,
      kvCachePenaltyWeight: tuningDraft.kvCachePenaltyWeight,
      degradedNodePenalty: tuningDraft.degradedNodePenalty,
      unknownNodePenalty: tuningDraft.unknownNodePenalty
    })
    setTuningSaved(true)
    await refresh()
  }

  return <div className="stack">
    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>Current routing policy</h2><span>Applied live, no gateway restart</span></div>
        <div className="metric"><span>Active strategy</span><strong>{friendlyStrategy(routing.strategy)}</strong></div>
        <p>{strategyDescription(routing.strategy)}</p>
        <p>The effective backend weight is node weight × deployment weight. Capacity limits are always respected before a route is selected.</p>
        <p>Weighted least loaded also uses recent TTFT/failure EWMA plus live vLLM queue and KV-cache pressure. PostgreSQL is not queried on the inference hot path.</p>
      </section>
      <section className="panel formPanel"><h2>Change strategy</h2><form onSubmit={submit}>
        <label>Routing strategy<select value={strategy} onChange={e => { setStrategy(e.target.value as RoutingSettings['strategy']); setSaved(false) }}>
          {routing.supportedStrategies.map(item => <option key={item} value={item}>{friendlyStrategy(item)}</option>)}
        </select></label>
        <p>{strategyDescription(strategy)}</p>
        <button className="primary">Apply routing strategy</button>
        {saved && <div className="notice">Routing policy updated live.</div>}
      </form></section>
    </div>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Smart-routing tuning</h2><span>Persisted in PostgreSQL · applied immediately · last update {formatDate(tuning.updatedAtUtc)}</span></div>
      <form onSubmit={submitTuning} className="tuningGrid">
        <label>Warm-up samples<input aria-label="Warm-up samples" type="number" min="0" max="100" value={tuningDraft.warmupSamples} onChange={e => setTuning('warmupSamples', Number(e.target.value))} /></label>
        <label>TTFT target (ms)<input aria-label="TTFT target" type="number" min="1" max="120000" value={tuningDraft.ttftTargetMilliseconds} onChange={e => setTuning('ttftTargetMilliseconds', Number(e.target.value))} /></label>
        <label>TTFT penalty weight<input aria-label="TTFT penalty weight" type="number" min="0" max="10" step="0.05" value={tuningDraft.ttftPenaltyWeight} onChange={e => setTuning('ttftPenaltyWeight', Number(e.target.value))} /></label>
        <label>Failure penalty weight<input aria-label="Failure penalty weight" type="number" min="0" max="10" step="0.05" value={tuningDraft.failurePenaltyWeight} onChange={e => setTuning('failurePenaltyWeight', Number(e.target.value))} /></label>
        <label>External-load penalty<input aria-label="External load penalty" type="number" min="0" max="10" step="0.05" value={tuningDraft.externalLoadPenaltyWeight} onChange={e => setTuning('externalLoadPenaltyWeight', Number(e.target.value))} /></label>
        <label>Queue penalty weight<input aria-label="Queue penalty weight" type="number" min="0" max="10" step="0.05" value={tuningDraft.queuePenaltyWeight} onChange={e => setTuning('queuePenaltyWeight', Number(e.target.value))} /></label>
        <label>KV-cache threshold<input aria-label="KV cache threshold" type="number" min="0" max="1" step="0.01" value={tuningDraft.kvCacheThreshold} onChange={e => setTuning('kvCacheThreshold', Number(e.target.value))} /></label>
        <label>KV-cache penalty weight<input aria-label="KV cache penalty weight" type="number" min="0" max="10" step="0.05" value={tuningDraft.kvCachePenaltyWeight} onChange={e => setTuning('kvCachePenaltyWeight', Number(e.target.value))} /></label>
        <label>Degraded-node penalty<input aria-label="Degraded node penalty" type="number" min="0" max="10" step="0.05" value={tuningDraft.degradedNodePenalty} onChange={e => setTuning('degradedNodePenalty', Number(e.target.value))} /></label>
        <label>Unknown-node penalty<input aria-label="Unknown node penalty" type="number" min="0" max="10" step="0.05" value={tuningDraft.unknownNodePenalty} onChange={e => setTuning('unknownNodePenalty', Number(e.target.value))} /></label>
        <div className="tuningActions"><button className="primary">Apply smart-routing tuning</button><button type="button" className="secondary" onClick={() => { setTuningDraft(emptyRoutingTuning); setTuningSaved(false) }}>Load defaults</button>{tuningSaved && <span className="notice inlineNotice">Tuning updated live.</span>}</div>
      </form>
    </section>

    <div className="gridTwo">
      <section className="panel"><div className="panelTitle"><h2>Performance feedback</h2><span>In-memory EWMA used by Weighted least loaded</span></div>
        <table><thead><tr><th>Deployment</th><th>Node / model</th><th>Samples</th><th>EWMA TTFT</th><th>EWMA duration</th><th>Failure score</th><th>Last sample</th></tr></thead><tbody>
          {performance.map(item => {
            const deployment = deploymentMap.get(item.deploymentId)
            return <tr key={item.deploymentId}>
              <td className="mono">{short(item.deploymentId)}</td>
              <td><strong>{deployment ? nodeNames.get(deployment.nodeId) ?? short(deployment.nodeId) : '—'}</strong><div className="muted">{deployment ? modelNames.get(deployment.modelId) ?? short(deployment.modelId) : 'unknown deployment'}</div></td>
              <td>{item.sampleCount}</td>
              <td>{formatMetricLatency(item.ewmaTimeToFirstByteMilliseconds)}</td>
              <td>{formatMetricLatency(item.ewmaDurationMilliseconds)}</td>
              <td>{formatRatioPercent(item.infrastructureFailureScore)}</td>
              <td>{formatDate(item.lastObservedAtUtc)}</td>
            </tr>
          })}
          {performance.length === 0 && <tr><td colSpan={7} className="muted">No inference samples yet. Signals appear after requests are completed.</td></tr>}
        </tbody></table>
      </section>

      <section className="panel"><div className="panelTitle"><h2>Live vLLM capacity</h2><span>Polled from each service root /metrics</span></div>
        <table><thead><tr><th>Node</th><th>Runtime</th><th>Running</th><th>Waiting</th><th>KV cache</th><th>Generated tokens</th><th>Last sample</th></tr></thead><tbody>
          {runtime.map(item => <tr key={item.nodeId}>
            <td><strong>{nodeNames.get(item.nodeId) ?? short(item.nodeId)}</strong><div className="muted">{item.modelName ?? 'model not reported'}</div></td>
            <td>{item.available ? 'Available' : 'Unavailable'}{item.error && <div className="muted">{item.error}</div>}</td>
            <td>{formatRuntimeNumber(item.runningRequests)}</td>
            <td>{formatRuntimeNumber(item.waitingRequests)}</td>
            <td>{formatRatioPercent(item.kvCacheUsageRatio)}</td>
            <td>{formatRuntimeNumber(item.generationTokensTotal)}</td>
            <td>{formatDate(item.collectedAtUtc ?? item.lastAttemptAtUtc)}</td>
          </tr>)}
          {runtime.length === 0 && <tr><td colSpan={7} className="muted">No vLLM runtime metrics collected yet. The gateway will continue routing without this optional signal.</td></tr>}
        </tbody></table>
      </section>
    </div>
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
    <section className="panel"><div className="panelTitle"><h2>Deployments</h2><span>Logical model → hardware</span></div>
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

function Credentials({ credentials, canWrite, refresh }: { credentials: ApiCredential[]; canWrite: boolean; refresh: () => Promise<void> }) {
  const [name, setName] = useState('GitHub Copilot')
  const [created, setCreated] = useState<CreatedApiCredential | null>(null)
  async function submit(event: FormEvent) {
    event.preventDefault()
    const result = await api.createApiCredential({ name })
    setCreated(result)
    await refresh()
  }
  async function reveal(id: string) {
    const result = await api.revealApiCredential(id)
    setCreated({ id: result.id, name: result.name, keyPrefix: result.keyPrefix, secret: result.secret, enabled: true, createdAtUtc: '', secretAvailable: true })
  }
  async function rotate(id: string) {
    const result = await api.rotateApiCredential(id)
    setCreated(result)
    await refresh()
  }
  return <div className="gridTwo">
    <section className="panel"><div className="panelTitle"><h2>Organization API credentials</h2><span>Admin-created shared/workload keys · caller quotas off by default</span></div>
      <table><thead><tr><th>Name</th><th>Prefix</th><th>State</th><th>Created</th><th>Last used</th><th>Secret</th><th>Action</th></tr></thead><tbody>
        {credentials.map(item => <tr key={item.id}><td><strong>{item.name}</strong></td><td className="mono">{item.keyPrefix}…</td><td>{item.enabled ? 'Enabled' : 'Revoked'}</td><td>{formatDate(item.createdAtUtc)}</td><td>{formatDate(item.lastUsedAtUtc)}</td><td>{item.secretAvailable ? 'Recoverable' : 'Rotate once'}</td><td className="actions">
          {canWrite && item.secretAvailable && <button onClick={() => void reveal(item.id)}>Reveal / copy</button>}
          {canWrite && item.enabled && <button onClick={() => void rotate(item.id)}>Rotate</button>}
          {canWrite && item.enabled && <button onClick={() => void api.revokeApiCredential(item.id).then(refresh)}>Revoke</button>}
        </td></tr>)}
      </tbody></table>
    </section>
    <section className="panel formPanel"><h2>Create organization credential</h2><p className="muted">Use this for shared integrations such as GitHub Copilot. Admins who need a personal key use <a href="/admin/me">My dashboard</a>; normal users can create only their own personal keys.</p><form onSubmit={submit}>
      <label>Name<input value={name} onChange={e => setName(e.target.value)} required placeholder="GitHub Copilot Production" /></label>
      <button className="primary">Generate organization API key</button>
    </form>
      {created && <div className="secretBox"><strong>API key available to administrators</strong><p>The authentication hash is one-way; this recoverable copy is stored application-encrypted at rest. Reveals are audited.</p><code>{created.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(created.secret)}>Copy</button></div>}
    </section>
  </div>
}

function Metrics({ metrics, summary, nodeNames, credentialNames }: { metrics: RequestMetric[]; summary: MetricsSummary; nodeNames: Map<string,string>; credentialNames: Map<string,string> }) {
  return <div className="stack">
    <section className="cards cardsFive">
      <Metric label={`${summary.windowHours}h requests`} value={formatNumber(summary.requestCount)} />
      <Metric label="Success rate" value={formatPercent(summary.successRatePercent)} />
      <Metric label="P50 TTFT" value={formatMetricLatency(summary.p50TimeToFirstByteMilliseconds)} />
      <Metric label="P95 TTFT" value={formatMetricLatency(summary.p95TimeToFirstByteMilliseconds)} />
      <Metric label="Failover requests" value={formatNumber(summary.failoverRequests)} />
    </section>

    <div className="gridTwo">
      <section className="panel"><div className="panelTitle"><h2>By logical model</h2><span>{formatNumber(summary.totalTokens)} observed tokens</span></div>
        <table><thead><tr><th>Model</th><th>Requests</th><th>Errors</th><th>Avg duration</th><th>Avg TTFT</th><th>Output tokens</th></tr></thead><tbody>
          {summary.byModel.map(item => <tr key={item.logicalModel}><td><strong>{item.logicalModel}</strong></td><td>{formatNumber(item.requestCount)}</td><td>{formatNumber(item.errorCount)}</td><td>{formatMetricLatency(item.averageDurationMilliseconds)}</td><td>{formatMetricLatency(item.averageTimeToFirstByteMilliseconds)}</td><td>{formatNumber(item.outputTokens)}</td></tr>)}
        </tbody></table>
      </section>
      <section className="panel"><div className="panelTitle"><h2>By inference node</h2><span>{formatNumber(summary.streamingRequests)} streaming requests</span></div>
        <table><thead><tr><th>Node</th><th>Requests</th><th>Errors</th><th>Avg duration</th><th>P95 duration</th><th>Output tokens</th></tr></thead><tbody>
          {summary.byNode.map(item => <tr key={item.nodeId}><td><strong>{nodeNames.get(item.nodeId) ?? short(item.nodeId)}</strong></td><td>{formatNumber(item.requestCount)}</td><td>{formatNumber(item.errorCount)}</td><td>{formatMetricLatency(item.averageDurationMilliseconds)}</td><td>{formatMetricLatency(item.p95DurationMilliseconds)}</td><td>{formatNumber(item.outputTokens)}</td></tr>)}
        </tbody></table>
      </section>
    </div>

    <section className="panel"><div className="panelTitle"><h2>Latest inference requests</h2><span>No prompt or generated content is stored</span></div>
      <table><thead><tr><th>Time</th><th>Model</th><th>Node</th><th>Credential</th><th>Surface</th><th>Status</th><th>TTFT</th><th>Duration</th><th>Tokens</th><th>Attempts</th><th>Error</th></tr></thead><tbody>
        {metrics.map(metric => <tr key={metric.requestId}>
          <td>{formatDate(metric.startedAtUtc)}</td>
          <td><strong>{metric.logicalModel}</strong></td>
          <td>{metric.nodeId ? nodeNames.get(metric.nodeId) ?? short(metric.nodeId) : '—'}</td>
          <td>{metric.apiCredentialId ? credentialNames.get(metric.apiCredentialId) ?? short(metric.apiCredentialId) : '—'}</td>
          <td>{friendlySurface(metric.surface)}{metric.isStreaming ? ' · SSE' : ''}</td>
          <td>{metric.statusCode}</td>
          <td>{formatMetricLatency(metric.timeToFirstByteMilliseconds)}</td>
          <td>{metric.durationMilliseconds} ms</td>
          <td>{metric.totalTokens ?? '—'}</td>
          <td>{metric.attemptCount}{metric.attemptCount > 1 ? ' · failover' : ''}</td>
          <td className="mono">{metric.errorCode ?? '—'}</td>
        </tr>)}
      </tbody></table>
    </section>
  </div>
}

function Audit({ events }: { events: AuditEvent[] }) {
  return <section className="panel"><div className="panelTitle"><h2>Administrative audit trail</h2><span>Configuration changes only; secrets and prompts are excluded</span></div>
    <table><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Entity</th><th>Source</th><th>Details</th></tr></thead><tbody>
      {events.map(event => <tr key={event.id}><td>{formatDate(event.occurredAtUtc)}</td><td><strong>{event.actor}</strong></td><td className="mono">{event.action}</td><td>{event.entityType} · {short(event.entityId)}</td><td className="mono">{event.sourceIp ?? '—'}</td><td className="mono">{formatAuditDetails(event.detailsJson)}</td></tr>)}
    </tbody></table>
  </section>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
type NavIconName = 'dashboard' | 'nodes' | 'hardware' | 'control' | 'models' | 'deployments' | 'routing' | 'key' | 'metrics' | 'play' | 'logs' | 'audit' | 'help' | 'governance' | 'users' | 'releases'

function NavItem({ active, onClick, icon, children }: { active: boolean; onClick: () => void; icon: NavIconName; children: React.ReactNode }) {
  return <button className={active ? 'active' : ''} onClick={onClick}><NavIcon name={icon} /><span>{children}</span></button>
}

function ProductNavItem({ active, onClick }: { active: boolean; onClick: () => void }) {
  const [version, setVersion] = useState<string | null>(null)
  useEffect(() => {
    let cancelled = false
    fetch('/api/admin/product', { credentials: 'same-origin' })
      .then(response => response.ok ? response.json() as Promise<{ version: string }> : null)
      .then(product => { if (!cancelled && product) setVersion(product.version) })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [])
  return <NavItem icon="releases" active={active} onClick={onClick}>{version ? `v${version} · Release Notes` : 'Release Notes'}</NavItem>
}

function NavIcon({ name }: { name: NavIconName }) {
  const paths: Record<NavIconName, React.ReactNode> = {
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
    nodes: <><circle cx="6" cy="12" r="2.5" /><circle cx="18" cy="6" r="2.5" /><circle cx="18" cy="18" r="2.5" /><path d="M8.3 10.8 15.6 7.2M8.3 13.2l7.3 3.6" /></>,
    hardware: <><rect x="5" y="5" width="14" height="14" rx="2" /><rect x="9" y="9" width="6" height="6" rx="1" /><path d="M9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3" /></>,
    control: <><path d="M4 6h10M18 6h2M4 12h2M10 12h10M4 18h7M15 18h5" /><circle cx="16" cy="6" r="2" /><circle cx="8" cy="12" r="2" /><circle cx="13" cy="18" r="2" /></>,
    models: <><path d="m12 3 8 4.5-8 4.5-8-4.5L12 3Z" /><path d="m4 12 8 4.5 8-4.5M4 16.5 12 21l8-4.5" /></>,
    deployments: <><rect x="4" y="4" width="16" height="6" rx="2" /><rect x="4" y="14" width="16" height="6" rx="2" /><path d="M8 7h.01M8 17h.01M12 7h5M12 17h5" /></>,
    routing: <><path d="M5 5v4a3 3 0 0 0 3 3h8" /><path d="m13 9 3 3-3 3" /><path d="M5 19v-3a4 4 0 0 1 4-4" /></>,
    key: <><circle cx="8" cy="12" r="4" /><path d="M12 12h9M17 12v3M20 12v2" /></>,
    metrics: <><path d="M4 20V10M10 20V4M16 20v-7M22 20V7" /></>,
    play: <path d="m8 5 11 7-11 7V5Z" />,
    logs: <><path d="M6 3h9l3 3v15H6z" /><path d="M15 3v4h4M9 11h6M9 15h6" /></>,
    audit: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
    help: <><circle cx="12" cy="12" r="9" /><path d="M9.8 9a2.5 2.5 0 1 1 3.5 2.3c-.9.4-1.3 1-1.3 1.7M12 17h.01" /></>,
    governance: <><path d="M12 3 5 6v5c0 4.6 2.8 8.1 7 10 4.2-1.9 7-5.4 7-10V6l-7-3Z" /><path d="m9 12 2 2 4-5" /></>,
    users: <><circle cx="9" cy="8" r="3" /><circle cx="17" cy="9" r="2.5" /><path d="M3 20a6 6 0 0 1 12 0M14 16a5 5 0 0 1 7 4" /></>,
    releases: <><path d="M4 5h10l6 6-9 9-7-7V5Z" /><circle cx="9" cy="10" r="1.5" /></>
  }
  return <span className="navIcon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg></span>
}

function title(view: View) { return ({ dashboard: 'Gateway dashboard', nodes: 'Inference nodes', hardware: 'Hardware', 'model-management': 'Model & hardware control', models: 'Models & deployments', deployments: 'Models & deployments', routing: 'Routing policy', credentials: 'API credentials', users: 'Users & access', metrics: 'Inference observability', playground: 'Model & classifier playground', logs: 'Request & response audit', audit: 'Administrative audit trail', help: 'Endpoint & platform guide', governance: 'Usage & governance', releases: 'Version & release notes' } as const)[view] }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function formatLatency(value?: number | null) { return value === null || value === undefined ? '—' : `${value} ms` }
function formatMetricLatency(value?: number | null) { return value === null || value === undefined ? '—' : `${Math.round(value)} ms` }
function formatPercent(value: number) { return `${value.toFixed(1)}%` }
function formatRatioPercent(value?: number | null) { return value === null || value === undefined ? '—' : `${(value * 100).toFixed(1)}%` }
function formatRuntimeNumber(value?: number | null) { return value === null || value === undefined ? '—' : new Intl.NumberFormat().format(Math.round(value)) }
function formatNumber(value: number) { return new Intl.NumberFormat().format(value) }
function healthStreak(node: Node) { return node.consecutiveHealthFailures > 0 ? `${node.consecutiveHealthFailures} fail` : `${node.consecutiveHealthSuccesses} ok` }
function short(value: string) { return value.length > 12 ? `${value.slice(0, 8)}…` : value }
function friendlyStrategy(value: RoutingSettings['strategy']) { return ({ WeightedLeastLoaded: 'Weighted least loaded', RoundRobin: 'Round robin', WeightedRoundRobin: 'Weighted round robin' } as const)[value] }
function strategyDescription(value: RoutingSettings['strategy']) { return ({ WeightedLeastLoaded: 'Routes to the least-loaded eligible deployment while accounting for configured capacity, health, recent inference performance and live vLLM pressure.', RoundRobin: 'Cycles evenly through eligible deployments. Useful for deterministic local tests and homogeneous runtimes.', WeightedRoundRobin: 'Cycles through eligible deployments proportionally to their effective weights.' } as const)[value] }
function friendlySurface(value: string) { return value === 'chat_completions' ? 'Chat Completions' : value === 'responses' ? 'Responses' : value }
function probeSummary(probe: NodeConnectionTest['health']) { return probe.success ? `✓ HTTP ${probe.statusCode} in ${probe.latencyMilliseconds} ms` : `✕ ${probe.error ?? `HTTP ${probe.statusCode}`} (${probe.latencyMilliseconds} ms)` }
function formatAuditDetails(value?: string | null) { if (!value) return '—'; return value.length > 160 ? `${value.slice(0, 157)}…` : value }
