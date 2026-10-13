import { FormEvent, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { useT } from './uiPreferences'
import type { Deployment, Model, Node } from './types'

/**
 * System One is a typed classifier API surface, not a supported generic
 * Hugging Face model runtime. This advanced page connects EXTERNAL
 * services; managed Laya installation happens directly in the catalog.
 */
export default function SystemOneExperience({ models, deployments, nodes, canWrite, refresh }: {
  models: Model[]
  deployments: Deployment[]
  nodes: Node[]
  canWrite: boolean
  refresh: () => Promise<void>
}) {
  const t = useT()
  const classifiers = useMemo(() => models.filter(model => model.surface === 'SystemOne'), [models])
  const activeNodes = useMemo(() => nodes.filter(node => node.enabled), [nodes])
  const nodeName = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const [mode, setMode] = useState<'create' | 'existing'>('create')
  const [existingId, setExistingId] = useState('')
  const [logicalName, setLogicalName] = useState('')
  const [providerName, setProviderName] = useState('')
  const [nodeId, setNodeId] = useState('')
  const [runtimeRoot, setRuntimeRoot] = useState('')
  const [maxConcurrency, setMaxConcurrency] = useState(1)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)
  const [defaultState, setDefaultState] = useState<{
    configuredModel: string | null; effectiveModel: string | null; options: string[]
  } | null>(null)
  const [defaultChoice, setDefaultChoice] = useState('')
  const [defaultBusy, setDefaultBusy] = useState(false)
  const [defaultError, setDefaultError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    void api.getSystemOneDefault().then(value => {
      if (!active) return
      setDefaultState(value)
      setDefaultChoice(value.configuredModel ?? '')
    }).catch(reason => {
      if (active) setDefaultError(reason instanceof Error ? reason.message : String(reason))
    })
    return () => { active = false }
  }, [])

  async function saveDefault(event: FormEvent) {
    event.preventDefault()
    if (!canWrite || defaultBusy) return
    setDefaultBusy(true); setDefaultError(null)
    try {
      const value = await api.setSystemOneDefault(defaultChoice || null)
      setDefaultState(value)
      setDefaultChoice(value.configuredModel ?? '')
    } catch (reason) {
      setDefaultError(reason instanceof Error ? reason.message : String(reason))
    } finally { setDefaultBusy(false) }
  }
  // A failed second request must not duplicate an already-created logical model.
  const [pendingModel, setPendingModel] = useState<Model | null>(null)

  const target = mode === 'existing' ? classifiers.find(model => model.id === existingId) : pendingModel
  const duplicate = mode === 'create' && !pendingModel &&
    models.some(model => model.publicName.toLowerCase() === logicalName.trim().toLowerCase())
  const alreadyOnNode = target && deployments.some(deployment =>
    deployment.modelId === target.id && deployment.nodeId === nodeId)
  let urlError: string | null = null
  if (runtimeRoot.trim()) {
    try {
      const url = new URL(runtimeRoot.trim())
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password ||
          url.search || url.hash)
        urlError = t('systemOne.urlInvalid')
      if (url.pathname.endsWith('/v1/systemone'))
        urlError = t('systemOne.urlPath')
    } catch { urlError = t('systemOne.urlInvalid') }
  }
  const canSubmit = canWrite && !busy && !!nodeId && !!runtimeRoot.trim() &&
    !urlError && !duplicate && !alreadyOnNode &&
    (mode === 'existing' ? !!target : !!pendingModel ||
      (!!logicalName.trim() && !!providerName.trim()))

  async function connect(event: FormEvent) {
    event.preventDefault()
    if (!canSubmit) return
    setBusy(true); setError(null); setSuccess(null)
    let model = target
    try {
      if (!model) {
        model = await api.createModel({
          publicName: logicalName.trim(),
          providerModelName: providerName.trim(),
          surface: 'SystemOne',
          supportsStreaming: false,
          supportsTools: false
        })
        setPendingModel(model)
      }
      await api.createDeployment({
        modelId: model.id, nodeId, runtimeBaseAddress: runtimeRoot.trim(),
        weight: 1, maxConcurrency
      })
      setPendingModel(null)
      setSuccess(`${t('systemOne.successPrefix')} "${model.publicName}" — ${nodeName.get(nodeId) ?? nodeId}. ${t('systemOne.successSuffix')}`)
      setMode('existing'); setExistingId(model.id); setRuntimeRoot('')
      await refresh()
    } catch (reason) {
      setError((reason instanceof Error ? reason.message : String(reason)) +
        (model ? ' ' + t('systemOne.unknown') : ' ' + t('systemOne.createFailed')))
    } finally { setBusy(false) }
  }

  const activeRoutes = deployments.filter(deployment => deployment.enabled &&
    classifiers.some(model => model.id === deployment.modelId))

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>{t('systemOne.title')}</h2>
        <p>{t('systemOne.intro')}</p>
      </div>
      <div className="capacityLegend">
        <strong>{classifiers.length} {t('systemOne.modelName')}</strong>
        <span>{activeRoutes.length} {t('systemOne.routeEnabled')}</span>
      </div>
    </section>

    <section className="panel">
      <div className="panelTitle"><div>
        <h2>{t('systemOne.defaultTitle')}</h2>
        <span>{t('systemOne.defaultHelp')}</span>
      </div></div>
      <form className="formPanel" onSubmit={event => void saveDefault(event)}>
        <label>{t('systemOne.defaultLabel')}
          <select aria-label={t('systemOne.defaultLabel')}
            value={defaultChoice} disabled={!canWrite || defaultBusy || !defaultState}
            onChange={event => setDefaultChoice(event.target.value)}>
            <option value="">{t('systemOne.defaultAutomatic')}</option>
            {defaultState?.options.map(name => <option value={name} key={name}>{name}</option>)}
          </select>
        </label>
        <p className="muted">{t('systemOne.defaultEffective')}: <strong>
          {defaultState?.effectiveModel ?? t('systemOne.none')}
        </strong> · POST /v1/systemone</p>
        {defaultError && <p className="error" role="alert">{defaultError}</p>}
        <div className="actions">
          <button type="submit" className="primary"
            disabled={!canWrite || !defaultState || defaultBusy ||
              defaultChoice === (defaultState?.configuredModel ?? '')}>
            {defaultBusy ? t('systemOne.defaultSaving') : t('systemOne.defaultSave')}
          </button>
        </div>
      </form>
    </section>

    <section className="panel modelExternalInfo">
      <div className="panelTitle"><div>
        <h2>{t('systemOne.externalOnly')}</h2>
        <span>{t('systemOne.externalOnlyDesc')}</span>
      </div></div>
      <p><a href="/admin/catalog?tab=deploy&kind=SystemOne">{t('systemOne.backToCatalog')} →</a></p>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle"><div><h2>{t('systemOne.setup.title')}</h2><span>{t('systemOne.setup.desc')}</span></div></div>
      {success && <div className="notice" role="status">{success}</div>}
      {error && <div className="error" role="alert">{error}</div>}
      {activeNodes.length === 0 && <div className="notice">{t('systemOne.unavailable')} <a href="/admin/infrastructure?section=fleet">{t('systemOne.serverLink')} →</a></div>}
      <form onSubmit={event => void connect(event)}>
        <div className="uxStep"><strong>{t('systemOne.step.identity')}</strong>
          <label>{t('systemOne.mode')}
            <select aria-label={t('systemOne.mode')} value={mode} onChange={event => {
              setMode(event.target.value as 'create' | 'existing'); setPendingModel(null); setError(null)
            }}>
              <option value="create">{t('systemOne.mode.new')}</option>
              <option value="existing" disabled={classifiers.length === 0}>{t('systemOne.mode.existing')}</option>
            </select>
          </label>
          {mode === 'create' ? <>
            <label>{t('systemOne.logical')}
              <input aria-label={t('systemOne.logical')} value={logicalName}
                onChange={event => setLogicalName(event.target.value)} required
                disabled={!!pendingModel} placeholder="systemone-laya" />
            </label>
            <label>{t('systemOne.provider')}
              <input aria-label={t('systemOne.provider')} value={providerName}
                onChange={event => setProviderName(event.target.value)} required
                disabled={!!pendingModel} placeholder="convaiinnovations/laya" />
            </label>
            {duplicate && <div className="notice">{t('systemOne.aliasUsed')}</div>}
            {pendingModel && <div className="notice">{t('systemOne.pending')}</div>}
          </> : <label>{t('systemOne.existing')}
            <select aria-label={t('systemOne.existing')} value={existingId}
              onChange={event => setExistingId(event.target.value)} required>
              <option value="">{t('systemOne.select')}</option>
              {classifiers.map(model => <option value={model.id} key={model.id}>{model.publicName}</option>)}
            </select>
          </label>}
        </div>

        <div className="uxStep"><strong>{t('systemOne.step.location')}</strong>
          <label>{t('systemOne.hardware')}
            <select aria-label={t('systemOne.hardware')} value={nodeId} onChange={event => setNodeId(event.target.value)} required>
              <option value="">{t('systemOne.selectServer')}</option>
              {activeNodes.map(node => <option key={node.id} value={node.id}>{node.name} ({node.status})</option>)}
            </select>
          </label>
          <label>{t('systemOne.root')}
            <input aria-label={t('systemOne.root')} value={runtimeRoot}
              onChange={event => setRuntimeRoot(event.target.value)} type="url"
              placeholder="http://10.0.0.21:8002" required />
          </label>
          {urlError && <div className="error">{urlError}</div>}
          <p className="muted">{t('systemOne.rootHelp')}</p>
          {alreadyOnNode && <div className="notice">{t('systemOne.already')}</div>}
          <label>{t('systemOne.limit')}
            <input aria-label={t('systemOne.limit')} type="number" min="1" max="10000"
              value={maxConcurrency} onChange={event => setMaxConcurrency(Number(event.target.value))} required />
          </label>
        </div>
        <div className="uxStep"><strong>{t('systemOne.step.publish')}</strong>
          <p>{t('systemOne.publishInfo')}</p>
          <div className="actions">
            <button className="primary" disabled={!canSubmit}>{busy ? t('systemOne.submit.busy') : t('systemOne.submit')}</button>
            <a href="/admin/models">{t('systemOne.modelRoutes')} →</a>
          </div>
        </div>
      </form>
    </section>

    <section className="panel">
      <div className="panelTitle"><div><h2>{t('systemOne.registered')}</h2><span>{t('systemOne.results')}</span></div></div>
      <div className="tableScroll"><table><thead><tr>
        <th>{t('systemOne.modelName')}</th><th>{t('systemOne.endpoint')}</th><th>{t('systemOne.nodeName')}</th><th>{t('systemOne.routeEnabled')}</th><th>{t('systemOne.next')}</th>
      </tr></thead><tbody>
        {classifiers.map(model => {
          const routes = deployments.filter(dep => dep.modelId === model.id)
          return <tr key={model.id}>
            <td><strong>{model.publicName}</strong>
              {model.publicName === defaultState?.effectiveModel && <div className="muted">{t('systemOne.defaultActive')}</div>}
            </td>
            <td className="mono">{model.providerModelName}</td>
            <td>{routes.map(route => <div key={route.id}>
              <strong>{nodeName.get(route.nodeId) ?? route.nodeId}</strong>
              <div className="muted mono">{route.runtimeBaseAddress ?? nodes.find(node => node.id === route.nodeId)?.baseAddress ?? t('systemOne.none')}</div>
            </div>)}
            {routes.length === 0 && <span className="muted">{t('systemOne.none')}</span>}</td>
            <td>{routes.filter(dep => dep.enabled).length} / {routes.length}</td>
            <td><a href="/admin/models?tab=models">{t('systemOne.modelRoutes')} →</a></td>
          </tr>
        })}
        {classifiers.length === 0 && <tr><td colSpan={5} className="muted">
          {t('systemOne.empty')}
        </td></tr>}
      </tbody></table></div>
    </section>
  </div>
}
