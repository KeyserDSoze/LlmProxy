import { FormEvent, useMemo, useState } from 'react'
import { api } from './api'
import { Modal, Tabs } from './UiPrimitives'
import type { Deployment, Model, Node } from './types'

export default function ModelsDeployments({ models, deployments, nodes, canWrite, refresh }: { models: Model[]; deployments: Deployment[]; nodes: Node[]; canWrite: boolean; refresh: () => Promise<void> }) {
  const [tab, setTab] = useState<'models' | 'nodes'>('models')
  const [publishOpen, setPublishOpen] = useState(false)
  const [deployModel, setDeployModel] = useState<Model | null>(null)
  const [manageNode, setManageNode] = useState<Node | null>(null)
  const [publicName, setPublicName] = useState('')
  const [providerModelName, setProviderModelName] = useState('')
  const [nodeId, setNodeId] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const deploymentsByModel = useMemo(() => new Map(models.map(model => [model.id, deployments.filter(item => item.modelId === model.id)])), [models, deployments])
  const deploymentsByNode = useMemo(() => new Map(nodes.map(node => [node.id, deployments.filter(item => item.nodeId === node.id)])), [nodes, deployments])
  const nodeNames = useMemo(() => new Map(nodes.map(node => [node.id, node.name])), [nodes])
  const modelNames = useMemo(() => new Map(models.map(model => [model.id, model.publicName])), [models])
  const activeNodes = useMemo(() => nodes.filter(node => node.enabled), [nodes])

  function openDeploy(model: Model) {
    setDeployModel(model)
    const used = new Set((deploymentsByModel.get(model.id) ?? []).map(item => item.nodeId))
    setNodeId(activeNodes.find(node => !used.has(node.id))?.id ?? '')
    setMessage(null); setError(null)
  }

  async function publish(event: FormEvent) {
    event.preventDefault(); setBusy('publish'); setError(null)
    try { await api.createModel({ publicName, providerModelName, supportsStreaming: true, supportsTools: true }); setPublicName(''); setProviderModelName(''); setPublishOpen(false); setMessage('Logical model published.'); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function deploy(event: FormEvent) {
    event.preventDefault(); if (!deployModel || !nodeId) return
    setBusy('deploy'); setError(null)
    try { await api.createDeployment({ nodeId, modelId: deployModel.id, weight: 1 }); setDeployModel(null); setMessage('Deployment created and available to routing.'); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function deployEverywhere(model: Model) {
    const existing = new Set((deploymentsByModel.get(model.id) ?? []).map(item => item.nodeId))
    const targets = activeNodes.filter(node => !existing.has(node.id))
    if (targets.length === 0) { setMessage(`${model.publicName} is already deployed on every active node.`); return }
    setBusy(`all:${model.id}`); setError(null)
    try { for (const node of targets) await api.createDeployment({ nodeId: node.id, modelId: model.id, weight: 1 }); setMessage(`${model.publicName} deployed on ${targets.length} additional active node${targets.length === 1 ? '' : 's'}.`); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function toggleDeployment(deployment: Deployment, enabled: boolean) {
    setBusy(`deployment:${deployment.id}`); setError(null)
    try { await api.updateDeployment(deployment.id, { weight: deployment.weight, maxConcurrency: deployment.maxConcurrency ?? null, enabled }); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function deployOnManagedNode(model: Model) {
    if (!manageNode) return
    setBusy(`node:${model.id}`); setError(null)
    try { await api.createDeployment({ nodeId: manageNode.id, modelId: model.id, weight: 1 }); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  return <div className="stack compactPage">
    <section className="panel pageToolbar modelExplainer">
      <div><h2>Models & deployments</h2><p><strong>Logical models</strong> are stable client-facing names such as <span className="mono">agic-code-fast</span>. They hide provider/runtime identifiers and can point to the same model deployed on one or many inference nodes.</p></div>
      {canWrite && <button className="primary" onClick={() => setPublishOpen(true)}>Publish logical model</button>}
    </section>
    {message && <div className="notice">{message}</div>}{error && <div className="error">{error}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'models', label: 'By model', count: models.length }, { value: 'nodes', label: 'By node', count: nodes.length }]} />

    {tab === 'models' && <section className="panel">
      <div className="panelTitle"><div><h2>Logical models</h2><span>See where each client-facing model is deployed and expand it to more nodes.</span></div></div>
      <div className="tableScroll"><table><thead><tr><th>Logical model</th><th>Provider model</th><th>Capabilities</th><th>Deployed on</th><th>Actions</th></tr></thead><tbody>
        {models.map(model => {
          const modelDeployments = deploymentsByModel.get(model.id) ?? []
          const enabledCount = modelDeployments.filter(item => item.enabled).length
          return <tr key={model.id}><td><strong>{model.publicName}</strong><div className="muted">Client sends this name in the OpenAI-compatible request.</div></td><td className="mono">{model.providerModelName}</td><td>{model.supportsStreaming ? 'stream ' : ''}{model.supportsTools ? 'tools' : ''}</td><td><div className="chipList">{modelDeployments.map(item => <span key={item.id} className={item.enabled ? 'chip' : 'chip mutedChip'}>{nodeNames.get(item.nodeId) ?? item.nodeId}{item.enabled ? '' : ' · off'}</span>)}</div>{modelDeployments.length === 0 && <span className="muted">Not deployed</span>}<div className="muted">{enabledCount} routing-enabled</div></td><td className="actions">{canWrite && <button onClick={() => openDeploy(model)}>Deploy…</button>}{canWrite && <button disabled={busy === `all:${model.id}`} onClick={() => void deployEverywhere(model)}>{busy === `all:${model.id}` ? 'Deploying…' : 'Deploy to all active'}</button>}</td></tr>
        })}
        {models.length === 0 && <tr><td colSpan={5} className="muted">No logical models published yet.</td></tr>}
      </tbody></table></div>
    </section>}

    {tab === 'nodes' && <section className="panel">
      <div className="panelTitle"><div><h2>Node deployment view</h2><span>Start from hardware and see exactly which logical models participate in routing there.</span></div></div>
      <div className="tableScroll"><table><thead><tr><th>Node</th><th>Status</th><th>Models</th><th>Routing deployments</th><th>Action</th></tr></thead><tbody>
        {nodes.map(node => {
          const nodeDeployments = deploymentsByNode.get(node.id) ?? []
          return <tr key={node.id}><td><strong>{node.name}</strong><div className="muted mono">{node.baseAddress}</div></td><td><Status value={node.status} /></td><td><div className="chipList">{nodeDeployments.map(item => <span key={item.id} className={item.enabled ? 'chip' : 'chip mutedChip'}>{modelNames.get(item.modelId) ?? item.modelId}</span>)}</div>{nodeDeployments.length === 0 && <span className="muted">No models</span>}</td><td>{nodeDeployments.filter(item => item.enabled).length} enabled / {nodeDeployments.length} total</td><td>{canWrite && <button onClick={() => setManageNode(node)}>Manage models</button>}</td></tr>
        })}
      </tbody></table></div>
    </section>}

    <Modal open={publishOpen} title="Publish logical model" description="Create the stable name clients use. Deployment to physical nodes is managed separately." onClose={() => setPublishOpen(false)}>
      <form className="formPanel" onSubmit={publish}><label>Logical name<input value={publicName} onChange={event => setPublicName(event.target.value)} required placeholder="agic-code-fast" /></label><label>Provider model name<input value={providerModelName} onChange={event => setProviderModelName(event.target.value)} required placeholder="Qwen/..." /></label><div className="modalActions"><button type="button" className="secondary" onClick={() => setPublishOpen(false)}>Cancel</button><button className="primary" disabled={busy === 'publish'}>Publish model</button></div></form>
    </Modal>

    <Modal open={Boolean(deployModel)} title={`Deploy · ${deployModel?.publicName ?? ''}`} description="Create a routing deployment on another active node." onClose={() => setDeployModel(null)}>
      <form className="formPanel" onSubmit={deploy}><label>Node<select value={nodeId} onChange={event => setNodeId(event.target.value)} required><option value="">Select node</option>{activeNodes.filter(node => !(deploymentsByModel.get(deployModel?.id ?? '') ?? []).some(item => item.nodeId === node.id)).map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label><div className="modalActions"><button type="button" className="secondary" onClick={() => setDeployModel(null)}>Cancel</button><button className="primary" disabled={!nodeId || busy === 'deploy'}>Create deployment</button></div></form>
    </Modal>

    <Modal open={Boolean(manageNode)} title={`Models on ${manageNode?.name ?? ''}`} description="Enable or disable routing for existing deployments, or deploy another logical model on this node." onClose={() => setManageNode(null)}>
      <div className="modalList">{models.map(model => {
        const deployment = (deploymentsByNode.get(manageNode?.id ?? '') ?? []).find(item => item.modelId === model.id)
        return <div className="modalListRow" key={model.id}><div><strong>{model.publicName}</strong><div className="muted mono">{model.providerModelName}</div></div><div className="actions">{deployment ? <><span className={deployment.enabled ? 'scopeBadge scope-org' : 'scopeBadge'}>{deployment.enabled ? 'Routing on' : 'Routing off'}</span><button disabled={busy === `deployment:${deployment.id}`} onClick={() => void toggleDeployment(deployment, !deployment.enabled)}>{deployment.enabled ? 'Disable' : 'Enable'}</button></> : <button className="primary" disabled={!manageNode?.enabled || busy === `node:${model.id}`} onClick={() => void deployOnManagedNode(model)}>Deploy</button>}</div></div>
      })}</div>
    </Modal>
  </div>
}

function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase()}`}><i />{value}</span> }
