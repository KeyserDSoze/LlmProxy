import { useRouteTab } from './adminNavigation'
import EnginesExperience from './EnginesExperience'
import HardwareExperience from './HardwareExperience'
import ModelHardwareExperience from './ModelHardwareExperience'
import NodesPage from './NodesPage'
import { AdminLink, Tabs } from './UiPrimitives'
import { useT } from './uiPreferences'
import type { Deployment, Model, Node, NodeHardwareMetricsSnapshot } from './types'

type Section = 'fleet' | 'hardware' | 'engines' | 'capacity'

/** Servers: pairing/agents, per-server hardware and fleet capacity. Model lifecycle lives in Models. */
export default function InfrastructureExperience({ nodes, deployments, hardware, canWrite, refresh }: {
  nodes: Node[]
  models: Model[]
  deployments: Deployment[]
  hardware: NodeHardwareMetricsSnapshot[]
  canWrite: boolean
  refresh: () => Promise<void>
}) {
  const t = useT()
  const [section, setSection] = useRouteTab<Section>('section', ['fleet', 'hardware', 'engines', 'capacity'], 'fleet')
  const healthy = nodes.filter(node => node.status === 'Healthy').length
  const allHealthy = nodes.length > 0 && healthy === nodes.length

  return <div className="stack compactPage">
    <div className="workspaceSummary" role="group" aria-label={t('infra.header')}>
      <span className={'pill ' + (allHealthy ? 'pill-success' : nodes.length === 0 ? 'pill-neutral' : 'pill-warning')}>
        <i aria-hidden="true" />{healthy}/{nodes.length} {t('infra.servers')}</span>
      <span className="muted">{deployments.filter(item => item.enabled).length} {t('infra.enabled')}</span>
      <span className="workspaceSummarySpacer" />
      <AdminLink className="buttonLink secondary" href="/admin/models">{t('infra.browse')} →</AdminLink>
    </div>

    <Tabs value={section} onChange={setSection} items={[
      { value: 'fleet', label: t('infra.fleet'), count: nodes.length },
      { value: 'hardware', label: t('infra.hardware') },
      { value: 'engines', label: t('infra.engines') },
      { value: 'capacity', label: t('infra.capacity'), count: nodes.length }
    ]} />

    {section === 'fleet' && <NodesPage nodes={nodes} canWrite={canWrite} refresh={refresh} embedded />}
    {section === 'hardware' && <ModelHardwareExperience nodes={nodes} canWrite={canWrite} refresh={refresh}
      mode="hardware" onRecoverAgent={() => setSection('fleet')} />}
    {section === 'engines' && <EnginesExperience nodes={nodes} canWrite={canWrite} />}
    {section === 'capacity' && <HardwareExperience nodes={nodes} hardware={hardware} canWrite={canWrite} refresh={refresh} embedded />}

    <section className="panel infrastructureGuide">
      <div><strong>{t('infra.people')}</strong><span>{t('infra.peopleDesc')}</span></div>
      <div><strong>{t('infra.concurrency')}</strong><span>{t('infra.concurrencyDesc')}</span></div>
      <div><strong>{t('infra.ratelimit')}</strong><span>{t('infra.ratelimitDesc')}</span></div>
    </section>
  </div>
}
