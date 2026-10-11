import { FormEvent, useMemo, useState } from 'react'
import { api } from './api'
import type { Deployment, Model, Node } from './types'

/**
 * System One is a typed classifier API surface, not a supported generic
 * Hugging Face model runtime. This is deliberately a CONNECTION workflow,
 * never a fake model download/install action.
 */
export default function SystemOneExperience({ models, deployments, nodes, canWrite, refresh }: {
  models: Model[]
  deployments: Deployment[]
  nodes: Node[]
  canWrite: boolean
  refresh: () => Promise<void>
}) {
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
        urlError = 'Use an HTTP(S) service root without username, password, query or fragment.'
      if (url.pathname.endsWith('/v1/systemone'))
        urlError = 'Provide the service root only; LLMProxy adds /v1/systemone automatically.'
    } catch { urlError = 'Enter a complete HTTP(S) service root URL.' }
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
      setSuccess(`System One route for "${model.publicName}" created on "${nodeName.get(nodeId) ?? nodeId}". Check actual classifier responses in the Playground before production use.`)
      setMode('existing'); setExistingId(model.id); setRuntimeRoot('')
      await refresh()
    } catch (reason) {
      setError((reason instanceof Error ? reason.message : String(reason)) +
        (model ? ' The logical model exists, but the deployment is not confirmed. Retry the connection, or select this existing classifier after refreshing.' :
          ' No deployment was created. Please correct the model or server configuration.'))
    } finally { setBusy(false) }
  }

  const activeRoutes = deployments.filter(deployment => deployment.enabled &&
    classifiers.some(model => model.id === deployment.modelId))

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>System One classifiers</h2>
        <p>Connect a classifier service and publish it through the typed <code>POST /v1/systemone</code> API. These workloads use the same nodes, routing policies and capacity admission as Chat models.</p>
      </div>
      <div className="capacityLegend">
        <strong>{classifiers.length} classifier{classifiers.length === 1 ? '' : 's'}</strong>
        <span>{activeRoutes.length} routes enabled</span>
      </div>
    </section>

    <section className="panel">
      <div className="panelTitle"><div><h2>What can LLMProxy do here?</h2>
        <span>Choose the right operation before touching a server.</span></div></div>
      <div className="uxPathGrid">
        <div className="uxTask"><strong>1 · Have a System One service running</strong>
          <p>This API surface is a typed classifier protocol, not an inference engine. The current Node Agent does not install or start a System One runtime or download Laya automatically.</p>
          <span className="muted">The service must implement <code>POST /v1/systemone</code>.</span>
        </div>
        <div className="uxTask"><strong>2 · Connect it to hardware</strong>
          <p>Choose the registered node that hosts/reaches the classifier and provide its runtime service root. LLMProxy will route only to this deployment.</p>
          <a href="/admin/infrastructure?section=fleet">Set up hardware / agent →</a>
        </div>
        <div className="uxTask"><strong>3 · Validate and enable traffic</strong>
          <p>Publish the logical alias, then test the actual classifier response. Configure routing, authentication and capacity before giving clients the API endpoint.</p>
          <a href="/admin/playground?tab=classifier">Open classifier Playground →</a>
        </div>
      </div>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle"><div><h2>Connect a System One runtime</h2>
        <span>Guided setup · publishes a logical model and creates a physical routing deployment.</span></div></div>
      {success && <div className="notice" role="status">{success}</div>}
      {error && <div className="error" role="alert">{error}</div>}
      {activeNodes.length === 0 && <div className="notice">No enabled nodes. <a href="/admin/infrastructure?section=fleet">Register/enable hardware first →</a></div>}
      <form onSubmit={event => void connect(event)}>
        <div className="uxStep"><strong>Step 1 — Classifier identity</strong>
          <label>Model registration
            <select aria-label="System One model registration" value={mode} onChange={event => {
              setMode(event.target.value as 'create' | 'existing'); setPendingModel(null); setError(null)
            }}>
              <option value="create">Publish a new System One alias</option>
              <option value="existing" disabled={classifiers.length === 0}>Add a deployment to an existing classifier</option>
            </select>
          </label>
          {mode === 'create' ? <>
            <label>Logical model name (seen by API clients)
              <input aria-label="System One logical name" value={logicalName}
                onChange={event => setLogicalName(event.target.value)} required
                disabled={!!pendingModel} placeholder="systemone-laya" />
            </label>
            <label>Upstream model identity (metadata only)
              <input aria-label="System One provider name" value={providerName}
                onChange={event => setProviderName(event.target.value)} required
                disabled={!!pendingModel} placeholder="convaiinnovations/laya" />
            </label>
            {duplicate && <div className="notice">This alias already exists. Select "Add a deployment to an existing classifier" instead.</div>}
            {pendingModel && <div className="notice">Alias published. Connection is pending; you can retry without creating another model.</div>}
          </> : <label>Existing System One model
            <select aria-label="Existing System One model" value={existingId}
              onChange={event => setExistingId(event.target.value)} required>
              <option value="">Select classifier</option>
              {classifiers.map(model => <option value={model.id} key={model.id}>{model.publicName}</option>)}
            </select>
          </label>}
        </div>

        <div className="uxStep"><strong>Step 2 — Runtime location</strong>
          <label>Hardware node
            <select aria-label="System One target node" value={nodeId} onChange={event => setNodeId(event.target.value)} required>
              <option value="">Choose an enabled node</option>
              {activeNodes.map(node => <option key={node.id} value={node.id}>{node.name} ({node.status})</option>)}
            </select>
          </label>
          <label>Classifier service root
            <input aria-label="System One service root" value={runtimeRoot}
              onChange={event => setRuntimeRoot(event.target.value)} type="url"
              placeholder="http://10.0.0.21:8002" required />
          </label>
          {urlError && <div className="error">{urlError}</div>}
          <p className="muted">This address must be reachable by LLMProxy (or through a supported node relay). Do not append <code>/v1/systemone</code>; the gateway adds that path. Creating a deployment does not install software on the node.</p>
          {alreadyOnNode && <div className="notice">This logical model already has a manual deployment on that node. Open Model routes to manage it.</div>}
          <label>Maximum concurrent classifier requests
            <input aria-label="System One concurrency" type="number" min="1" max="10000"
              value={maxConcurrency} onChange={event => setMaxConcurrency(Number(event.target.value))} required />
          </label>
        </div>
        <div className="uxStep"><strong>Step 3 — Publish, then test</strong>
          <p>No files will be downloaded. After creation, routing eligibility still depends on the node and runtime health. Test a real classifier request before treating this as production-ready.</p>
          <div className="actions">
            <button className="primary" disabled={!canSubmit}>{busy ? 'Connecting…' : 'Connect and publish classifier'}</button>
            <a href="/admin/models">Inspect all model routes →</a>
          </div>
        </div>
      </form>
    </section>

    <section className="panel">
      <div className="panelTitle"><div><h2>Registered System One models</h2>
        <span>The list is filtered to classifier models. Chat models remain in the Models workspace.</span></div></div>
      <div className="tableScroll"><table><thead><tr>
        <th>Logical model</th><th>Provider identity</th><th>Deployed on</th><th>Routing enabled</th><th>Next step</th>
      </tr></thead><tbody>
        {classifiers.map(model => {
          const routes = deployments.filter(dep => dep.modelId === model.id)
          return <tr key={model.id}>
            <td><strong>{model.publicName}</strong></td>
            <td className="mono">{model.providerModelName}</td>
            <td>{routes.map(route => <div key={route.id}>
              <strong>{nodeName.get(route.nodeId) ?? route.nodeId}</strong>
              <div className="muted mono">{route.runtimeBaseAddress ?? nodes.find(node => node.id === route.nodeId)?.baseAddress ?? 'No service root'}</div>
            </div>)}
            {routes.length === 0 && <span className="muted">No runtime connected</span>}</td>
            <td>{routes.filter(dep => dep.enabled).length} / {routes.length}</td>
            <td><a href="/admin/models?tab=models">Manage routes →</a></td>
          </tr>
        })}
        {classifiers.length === 0 && <tr><td colSpan={5} className="muted">
          No System One models registered. Use the guided connection above after preparing a compatible classifier runtime.
        </td></tr>}
      </tbody></table></div>
    </section>
  </div>
}
