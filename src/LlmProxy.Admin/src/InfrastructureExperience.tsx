import { useRouteTab } from './adminNavigation'
import HardwareExperience from './HardwareExperience'
import ModelHardwareExperience from './ModelHardwareExperience'
import NodesPage from './NodesPage'
import { Tabs } from './UiPrimitives'
import type { Deployment, Model, Node, NodeHardwareMetricsSnapshot } from './types'

type Section = 'fleet' | 'capacity' | 'inventory'

export default function InfrastructureExperience({ nodes, models, deployments, hardware, canWrite, refresh }: {
  nodes: Node[]
  models: Model[]
  deployments: Deployment[]
  hardware: NodeHardwareMetricsSnapshot[]
  canWrite: boolean
  refresh: () => Promise<void>
}) {
  const [section, setSection] = useRouteTab<Section>('section', ['fleet', 'capacity', 'inventory'], 'fleet')

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>Servers, agents & hardware</h2>
        <p className="muted">Pair or recover a server, inspect its live capacity, and maintain installed runtimes. To discover and install models, go to the dedicated model catalog.</p>
      </div>
      <div className="capacityLegend">
        <strong>{nodes.length} hardware node{nodes.length === 1 ? '' : 's'}</strong>
        <span>{deployments.filter(item => item.enabled).length} routing-enabled deployments</span>
        <a href="/admin/catalog?tab=deploy">Browse & install models →</a>
      </div>
    </section>

    <Tabs value={section} onChange={setSection} items={[
      { value: 'fleet', label: 'Fleet & access', count: nodes.length },
      { value: 'capacity', label: 'Capacity & telemetry', count: nodes.length },
      { value: 'inventory', label: 'Hardware inventory & runtimes', count: deployments.length }
    ]} />

    {section === 'fleet' && <NodesPage nodes={nodes} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'capacity' && <HardwareExperience nodes={nodes} hardware={hardware} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'inventory' && <ModelHardwareExperience nodes={nodes} canWrite={canWrite} refresh={refresh} embedded onRecoverAgent={() => setSection("fleet")} />}

    <section className="panel infrastructureGuide">
      <div><strong>People & access</strong><span>Users & Access decides who may use personal LlmProxy credentials. A shared Copilot organization key does not identify the individual developer.</span></div>
      <div><strong>Physical concurrency</strong><span>Capacity is simultaneous inference requests on a machine, not a people count. All deployments on the same hardware share the ceiling.</span></div>
      <div><strong>Rate limits</strong><span>Usage & Governance controls request/token quotas per user, group or credential independently from physical capacity.</span></div>
    </section>
  </div>
}
