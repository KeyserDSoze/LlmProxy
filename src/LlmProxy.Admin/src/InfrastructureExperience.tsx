import { useState } from 'react'
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
  const [section, setSection] = useState<Section>('fleet')

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>Physical infrastructure</h2>
        <p className="muted">One hardware node represents one physical machine. Model runtimes may use different ports while sharing that machine's physical concurrency ceiling.</p>
      </div>
      <div className="capacityLegend">
        <strong>{nodes.length} hardware node{nodes.length === 1 ? '' : 's'}</strong>
        <span>{deployments.filter(item => item.enabled).length} routing-enabled deployments</span>
      </div>
    </section>

    <Tabs value={section} onChange={setSection} items={[
      { value: 'fleet', label: 'Fleet & access', count: nodes.length },
      { value: 'capacity', label: 'Capacity & telemetry', count: nodes.length },
      { value: 'inventory', label: 'Inventory & model lifecycle', count: deployments.length }
    ]} />

    {section === 'fleet' && <NodesPage nodes={nodes} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'capacity' && <HardwareExperience nodes={nodes} hardware={hardware} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'inventory' && <ModelHardwareExperience nodes={nodes} canWrite={canWrite} refresh={refresh} embedded />}

    <section className="panel infrastructureGuide">
      <div><strong>People & access</strong><span>Users & Access decides who may use personal LlmProxy credentials. A shared Copilot organization key does not identify the individual developer.</span></div>
      <div><strong>Physical concurrency</strong><span>Capacity is simultaneous inference requests on a machine, not a people count. All deployments on the same hardware share the ceiling.</span></div>
      <div><strong>Rate limits</strong><span>Usage & Governance controls request/token quotas per user, group or credential independently from physical capacity.</span></div>
    </section>
  </div>
}
