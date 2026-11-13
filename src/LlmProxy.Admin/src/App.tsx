import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { navigateAdmin, normalizeLegacyAdminUrl, urlForAdminView, viewFromAdminPath } from './adminNavigation'
import type { AdminView } from './adminNavigation'
import GovernanceExperience from './GovernanceExperience'
import InfrastructureExperience from './InfrastructureExperience'
import ModelHardwareExperience from './ModelHardwareExperience'
import SystemOneExperience from './SystemOneExperience'
import SettingsPage from './SettingsPage'
import { useT } from './uiPreferences'
import type { TranslationKey } from './uiPreferences'
import CredentialsPage from './CredentialsPage'
import MetricsPage from './MetricsPage'
import ReleaseNotesPage from './ReleaseNotesPage'
import ContentLogs from './ContentLogs'
import HelpPage from './HelpPage'
import PageDocumentation from './PageDocumentation'
import PlaygroundExperience from './PlaygroundExperience'
import UsersAccessExperience from './UsersAccessExperience'
import UpdateBanner, { availableUpdateCount, useProductUpdates } from './UpdateBanner'
import { AdminLink } from './UiPrimitives'
import type { ApiCredential, AuditEvent, Deployment, DeploymentPerformanceSnapshot, MetricsSummary, Model, Node, NodeHardwareMetricsSnapshot, NodeRuntimeMetricsSnapshot, Overview, RoutingSettings, RoutingTuningSettings } from './types'

export type View = AdminView

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

// Old bookmarks into the former Servers → model inventory are rewritten in place.
function currentView(): View {
  const legacy = normalizeLegacyAdminUrl(window.location.pathname, window.location.search)
  if (legacy) window.history.replaceState(null, '', legacy)
  return viewFromAdminPath(window.location.pathname)
}

