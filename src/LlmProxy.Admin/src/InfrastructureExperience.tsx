import { useRouteTab } from './adminNavigation'
import HardwareExperience from './HardwareExperience'
import ModelHardwareExperience from './ModelHardwareExperience'
import NodesPage from './NodesPage'
import { Tabs } from './UiPrimitives'
import { useT } from './uiPreferences'
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
  const t = useT()
  const [section, setSection] = useRouteTab<Section>('section', ['fleet', 'capacity', 'inventory'], 'fleet')

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>{t('infra.header')}</h2>
        <p className="muted">{t('infra.desc')}</p>
      </div>
      <div className="capacityLegend">
        <strong>{nodes.length} {t('infra.servers')}</strong>
        <span>{deployments.filter(item => item.enabled).length} {t('infra.enabled')}</span>
        <a href="/admin/catalog?tab=deploy">{t('infra.browse')} →</a>
      </div>
    </section>

    <Tabs value={section} onChange={setSection} items={[
      { value: 'fleet', label: t('infra.fleet'), count: nodes.length },
      { value: 'capacity', label: t('infra.capacity'), count: nodes.length },
      { value: 'inventory', label: t('infra.inventory'), count: deployments.length }
    ]} />

    {section === 'fleet' && <NodesPage nodes={nodes} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'capacity' && <HardwareExperience nodes={nodes} hardware={hardware} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'inventory' && <ModelHardwareExperience nodes={nodes} canWrite={canWrite} refresh={refresh} embedded onRecoverAgent={() => setSection("fleet")} />}

    <section className="panel infrastructureGuide">
      <div><strong>{t('infra.people')}</strong><span>{t('infra.peopleDesc')}</span></div>
      <div><strong>{t('infra.concurrency')}</strong><span>{t('infra.concurrencyDesc')}</span></div>
      <div><strong>{t('infra.ratelimit')}</strong><span>{t('infra.ratelimitDesc')}</span></div>
    </section>
  </div>
}
