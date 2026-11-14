import { FormEvent, useMemo, useState } from 'react'
import { api } from './api'
import { useRouteTab } from './adminNavigation'
import { Modal, Tabs } from './UiPrimitives'
import { isRoutable } from './routableModels'
import type { Deployment, Model, Node } from './types'

type Surface = 'OpenAi' | 'SystemOne' | 'ImageGeneration' | 'SpeechSynthesis' | 'AudioTranscription' | 'Embedding' | 'Realtime'

const surfaceBadge: Record<Surface, string> = {
  OpenAi: 'CHAT', SystemOne: 'SYSTEM ONE', ImageGeneration: 'IMAGE', SpeechSynthesis: 'TTS', AudioTranscription: 'STT', Embedding: 'EMBEDDING', Realtime: 'VOICE'
}
const surfaceFlow: Record<Surface, string> = {
  OpenAi: '', SystemOne: 'typed classifier', ImageGeneration: 'text → image', SpeechSynthesis: 'text → audio',
  AudioTranscription: 'audio → text', Embedding: 'text → vectors', Realtime: 'audio ⇄ audio (WebSocket)'
}
const surfaceEndpoint: Record<Surface, string> = {
  OpenAi: '/v1/chat/completions', SystemOne: '/v1/systemone', ImageGeneration: '/v1/images/generations', SpeechSynthesis: '/v1/audio/speech',
  AudioTranscription: '/v1/audio/transcriptions', Embedding: '/v1/embeddings', Realtime: '/v1/realtime'
}

