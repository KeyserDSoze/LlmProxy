import { FormEvent, useState } from 'react'
import { api } from './api'
import { useT } from './uiPreferences'
import type { Deployment, Model, Node } from './types'
import type { TranslationKey } from './uiPreferences'

export type PublicationRuntime = { deploymentId: string; nodeId: string; status: string }
export function runtimeStatusKey(status?: string): TranslationKey {
  switch (status) {
    case 'running': return 'publishing.running'
    case 'stopped': return 'publishing.stopped'
    case 'error': return 'publishing.error'
    case 'installing': return 'publishing.installing'
    case 'starting': return 'publishing.starting'
    case 'missing': return 'publishing.missing'
    case 'unavailable': return 'publishing.unavailable'
    case 'external': return 'publishing.externalState'
    default: return 'publishing.unknown'
  }
}

/** Rename the existing identity, never create a second, disconnected alias for an installed model. */
export default function PublicationEditor({ models, deployments, nodes, statuses, initialId, onSaved, onCancel }: {
  models: Model[]; deployments: Deployment[]; nodes: Node[]; statuses: PublicationRuntime[]; initialId?: string
  onSaved: () => Promise<void>; onCancel: () => void
}) {
  const t = useT()
  const [modelId, setModelId] = useState(initialId ?? models[0]?.id ?? '')
  const selected = models.find(model => model.id === modelId)
  const [name, setName] = useState(selected?.publicName ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const targets = deployments.filter(item => item.modelId === modelId)
  const [activate, setActivate] = useState(false)
  const runtimeText = (deployment: Deployment) => {
    const status = statuses.find(item => item.deploymentId === deployment.id)?.status
    return t(runtimeStatusKey(status))
  }
  async function save(event: FormEvent) {
    event.preventDefault()
    if (!selected) return
    setBusy(true); setError(null)
    try {
      if (name.trim() !== selected.publicName) await api.renameModel(selected.id, name.trim())
      if (activate) for (const deployment of targets.filter(item => item.managedInstallationId && !item.enabled && statuses.some(status => status.deploymentId === item.id && status.status === 'running')))
        await api.activateManagedDeployment(deployment.id)
      await onSaved()
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)) }
    finally { setBusy(false) }
  }
  const canActivate = targets.some(item => item.managedInstallationId && !item.enabled && statuses.some(status => status.deploymentId === item.id && status.status === 'running'))
  return <form className="formPanel" onSubmit={save}>
    {error && <div className="error" role="alert">{error}</div>}
    <label>{t('publishing.choose')}<select value={modelId} required onChange={event => {
      const id = event.target.value
      setModelId(id); setName(models.find(model => model.id === id)?.publicName ?? ''); setActivate(false)
    }}>
      {models.map(model => <option key={model.id} value={model.id}>{model.publicName} — {deployments.filter(item => item.modelId === model.id).map(item =>
        `${nodes.find(node => node.id === item.nodeId)?.name ?? item.nodeId}: ${runtimeText(item)}`).join(' · ') || t('publishing.noServer')}</option>)}
    </select></label>
    <label>{t('publishing.publicName')}<input value={name} maxLength={160} required onChange={event => setName(event.target.value)} /></label>
    <p className="muted">{t('publishing.renameHint')}</p>
    {selected && <div className="publicationTarget"><span className="muted">{t('publishing.runtime')}</span><code>{selected.providerModelName}</code>
      {targets.map(item => <div key={item.id}><strong>{nodes.find(node => node.id === item.nodeId)?.name}</strong> · {runtimeText(item)} · {item.enabled ? t('publishing.apiOn') : t('publishing.apiOff')}</div>)}
    </div>}
    {canActivate && <label className="inlineCheck"><input type="checkbox" checked={activate} onChange={event => setActivate(event.target.checked)} />{t('publishing.checkAlso')}</label>}
    <div className="notice">{t('publishing.renameWarning')}</div>
    <div className="modalActions"><button type="button" className="secondary" onClick={onCancel}>{t('publishing.cancel')}</button><button className="primary" disabled={busy || !selected}>{t('publishing.save')}</button></div>
  </form>
}