export default function App({ initialView = 'dashboard' }: { initialView?: View }) {
  const t = useT()
  const [mobileNavOpen, setMobileNavOpen] = useState(false)
  const [productVersion, setProductVersion] = useState<string | null>(null)
  useEffect(() => {
    let active = true
    void fetch('/api/admin/product', { credentials: 'same-origin' })
      .then(res => res.ok ? res.json() as Promise<{ version: string }> : null)
      .then(value => { if (active && value?.version) setProductVersion(value.version) })
      .catch(() => undefined)
    return () => { active = false }
  }, [])
  useEffect(() => {
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setMobileNavOpen(false)
    }
    window.addEventListener('keydown', onEscape)
    return () => window.removeEventListener('keydown', onEscape)
  }, [])
  const [view, setView] = useState<View>(() => currentView() || initialView)
  useEffect(() => {
    const onHistoryChange = () => {
      setView(currentView())
      setMobileNavOpen(false)
    }
    window.addEventListener('popstate', onHistoryChange)
    return () => window.removeEventListener('popstate', onHistoryChange)
  }, [])
  function navigate(view: View) {
    const url = urlForAdminView(view)
    if (window.location.pathname !== url || window.location.search)
      window.history.pushState(null, '', url)
    setView(view)
    setMobileNavOpen(false)
  }
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
  const [audit, setAudit] = useState<AuditEvent[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [authRequired, setAuthRequired] = useState(false)
  const [canWrite, setCanWrite] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextSession, nextOverview, nextRouting, nextTuning, nextPerformance, nextRuntime, nextHardware, nextNodes, nextModels, nextDeployments, nextCredentials, nextMetricsSummary, nextAudit] = await Promise.all([
        api.adminSession(), api.overview(), api.routing(), api.routingTuning(), api.routingPerformance(), api.routingRuntime(), api.hardware(), api.nodes(), api.models(), api.deployments(), api.apiCredentials(), api.metricsSummary(24), api.audit(100)
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

  const attention = useMemo(() => attentionItems(nodes, models, deployments), [nodes, models, deployments])
  const urgent = attention.filter(item => item.tone !== 'info').length
  const { overview: productUpdates, refresh: refreshProductUpdates } = useProductUpdates()
  const pendingUpdates = availableUpdateCount(productUpdates)

  return (
    <div className="shell">
      {mobileNavOpen && <button className="mobileNavScrim" type="button" aria-label={t('nav.close')}
        onClick={() => setMobileNavOpen(false)} />}
      <aside className={mobileNavOpen ? 'sidebar is-open' : 'sidebar'} id="admin-navigation">
        <div className="sidebarTop">
          <div className="brand">
            <div className="brandMark"><img src="/admin/favicon.svg" alt="" /></div>
            <div className="brandText"><strong>LlmProxy</strong>
              <span className="brandVersion" data-testid="gateway-version">{productVersion ? 'v' + productVersion.replace(/^v/, '') : '—'}</span>
            </div>
          </div>
          <button type="button" className="sidebarClose" aria-label={t('nav.close')} onClick={() => setMobileNavOpen(false)}>×</button>
        </div>
        <nav aria-label="Admin navigation">
          <NavItem icon="dashboard" active={view === 'dashboard'} badge={urgent} onClick={() => navigate('dashboard')}>{t('nav.dashboard')}</NavItem>

          <div className="navGroupLabel">{t('nav.modelsGroup')}</div>
          <NavItem icon="models" active={view === 'models' || view === 'catalog'} onClick={() => navigate('models')}>{t('nav.modelsHome')}</NavItem>
          <NavItem icon="routing" active={view === 'routing'} onClick={() => navigate('routing')}>{t('nav.routing')}</NavItem>
          <NavItem icon="classifier" active={view === 'system-one'} onClick={() => navigate('system-one')}>{t('nav.systemOneShort')}</NavItem>
          {canWrite && <NavItem icon="play" active={view === 'playground'} onClick={() => navigate('playground')}>{t('nav.playground')}</NavItem>}

          <div className="navGroupLabel">{t('nav.infrastructureGroup')}</div>
          <NavItem icon="nodes" active={view === 'infrastructure'} onClick={() => navigate('infrastructure')}>{t('nav.infrastructure')}</NavItem>

          <div className="navGroupLabel">{t('nav.accessGroup')}</div>
          <NavItem icon="key" active={view === 'credentials'} onClick={() => navigate('credentials')}>{t('nav.credentials')}</NavItem>
          {canWrite && <NavItem icon="users" active={view === 'users'} onClick={() => navigate('users')}>{t('nav.users')}</NavItem>}
          <NavItem icon="governance" active={view === 'governance'} onClick={() => navigate('governance')}>{t('nav.governance')}</NavItem>

          <div className="navGroupLabel">{t('nav.monitorGroup')}</div>
          <NavItem icon="metrics" active={view === 'metrics'} onClick={() => navigate('metrics')}>{t('nav.metrics')}</NavItem>
          {canWrite && <NavItem icon="logs" active={view === 'logs'} onClick={() => navigate('logs')}>{t('nav.logs')}</NavItem>}
          <NavItem icon="audit" active={view === 'audit'} onClick={() => navigate('audit')}>{t('nav.audit')}</NavItem>

          <div className="navSecondary">
            <div className="navGroupLabel">{t('nav.platformGroup')}</div>
            <NavItem icon="help" active={view === 'help'} onClick={() => navigate('help')}>{t('nav.help')}</NavItem>
            <NavItem icon="settings" active={view === 'settings'} onClick={() => navigate('settings')}>{t('nav.settings')}</NavItem>
            <NavItem icon="releases" active={view === 'releases'} badge={pendingUpdates} badgeLabel={t('nav.releasesBadge')} badgeTone="info"
              onClick={() => navigate('releases')}>{t('nav.releases')}</NavItem>
          </div>
        </nav>
        <div className="sidebarFooter">
          <span><span className="dot" /> {t('nav.footer')}</span>
        </div>
      </aside>

      <main>
        <header>
          <div className="headerTitleRow">
            <button className="mobileMenuButton" type="button" aria-controls="admin-navigation"
              aria-expanded={mobileNavOpen} aria-label={t('nav.open')}
              onClick={() => setMobileNavOpen(true)}>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true"><path d="M4 6h16M4 12h16M4 18h16"/></svg>
            </button>
            <div>
              <div className="uxBreadcrumb"><button onClick={() => navigate('dashboard')}>{t('nav.overview')}</button><span aria-hidden="true">/</span><span>{t(viewTitleKey(view))}</span></div>
              <h1>{t(viewTitleKey(view))}</h1><p>{t(viewDescriptionKey(view))}</p>
            </div>
          </div>
          <div className="shellHeaderControls"><button className="secondary" onClick={() => void refresh()}>{t('common.refresh')}</button></div>
        </header>

        {authRequired && <div className="notice">Authentication is required. <a href="/auth/login">Sign in with Entra ID</a>. Normal users can open <a href="/admin/me">My API Keys</a>.</div>}
        {view !== 'releases' && <UpdateBanner overview={productUpdates} loadedVersion={productVersion} canWrite={canWrite}
          onOpenReleases={() => navigate('releases')} onRefresh={refreshProductUpdates} />}
        {error && <div className="error">{error}</div>}
        {loading ? <div className="loading">{t('common.loading')}</div> : (
          <>
            {view === 'dashboard' && <>
              <AttentionCenter items={attention} />
              <Dashboard overview={overview} nodes={nodes} routing={routing} metricsSummary={metricsSummary} />
              <OperatorStart nodes={nodes} models={models} deployments={deployments} canWrite={canWrite} />
            </>}
            {view === 'infrastructure' && <InfrastructureExperience nodes={nodes} models={models} deployments={deployments} hardware={hardware} canWrite={canWrite} refresh={refresh} />}
            {view === 'catalog' && <ModelHardwareExperience nodes={nodes} models={models} deployments={deployments}
              canWrite={canWrite} refresh={refresh} mode="models" defaultPanel="catalog" />}
            {view === 'models' && <ModelHardwareExperience nodes={nodes} models={models} deployments={deployments}
              canWrite={canWrite} refresh={refresh} mode="models" defaultPanel="installed" />}
            {view === 'system-one' && <SystemOneExperience models={models} deployments={deployments} nodes={nodes} canWrite={canWrite} refresh={refresh} />}
            {view === 'settings' && <SettingsPage productVersion={productVersion} onOpenReleases={() => navigate('releases')} />}
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
            {view !== 'releases' && <PageDocumentation page={view} />}
          </>
        )}
      </main>
    </div>
  )
}

type AttentionItem = {
  key: string
  tone: 'danger' | 'warning' | 'info'
  title: string
  detail: string
  action: TranslationKey
  href: string
  titleKey?: TranslationKey
  detailKey?: TranslationKey
}

const usableNodeStatuses = new Set(['Healthy', 'Degraded', 'Unknown'])

/** Derived only from data the dashboard already loads; no extra Agent round-trips. */
export function attentionItems(nodes: Node[], models: Model[], deployments: Deployment[]): AttentionItem[] {
  const items: AttentionItem[] = []
  if (nodes.length === 0)
    items.push({ key: 'no-servers', tone: 'info', title: '', detail: '', titleKey: 'dashboard.attention.noServers',
      detailKey: 'dashboard.attention.noServersDesc', action: 'dashboard.attention.pair', href: '/admin/infrastructure?section=fleet' })
  for (const node of nodes) {
    const href = '/admin/infrastructure?section=fleet'
    if (!node.enabled)
      items.push({ key: 'node-disabled-' + node.id, tone: 'warning', title: node.name, titleKey: 'dashboard.attention.disabled',
        detail: node.lastHealthError ?? '', action: 'dashboard.attention.openServers', href })
    else if (node.status === 'Unhealthy')
      items.push({ key: 'node-unhealthy-' + node.id, tone: 'danger', title: node.name, titleKey: 'dashboard.attention.unhealthy',
        detail: node.lastHealthError ?? '', action: 'dashboard.attention.openServers', href })
    else if (node.status === 'Degraded')
      items.push({ key: 'node-degraded-' + node.id, tone: 'warning', title: node.name, titleKey: 'dashboard.attention.degraded',
        detail: node.lastHealthError ?? '', action: 'dashboard.attention.openServers', href })
    else if (node.status === 'Draining')
      items.push({ key: 'node-draining-' + node.id, tone: 'info', title: node.name, titleKey: 'dashboard.attention.draining',
        detail: '', action: 'dashboard.attention.openServers', href })
  }
  const usableNodes = new Set(nodes.filter(node => node.enabled && usableNodeStatuses.has(node.status)).map(node => node.id))
  for (const model of models) {
    if (model.enabled === false) continue
    const routed = deployments.some(item => item.modelId === model.id && item.enabled && usableNodes.has(item.nodeId))
    if (!routed)
      items.push({ key: 'model-' + model.id, tone: 'warning', title: model.publicName, titleKey: 'dashboard.attention.noRoute',
        detail: '', detailKey: 'dashboard.attention.noRouteDesc', action: 'dashboard.attention.openModels',
        href: '/admin/models?panel=advanced' })
  }
  const disabled = deployments.filter(item => !item.enabled).length
  if (disabled > 0)
    items.push({ key: 'disabled-deployments', tone: 'info', title: String(disabled), titleKey: 'dashboard.attention.disabledRoutes',
      detail: '', detailKey: 'dashboard.attention.disabledRoutesDesc', action: 'dashboard.attention.openModels',
      href: '/admin/models?panel=installed' })
  return items
}

function AttentionCenter({ items }: { items: AttentionItem[] }) {
  const t = useT()
  if (items.length === 0) return <section className="panel attentionPanel attentionPanel--ok" aria-label={t('dashboard.attention.title')}>
    <div className="attentionOk"><span className="attentionOkIcon" aria-hidden="true">✓</span>
      <div><strong>{t('dashboard.attention.ok')}</strong><p>{t('dashboard.attention.okDesc')}</p></div></div>
  </section>
  return <section className="panel attentionPanel" aria-label={t('dashboard.attention.title')}>
    <div className="panelTitle"><div><h2>{t('dashboard.attention.title')}</h2><span>{t('dashboard.attention.desc')}</span></div>
      <span className="pill pill-warning">{items.length}</span></div>
    <ul className="attentionList">
      {items.map(item => <li key={item.key} className={'attentionItem attentionItem--' + item.tone}>
        <span className="attentionDot" aria-hidden="true" />
        <div className="attentionText">
          <strong>{item.titleKey ? t(item.titleKey) : item.title}{item.titleKey && item.title ? ' · ' + item.title : ''}</strong>
          {(item.detailKey || item.detail) && <span>{item.detailKey ? t(item.detailKey) : item.detail}</span>}
        </div>
        <AdminLink className="buttonLink secondary" href={item.href}>{t(item.action)} →</AdminLink>
      </li>)}
    </ul>
  </section>
}

function OperatorStart({ nodes, models, deployments, canWrite }: {
  nodes: Node[]; models: Model[]; deployments: Deployment[]; canWrite: boolean
}) {
  const t = useT()
  const systemOne = models.filter(model => model.surface === 'SystemOne')
  const readyRoutes = deployments.filter(item => item.enabled).length
  const healthyNodes = nodes.filter(node => node.status === 'Healthy').length
  const tasks: Array<{ title: string; summary: string; href: string; action: string; write?: boolean }> = [
    { title: t('dashboard.tasks.install.title'), summary: t('dashboard.tasks.install.desc'), href: '/admin/catalog', action: t('dashboard.tasks.install.action') },
    { title: t('dashboard.tasks.classifier.title'), summary: t('dashboard.tasks.classifier.desc'), href: '/admin/system-one', action: t('dashboard.tasks.classifier.action') },
    { title: t('dashboard.tasks.server.title'), summary: t('dashboard.tasks.server.desc'), href: '/admin/infrastructure', action: t('dashboard.tasks.server.action') },
    { title: t('dashboard.tasks.publish.title'), summary: t('dashboard.tasks.publish.desc'), href: '/admin/models?panel=advanced', action: t('dashboard.tasks.publish.action') },
    { title: t('dashboard.tasks.test.title'), summary: t('dashboard.tasks.test.desc'), href: '/admin/playground', action: t('dashboard.tasks.test.action'), write: true }
  ]
  return <section className="panel uxStartPanel">
    <div className="panelTitle"><div><h2>{t('dashboard.tasks.title')}</h2>
      <span>{t('dashboard.tasks.desc')}</span></div>
      <div className="uxSummary">{healthyNodes}/{nodes.length} {t('dashboard.summary.nodes')} · {readyRoutes} {t('dashboard.summary.deployments')} · {systemOne.length} {t('dashboard.summary.classifiers')}</div>
    </div>
    <div className="uxPathGrid">
      {tasks.filter(task => canWrite || !task.write).map(task => <div key={task.title} className="uxTask">
        <strong>{task.title}</strong>
        <p>{task.summary}</p>
        <button className="secondary" onClick={() => navigateAdmin(task.href)}>{task.action} →</button>
      </div>)}
    </div>
  </section>
}

function Dashboard({ overview, nodes, routing, metricsSummary }: { overview: Overview; nodes: Node[]; routing: RoutingSettings; metricsSummary: MetricsSummary }) {
  const t = useT()
  return <>
    <section className="cards cardsFive">
      <Metric label={t('dashboard.kpi.fleet')} value={`${overview.nodes.healthy}/${overview.nodes.total}`} />
      <Metric label={t('dashboard.kpi.models')} value={overview.models} />
      <Metric label={t('dashboard.kpi.deployments')} value={overview.deployments} />
      <Metric label={t('dashboard.kpi.active')} value={overview.activeRequests} />
      <Metric label={t('dashboard.kpi.today')} value={overview.requestsToday} />
    </section>
    <section className="cards cardsFive">
      <Metric label={`${metricsSummary.windowHours}h requests`} value={formatNumber(metricsSummary.requestCount)} />
      <Metric label={t('dashboard.kpi.success')} value={formatPercent(metricsSummary.successRatePercent)} />
      <Metric label="P50 TTFT" value={formatMetricLatency(metricsSummary.p50TimeToFirstByteMilliseconds)} />
      <Metric label="P95 TTFT" value={formatMetricLatency(metricsSummary.p95TimeToFirstByteMilliseconds)} />
      <Metric label={t('dashboard.kpi.output')} value={formatNumber(metricsSummary.outputTokens)} />
    </section>
    <section className="panel">
      <div className="panelTitle"><h2>{t('dashboard.fleet.title')}</h2><span>Routing: {friendlyStrategy(routing.strategy)}</span></div>
      <div className="tableScroll"><table><thead><tr><th>{t('dashboard.fleet.node')}</th><th>{t('dashboard.fleet.status')}</th><th>{t('dashboard.fleet.root')}</th><th>{t('dashboard.fleet.latency')}</th><th>{t('dashboard.fleet.streak')}</th><th>{t('dashboard.fleet.last')}</th></tr></thead>
        <tbody>{nodes.map(node => <tr key={node.id}>
          <td><strong>{node.name}</strong></td><td><Status value={node.status} /></td><td className="mono">{node.baseAddress}</td>
          <td>{formatLatency(node.lastHealthLatencyMilliseconds)}</td><td>{healthStreak(node)}</td><td>{formatDate(node.lastHealthCheckUtc)}</td>
        </tr>)}</tbody>
      </table></div>
    </section>
  </>
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
        <p className="muted">Published model names and their server placements are managed in <AdminLink href="/admin/models?panel=advanced">Models → Publishing</AdminLink>.</p>
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

    <div className="gridEqual">
      <section className="panel"><div className="panelTitle"><h2>Performance feedback</h2><span>In-memory EWMA used by Weighted least loaded</span></div>
        <div className="tableScroll"><table><thead><tr><th>Deployment</th><th>Node / model</th><th>Samples</th><th>EWMA TTFT</th><th>EWMA duration</th><th>Failure score</th><th>Last sample</th></tr></thead><tbody>
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
        </tbody></table></div>
      </section>

      <section className="panel"><div className="panelTitle"><h2>Live vLLM capacity</h2><span>Polled from each service root /metrics</span></div>
        <div className="tableScroll"><table><thead><tr><th>Node</th><th>Runtime</th><th>Running</th><th>Waiting</th><th>KV cache</th><th>Generated tokens</th><th>Last sample</th></tr></thead><tbody>
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
        </tbody></table></div>
      </section>
    </div>
  </div>
}

function Audit({ events }: { events: AuditEvent[] }) {
  return <section className="panel"><div className="panelTitle"><h2>Administrative audit trail</h2><span>Configuration changes only; secrets and prompts are excluded</span></div>
    <div className="tableScroll"><table><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Entity</th><th>Source</th><th>Details</th></tr></thead><tbody>
      {events.map(event => <tr key={event.id}><td>{formatDate(event.occurredAtUtc)}</td><td><strong>{event.actor}</strong></td><td className="mono">{event.action}</td><td>{event.entityType} · {short(event.entityId)}</td><td className="mono">{event.sourceIp ?? '—'}</td><td className="mono">{formatAuditDetails(event.detailsJson)}</td></tr>)}
    </tbody></table></div>
  </section>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
type NavIconName = 'dashboard' | 'nodes' | 'models' | 'routing' | 'classifier' | 'key' | 'metrics' | 'play' | 'logs' | 'audit' | 'help' | 'governance' | 'users' | 'releases' | 'settings'

function NavItem({ active, onClick, icon, badge, badgeLabel, badgeTone, children }: { active: boolean; onClick: () => void; icon: NavIconName; badge?: number; badgeLabel?: string; badgeTone?: 'info'; children: React.ReactNode }) {
  return <button className={active ? 'active' : ''} aria-current={active ? 'page' : undefined} onClick={onClick}>
    <NavIcon name={icon} /><span>{children}</span>
    {badge ? <span className={badgeTone === 'info' ? 'navBadge navBadge--info' : 'navBadge'}><span aria-hidden="true">{badge}</span><span className="srOnly">{badge} {badgeLabel ?? 'items need attention'}</span></span> : null}
  </button>
}

function NavIcon({ name }: { name: NavIconName }) {
  const paths: Record<NavIconName, React.ReactNode> = {
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
    nodes: <><rect x="4" y="4" width="16" height="6" rx="2" /><rect x="4" y="14" width="16" height="6" rx="2" /><path d="M8 7h.01M8 17h.01M12 7h5M12 17h5" /></>,
    models: <><path d="m12 3 8 4.5-8 4.5-8-4.5L12 3Z" /><path d="m4 12 8 4.5 8-4.5M4 16.5 12 21l8-4.5" /></>,
    routing: <><path d="M5 5v4a3 3 0 0 0 3 3h8" /><path d="m13 9 3 3-3 3" /><path d="M5 19v-3a4 4 0 0 1 4-4" /></>,
    classifier: <><path d="M4 6h16M4 12h10M4 18h6" /><path d="m15 16 2 2 4-4" /></>,
    key: <><circle cx="8" cy="12" r="4" /><path d="M12 12h9M17 12v3M20 12v2" /></>,
    metrics: <><path d="M4 20V10M10 20V4M16 20v-7M22 20V7" /></>,
    play: <path d="m8 5 11 7-11 7V5Z" />,
    logs: <><path d="M6 3h9l3 3v15H6z" /><path d="M15 3v4h4M9 11h6M9 15h6" /></>,
    audit: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
    help: <><circle cx="12" cy="12" r="9" /><path d="M9.8 9a2.5 2.5 0 1 1 3.5 2.3c-.9.4-1.3 1-1.3 1.7M12 17h.01" /></>,
    governance: <><path d="M12 3 5 6v5c0 4.6 2.8 8.1 7 10 4.2-1.9 7-5.4 7-10V6l-7-3Z" /><path d="m9 12 2 2 4-5" /></>,
    users: <><circle cx="9" cy="8" r="3" /><circle cx="17" cy="9" r="2.5" /><path d="M3 20a6 6 0 0 1 12 0M14 16a5 5 0 0 1 7 4" /></>,
    releases: <><path d="M4 5h10l6 6-9 9-7-7V5Z" /><circle cx="9" cy="10" r="1.5" /></>,
    settings: <><circle cx="12" cy="12" r="3" /><path d="M10 2h4l.6 2.5a8 8 0 0 1 2 1.2L19 5l2 3-1.8 2a8 8 0 0 1 0 4l1.8 2-2 3-2.4-.7a8 8 0 0 1-2 1.2L14 22h-4l-.6-2.5a8 8 0 0 1-2-1.2L5 19l-2-3 1.8-2a8 8 0 0 1 0-4L3 8l2-3 2.4.7a8 8 0 0 1 2-1.2L10 2Z" /></>
  }
  return <span className="navIcon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg></span>
}

const titleKeys: Record<View, TranslationKey> = {
  dashboard: 'page.dashboard.title',
  catalog: 'page.catalog.title',
  'system-one': 'page.systemOne.title',
  infrastructure: 'page.infrastructure.title',
  models: 'page.models.title',
  routing: 'page.routing.title',
  credentials: 'page.credentials.title',
  users: 'page.users.title',
  governance: 'page.governance.title',
  metrics: 'page.metrics.title',
  playground: 'page.playground.title',
  logs: 'page.logs.title',
  audit: 'page.audit.title',
  help: 'page.help.title',
  releases: 'page.releases.title',
  settings: 'page.settings.title'
}
const descriptionKeys: Record<View, TranslationKey> = {
  dashboard: 'page.dashboard.desc',
  catalog: 'page.catalog.desc',
  'system-one': 'page.systemOne.desc',
  infrastructure: 'page.infrastructure.desc',
  models: 'page.models.desc',
  routing: 'page.routing.desc',
  credentials: 'page.credentials.desc',
  users: 'page.users.desc',
  governance: 'page.governance.desc',
  metrics: 'page.metrics.desc',
  playground: 'page.playground.desc',
  logs: 'page.logs.desc',
  audit: 'page.audit.desc',
  help: 'page.help.desc',
  releases: 'page.releases.desc',
  settings: 'page.settings.desc'
}
function viewTitleKey(view: View): TranslationKey { return titleKeys[view] }
function viewDescriptionKey(view: View): TranslationKey { return descriptionKeys[view] }
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
function formatAuditDetails(value?: string | null) { if (!value) return '—'; return value.length > 160 ? `${value.slice(0, 157)}…` : value }