/** `kind` limits the page to one workspace (text, images, voice, embeddings...) and preselects it when publishing. */
export default function ModelsDeployments({ models: allModels, deployments: allDeployments, nodes, canWrite, refresh, embedded = false, kind: scope }: { models: Model[]; deployments: Deployment[]; nodes: Node[]; canWrite: boolean; refresh: () => Promise<void>; embedded?: boolean; kind?: Surface }) {
  const [showAllTypes, setShowAllTypes] = useState(false)
  const effectiveScope = showAllTypes ? undefined : scope
  // Models that are registered but not running are hidden unless asked for: they cannot answer anything.
  const [showInactive, setShowInactive] = useState(false)
  const scopedModels = useMemo(() => effectiveScope ? allModels.filter(model => (model.surface ?? 'OpenAi') === effectiveScope) : allModels, [allModels, effectiveScope])
  const inactiveCount = useMemo(() => scopedModels.filter(model => !isRoutable(model, allDeployments, nodes)).length, [scopedModels, allDeployments, nodes])
  const models = useMemo(() => showInactive ? scopedModels : scopedModels.filter(model => isRoutable(model, allDeployments, nodes)), [scopedModels, showInactive, allDeployments, nodes])
  const deployments = useMemo(() => effectiveScope ? allDeployments.filter(item => scopedModels.some(model => model.id === item.modelId)) : allDeployments, [allDeployments, scopedModels, effectiveScope])
  const [tab, setTab] = useRouteTab<'models' | 'nodes'>(embedded ? 'advancedTab' : 'tab', ['models', 'nodes'], 'models')
  const [publishOpen, setPublishOpen] = useState(false)
  const [deployModel, setDeployModel] = useState<Model | null>(null)
  const [manageNode, setManageNode] = useState<Node | null>(null)
  const [settingsDeployment, setSettingsDeployment] = useState<Deployment | null>(null)
  const [settingsWeight, setSettingsWeight] = useState(1)
  const [settingsMaxConcurrency, setSettingsMaxConcurrency] = useState('')
  const [publicName, setPublicName] = useState('')
  const [providerModelName, setProviderModelName] = useState('')
  const [surface, setSurface] = useState<Surface>(scope ?? 'OpenAi')
  const [nodeId, setNodeId] = useState('')
  const [runtimeBaseAddress, setRuntimeBaseAddress] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const deploymentsByModel = useMemo(() => new Map(scopedModels.map(model => [model.id, deployments.filter(item => item.modelId === model.id)])), [scopedModels, deployments])
  const deploymentsByNode = useMemo(() => new Map(nodes.map(node => [node.id, deployments.filter(item => item.nodeId === node.id)])), [nodes, deployments])
  const nodeNames = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const modelById = useMemo(() => new Map(scopedModels.map(model => [model.id, model])), [scopedModels])
  const nodeById = useMemo(() => new Map(nodes.map(node => [node.id, node])), [nodes])
  const activeNodes = useMemo(() => nodes.filter(node => node.enabled), [nodes])

  function modelSurface(model: Model): Surface { return model.surface ?? 'OpenAi' }

  function openDeploy(model: Model) {
    setDeployModel(model)
    const used = new Set((deploymentsByModel.get(model.id) ?? []).map(item => item.nodeId))
    setNodeId(activeNodes.find(node => !used.has(node.id))?.id ?? '')
    setRuntimeBaseAddress('')
    setMessage(null); setError(null)
  }

  async function publish(event: FormEvent) {
    event.preventDefault()
    setBusy('publish'); setError(null)
    try {
      await api.createModel({
        publicName,
        providerModelName,
        supportsStreaming: surface === 'OpenAi',
        supportsTools: surface === 'OpenAi',
        surface
      })
      setPublicName(''); setProviderModelName(''); setSurface('OpenAi'); setPublishOpen(false)
      setMessage(`${surface === 'SystemOne' ? 'System One' : surface === 'ImageGeneration' ? 'Image' : surface === 'SpeechSynthesis' ? 'TTS' : surface === 'AudioTranscription' ? 'STT' : 'OpenAI Chat'} logical model published.`)
      await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  async function deploy(event: FormEvent) {
    event.preventDefault(); if (!deployModel || !nodeId) return
    if (modelSurface(deployModel) !== 'OpenAi' && !runtimeBaseAddress.trim()) {
      setError('A non-Chat workload needs an explicit OpenAI-compatible runtime service root on this server.')
      return
    }
    setBusy('deploy'); setError(null)
    try { await api.createDeployment({ nodeId, modelId: deployModel.id, weight: 1, runtimeBaseAddress: runtimeBaseAddress.trim() || null }); setDeployModel(null); setMessage('Deployment created and available to routing.'); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function deployEverywhere(model: Model) {
    const existing = new Set((deploymentsByModel.get(model.id) ?? []).map(item => item.nodeId))
    if (modelSurface(model) !== 'OpenAi') {
      setError('Images, audio and classifier deployments require a known runtime service root on each target. Configure every node explicitly.')
      return
    }
    const targets = activeNodes.filter(node => !existing.has(node.id))
    if (targets.length === 0) { setMessage(`${model.publicName} is already deployed on every active node.`); return }
    setBusy(`all:${model.id}`); setError(null)
    try {
      for (const node of targets) await api.createDeployment({ nodeId: node.id, modelId: model.id, weight: 1 })
      setMessage(`${model.publicName} deployed on ${targets.length} additional active node${targets.length === 1 ? '' : 's'}.`)
      await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function toggleDeployment(deployment: Deployment, enabled: boolean) {
    setBusy(`deployment:${deployment.id}`); setError(null)
    try { await api.updateDeployment(deployment.id, { weight: deployment.weight, maxConcurrency: deployment.maxConcurrency ?? null, enabled }); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  function openDeploymentSettings(deployment: Deployment) {
    setSettingsDeployment(deployment); setSettingsWeight(deployment.weight); setSettingsMaxConcurrency(deployment.maxConcurrency == null ? '' : String(deployment.maxConcurrency)); setError(null)
  }

  async function saveDeploymentSettings(event: FormEvent) {
    event.preventDefault(); if (!settingsDeployment) return
    const parsed = settingsMaxConcurrency.trim() === '' ? null : Number(settingsMaxConcurrency)
    setBusy(`settings:${settingsDeployment.id}`); setError(null)
    try {
      await api.updateDeployment(settingsDeployment.id, { weight: settingsWeight, maxConcurrency: parsed, enabled: settingsDeployment.enabled })
      setSettingsDeployment(null); setMessage('Deployment capacity updated.'); await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function deployOnManagedNode(model: Model) {
    if (!manageNode) return
    if (modelSurface(model) !== 'OpenAi') {
      setError('Images, audio and classifiers require an explicit runtime root. Open the model deployment form.')
      setManageNode(null)
      openDeploy(model)
      return
    }
    setBusy(`node:${model.id}`); setError(null)
    try { await api.createDeployment({ nodeId: manageNode.id, modelId: model.id, weight: 1 }); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  return <div className="stack compactPage">
    <section className="panel pageToolbar modelExplainer">
      <div><h2>Published models & routing</h2><p>These are the names your clients call. <strong>You normally do not publish anything by hand:</strong> installing a model under <em>Discover &amp; install</em> and starting it publishes it under the public name you chose, and switches it on once its health check passes. Use this page to connect a runtime you run yourself, to put a model on another server, to switch a model on or off, or to cap its capacity.</p><div className="actions"><a href="/admin/catalog?tab=deploy">Install an LLM on hardware →</a><a href="/admin/models?kind=SystemOne&panel=setup">Connect a System One classifier →</a></div></div>
      {canWrite && <button className="primary" onClick={() => setPublishOpen(true)}>Publish logical model</button>}
    </section>
    {message && <div className="notice">{message}</div>}{error && <div className="error" role="alert">{error}</div>}
    {scope && <label className="inlineCheck"><input type="checkbox" checked={showAllTypes} onChange={event => setShowAllTypes(event.target.checked)} /> Show models of every type (text, images, voice, embeddings, System One)</label>}
    {inactiveCount > 0 || showInactive ? <label className="inlineCheck"><input type="checkbox" checked={showInactive} onChange={event => setShowInactive(event.target.checked)} /> Show models that are not running ({inactiveCount})</label> : null}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'models', label: 'By model', count: models.length }, { value: 'nodes', label: 'By node', count: nodes.length }]} />

    {tab === 'models' && <section className="panel">
      <div className="panelTitle"><div><h2>Logical models</h2><span>See surface, runtime identity, deployment placement and routing state together.</span></div></div>
      <div className="tableScroll"><table><thead><tr><th>Logical model</th><th>Surface</th><th>Provider/runtime</th><th>Capabilities</th><th>Deployed on</th><th>Actions</th></tr></thead><tbody>
        {models.map(model => {
          const modelDeployments = deploymentsByModel.get(model.id) ?? []
          const enabledCount = modelDeployments.filter(item => item.enabled).length
          const kind = modelSurface(model)
          return <tr key={model.id}>
            <td><strong>{model.publicName}</strong><div className="muted">{kind === 'SystemOne' ? 'Selected by X-LlmProxy-Model (or default).' : kind === 'OpenAi' ? 'Client-facing OpenAI Chat/Responses model name.' : `Use this public model name on ${surfaceEndpoint[kind]}.`}</div></td>
            <td><span className={kind === 'SystemOne' ? 'scopeBadge scope-personal' : 'scopeBadge scope-org'}>{surfaceBadge[kind]}</span></td>
            <td className="mono">{model.providerModelName}</td>
            <td>{kind === 'OpenAi' ? [model.supportsStreaming ? 'stream' : '', model.supportsTools ? 'tools' : ''].filter(Boolean).join(' ') || 'basic' : surfaceFlow[kind]}</td>
            <td><div className="chipList">{modelDeployments.map(item => <span key={item.id} className={item.enabled ? 'chip' : 'chip mutedChip'}>{nodeNames.get(item.nodeId) ?? item.nodeId} · max {item.maxConcurrency ?? nodeById.get(item.nodeId)?.maxConcurrency ?? '—'}{item.enabled ? '' : ' · off'}</span>)}</div>{modelDeployments.length === 0 && <span className="muted">Not deployed</span>}<div className="muted">{enabledCount} routing-enabled</div></td>
            <td className="actions">{canWrite && <button onClick={() => openDeploy(model)}>Deploy…</button>}{canWrite && kind === 'OpenAi' && <button disabled={busy === `all:${model.id}`} onClick={() => void deployEverywhere(model)}>{busy === `all:${model.id}` ? 'Deploying…' : 'Deploy to all active'}</button>}
              {canWrite && kind === 'SystemOne' && <a href="/admin/models?kind=SystemOne&panel=setup">Connect classifier →</a>}</td>
          </tr>
        })}
        {models.length === 0 && <tr><td colSpan={6} className="muted">{inactiveCount > 0 ? `No model is running. ${inactiveCount} registered but not running: tick "Show models that are not running".` : 'No logical models published yet.'}</td></tr>}
      </tbody></table></div>
    </section>}

    {tab === 'nodes' && <section className="panel">
      <div className="panelTitle"><div><h2>Node deployment view</h2><span>Start from hardware and see every OpenAI and System One workload routed through it.</span></div></div>
      <div className="tableScroll"><table><thead><tr><th>Node</th><th>Status</th><th>Models</th><th>Routing deployments</th><th>Action</th></tr></thead><tbody>
        {nodes.map(node => {
          const nodeDeployments = deploymentsByNode.get(node.id) ?? []
          return <tr key={node.id}><td><strong>{node.name}</strong><div className="muted mono">{node.baseAddress}</div></td><td><Status value={node.status} /></td><td><div className="chipList">{nodeDeployments.map(item => {
            const model = modelById.get(item.modelId)
            return <span key={item.id} className={item.enabled ? 'chip' : 'chip mutedChip'}>{model?.publicName ?? item.modelId} · {model && modelSurface(model) === 'SystemOne' ? 'S1' : 'OpenAI'}</span>
          })}</div>{nodeDeployments.length === 0 && <span className="muted">No models</span>}</td><td>{nodeDeployments.filter(item => item.enabled).length} enabled / {nodeDeployments.length} total</td><td>{canWrite && <button onClick={() => setManageNode(node)}>Manage models</button>}</td></tr>
        })}
      </tbody></table></div>
    </section>}

    <Modal open={publishOpen} title="Publish logical model" description="Choose the API surface first; routing and deployment controls are shared." onClose={() => setPublishOpen(false)}>
      <form className="formPanel" onSubmit={publish}>
        <label>Surface<select aria-label="Model surface" value={surface} onChange={event => setSurface(event.target.value as Surface)}><option value="OpenAi">OpenAI · chat/responses</option><option value="SystemOne">System One · classifier</option><option value="ImageGeneration">Images · /v1/images/generations</option><option value="SpeechSynthesis">Text to speech · /v1/audio/speech</option><option value="AudioTranscription">Speech to text · /v1/audio/transcriptions</option><option value="Embedding">Embeddings · /v1/embeddings</option><option value="Realtime">Voice (streaming) · /v1/realtime</option></select></label>
        <label>Logical name<input value={publicName} onChange={event => setPublicName(event.target.value)} required placeholder={surface === 'SystemOne' ? 'systemone-laya' : 'agic-code-fast'} /></label>
        <label>Provider / runtime model name<input value={providerModelName} onChange={event => setProviderModelName(event.target.value)} required placeholder={surface === 'SystemOne' ? 'convaiinnovations/laya' : 'Qwen/...'} /></label>
        {surface === 'SystemOne' && <p><a href="/admin/models?kind=SystemOne&panel=setup">Recommended: use the guided System One setup to publish and connect the classifier together →</a></p>}
        {surface !== 'OpenAi' && surface !== 'SystemOne' && <p className="notice">This publishes an API model alias. It does not download or launch an image/audio runtime. Connect an existing runtime that implements the selected OpenAI endpoint and has a healthy /health endpoint. Hardware-specific auto-installation is not available yet.</p>}
        <div className="notice">{surface === 'SystemOne' ? 'System One requests keep their typed payload unchanged. The logical model selects eligible deployments; the provider name is inventory metadata.' : 'Image/audio and Chat requests rewrite the logical model to the provider model name before forwarding; the server must implement that specific API.'}</div>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setPublishOpen(false)}>Cancel</button><button className="primary" disabled={busy === 'publish'}>Publish model</button></div>
      </form>
    </Modal>

    <Modal open={Boolean(deployModel)} title={`Deploy · ${deployModel?.publicName ?? ''}`} description="Create a routing deployment on another active node." onClose={() => setDeployModel(null)}>
      <form className="formPanel" onSubmit={deploy}><label>Node<select value={nodeId} onChange={event => setNodeId(event.target.value)} required><option value="">Select node</option>{activeNodes.filter(node => !(deploymentsByModel.get(deployModel?.id ?? '') ?? []).some(item => item.nodeId === node.id)).map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label><label>Runtime service root {deployModel && modelSurface(deployModel) !== 'OpenAi' ? '(required for this non-Chat runtime)' : '(optional)'}<input aria-label="Runtime service root" value={runtimeBaseAddress} onChange={event => setRuntimeBaseAddress(event.target.value)} placeholder="Use node service root" /></label><div className="muted">Use an override when this model runs on a different port/service root on the same hardware, for example a System One classifier beside an OpenAI runtime.</div><div className="modalActions"><button type="button" className="secondary" onClick={() => setDeployModel(null)}>Cancel</button><button className="primary" disabled={!nodeId || busy === 'deploy' || !!(deployModel && modelSurface(deployModel) !== 'OpenAi' && !runtimeBaseAddress.trim())}>Create deployment</button></div></form>
    </Modal>

    <Modal open={Boolean(manageNode)} title={`Models on ${manageNode?.name ?? ''}`}
      description="A model answers clients only while it is switched on here. Switching it off keeps it installed." onClose={() => setManageNode(null)}>
      {(() => {
        const here = deploymentsByNode.get(manageNode?.id ?? '') ?? []
        const onServer = scopedModels.flatMap(model => { const deployment = here.find(item => item.modelId === model.id); return deployment ? [{ model, deployment }] : [] })
        const others = scopedModels.filter(model => !here.some(item => item.modelId === model.id))
        const kindText = (model: Model) => modelSurface(model) === 'SystemOne' ? 'System One' : surfaceBadge[modelSurface(model)]
        return <div className="deployPanel">
          {onServer.length === 0 && <p className="muted deployEmpty">No model is set up on this server yet.</p>}
          <div className="deployList">{onServer.map(({ model, deployment }) => <div className="deployRow" key={model.id}>
            <div className="deployInfo">
              <strong title={model.publicName}>{model.publicName}</strong>
              <span className="muted deployMeta" title={model.providerModelName}>{kindText(model)} · {model.providerModelName}</span>
              <span className="muted deployMeta">{deployment.maxConcurrency ? `Up to ${deployment.maxConcurrency} requests at once` : "Shares the server's request limit"}</span>
            </div>
            <span className={'pill ' + (deployment.enabled ? 'pill-success' : 'pill-neutral')}><i aria-hidden="true" />{deployment.enabled ? 'Live' : 'Off'}</span>
            <div className="deployActions">
              <button className="secondary" onClick={() => openDeploymentSettings(deployment)}>Capacity</button>
              <button disabled={busy === `deployment:${deployment.id}`} onClick={() => void toggleDeployment(deployment, !deployment.enabled)}>{deployment.enabled ? 'Disable' : 'Enable'}</button>
            </div>
          </div>)}</div>
          {others.length > 0 && <details className="deployMore">
            <summary>Add a published model to this server ({others.length})</summary>
            <div className="deployList">{others.map(model => <div className="deployRow" key={model.id}>
              <div className="deployInfo"><strong title={model.publicName}>{model.publicName}</strong>
                <span className="muted deployMeta" title={model.providerModelName}>{kindText(model)} · {model.providerModelName}</span></div>
              <span />
              <div className="deployActions"><button className="primary" disabled={!manageNode?.enabled || busy === `node:${model.id}`} onClick={() => void deployOnManagedNode(model)}>Deploy</button></div>
            </div>)}</div>
          </details>}
        </div>
      })()}
    </Modal>

    <Modal open={Boolean(settingsDeployment)} title="Deployment capacity" description="Optionally cap this model/runtime below the physical hardware ceiling. Leave it empty to inherit the hardware limit." onClose={() => setSettingsDeployment(null)}>
      <form className="formPanel" onSubmit={saveDeploymentSettings}>
        <label>Routing weight<input type="number" min="1" value={settingsWeight} onChange={event => setSettingsWeight(Number(event.target.value))} required /></label>
        <label>Maximum simultaneous requests<input aria-label="Deployment maximum simultaneous requests" type="number" min="1" value={settingsMaxConcurrency} onChange={event => setSettingsMaxConcurrency(event.target.value)} placeholder="Inherit hardware limit" /></label>
        <div className="secretBox"><strong>Effective ceiling</strong><p>{settingsMaxConcurrency.trim() || (settingsDeployment ? nodeById.get(settingsDeployment.nodeId)?.maxConcurrency : '—')} concurrent requests for this deployment; the physical hardware ceiling still applies across all deployments.</p></div>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setSettingsDeployment(null)}>Cancel</button><button className="primary" disabled={busy === `settings:${settingsDeployment?.id}`}>Save deployment</button></div>
      </form>
    </Modal>
  </div>
}

function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
