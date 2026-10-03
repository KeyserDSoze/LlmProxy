import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { ModelManagementOverview, Node } from './types'

export default function ModelHardware({ nodes, canWrite, refresh }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void> }) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? '')
  const [overview, setOverview] = useState<ModelManagementOverview | null>(null)
  const [managementBaseAddress, setManagementBaseAddress] = useState('')
  const [bearerToken, setBearerToken] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!nodeId && nodes[0]) setNodeId(nodes[0].id)
  }, [nodeId, nodes])

  const load = useCallback(async () => {
    if (!nodeId) {
      setOverview(null)
      return
    }
    setLoading(true)
    setError(null)
    try {
      const next = await api.modelManagementOverview(nodeId)
      setOverview(next)
      setManagementBaseAddress(next.node.managementBaseAddress ?? '')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setLoading(false)
    }
  }, [nodeId])

  useEffect(() => { void load() }, [load])

  const hardware = overview?.hardware
  const gpus = hardware?.gpus ?? []
  const totals = useMemo(() => ({
    gpuMemory: gpus.reduce((sum, gpu) => sum + gpu.memoryTotalGiB, 0),
    gpuFree: gpus.reduce((sum, gpu) => sum + gpu.memoryFreeGiB, 0)
  }), [gpus])

  async function configure(event: FormEvent) {
    event.preventDefault()
    if (!nodeId) return
    setBusy('configure'); setMessage(null); setError(null)
    try {
      await api.configureNodeManagement(nodeId, {
        managementBaseAddress: managementBaseAddress.trim() || null,
        bearerToken: bearerToken.trim() || null
      })
      setBearerToken('')
      setMessage('Management agent configuration updated.')
      await Promise.all([load(), refresh()])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(null)
    }
  }

  async function clearCredential() {
    if (!nodeId) return
    setBusy('credential'); setMessage(null); setError(null)
    try {
      await api.configureNodeManagement(nodeId, { managementBaseAddress: managementBaseAddress.trim() || null, clearBearerToken: true })
      setBearerToken('')
      setMessage('Management-agent bearer removed.')
      await Promise.all([load(), refresh()])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(null)
    }
  }

  async function install(catalogId: string) {
    setBusy('install:' + catalogId); setMessage(null); setError(null)
    try {
      await api.installManagedModel(nodeId, catalogId, {})
      setMessage('Model installation requested and registered as a managed deployment.')
      await Promise.all([load(), refresh()])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(null)
    }
  }

  async function action(kind: 'start' | 'stop' | 'remove', deploymentId: string) {
    setBusy(kind + ':' + deploymentId); setMessage(null); setError(null)
    try {
      if (kind === 'start') await api.startManagedDeployment(deploymentId)
      if (kind === 'stop') await api.stopManagedDeployment(deploymentId)
      if (kind === 'remove') await api.removeManagedDeployment(deploymentId)
      setMessage(kind === 'start' ? 'Model started and enabled for routing.' : kind === 'stop' ? 'Model removed from routing and stopped.' : 'Managed model removed from the hardware.')
      await Promise.all([load(), refresh()])
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusy(null)
    }
  }

  return <div className="stack modelHardware">
    <section className="panel">
      <div className="panelTitle">
        <div><h2>Hardware control plane</h2><span>Prepare a node once, then inventory hardware and control its model runtimes from LlmProxy.</span></div>
        <div className="actions">
          <select aria-label="Managed hardware node" value={nodeId} onChange={event => { setNodeId(event.target.value); setMessage(null) }}>
            <option value="">Select hardware</option>
            {nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}
          </select>
          <button className="secondary" disabled={!nodeId || loading} onClick={() => void load()}>{loading ? 'Reading…' : 'Refresh inventory'}</button>
        </div>
      </div>
      {error && <div className="error">{error}</div>}
      {message && <div className="notice">{message}</div>}
      {overview?.agentError && <div className="notice">Management agent: {overview.agentError}</div>}
    </section>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>Hardware inventory</h2><span>{overview?.agentAvailable ? 'Live from management agent' : 'Agent unavailable'}</span></div>
        {hardware ? <>
          <div className="cards cardsFive compactCards">
            <Metric label="CPU" value={hardware.cpuLogicalCores + ' threads'} />
            <Metric label="System RAM" value={formatGiB(hardware.systemMemoryAvailableGiB) + ' free'} />
            <Metric label="GPU" value={gpus.length} />
            <Metric label="GPU memory" value={formatGiB(totals.gpuFree) + ' free'} />
            <Metric label="Disk" value={formatGiB(hardware.diskAvailableGiB) + ' free'} />
          </div>
          <div className="inventoryMeta"><strong>{hardware.hostname}</strong><span>{hardware.operatingSystem ?? 'OS unknown'} · {hardware.architecture ?? 'arch unknown'} · {hardware.runtime ?? 'runtime unknown'} {hardware.runtimeVersion ?? ''}</span></div>
          <table><thead><tr><th>GPU</th><th>Total VRAM</th><th>Free VRAM</th><th>Driver</th><th>Compute</th></tr></thead><tbody>
            {gpus.map((gpu, index) => <tr key={index}><td><strong>{gpu.name}</strong></td><td>{formatGiB(gpu.memoryTotalGiB)}</td><td>{formatGiB(gpu.memoryFreeGiB)}</td><td>{gpu.driverVersion ?? '—'}</td><td>{gpu.computeCapability ?? '—'}</td></tr>)}
            {gpus.length === 0 && <tr><td colSpan={5} className="muted">No accelerator reported by the management agent.</td></tr>}
          </tbody></table>
        </> : <p className="muted">Configure the management agent endpoint to collect system RAM, CPU, disk and accelerator inventory.</p>}
      </section>

      <section className="panel formPanel">
        <h2>Management agent</h2>
        <p className="muted">The agent runs on prepared hardware and exposes only the inventory and model lifecycle contract. Its bearer is encrypted at rest and never returned by the API.</p>
        <form onSubmit={configure}>
          <label>Management service root<input aria-label="Management service root" value={managementBaseAddress} onChange={event => setManagementBaseAddress(event.target.value)} placeholder="http://10.0.0.21:9900" /></label>
          <label>Bearer token<input aria-label="Management bearer token" type="password" value={bearerToken} onChange={event => setBearerToken(event.target.value)} placeholder={overview?.node.hasManagementCredential ? 'Configured · leave blank to keep it' : 'Optional only on isolated development networks'} /></label>
          <div className="actions">
            <button className="primary" disabled={!canWrite || !nodeId || busy === 'configure'}>{busy === 'configure' ? 'Saving…' : 'Save agent'}</button>
            <button type="button" className="secondary" disabled={!canWrite || !nodeId || !overview?.node.hasManagementCredential || busy === 'credential'} onClick={() => void clearCredential()}>Clear bearer</button>
          </div>
        </form>
      </section>
    </div>

    <section className="panel">
      <div className="panelTitle"><div><h2>Deployable model catalog</h2><span>Curated open-weight/open-source runtimes with conservative serving estimates.</span></div><span>Fit uses currently free RAM / VRAM / disk</span></div>
      <div className="tableScroll">
        <table><thead><tr><th>Model</th><th>License</th><th>Size</th><th>Context</th><th>GPU plan</th><th>RAM / disk</th><th>Capabilities</th><th>Fit</th><th>Action</th></tr></thead><tbody>
          {(overview?.catalog ?? []).map(item => <tr key={item.model.id}>
            <td><strong>{item.model.displayName}</strong><div className="muted mono">{item.model.providerModelName}</div><div className="muted">{item.model.notes}</div><a href={item.model.sourceUrl} target="_blank" rel="noreferrer">Model card</a></td>
            <td>{item.model.license}</td>
            <td>{item.model.parameterBillions}B · {item.model.precision}</td>
            <td>{formatTokens(item.model.contextTokens)}</td>
            <td>{item.model.minimumGpuMemoryGiB} GiB min · {item.model.recommendedGpuMemoryGiB} GiB rec<div className="muted">{item.model.minimumGpuCount} GPU min · TP {item.compatibility.suggestedTensorParallelSize}</div></td>
            <td>{item.model.minimumSystemMemoryGiB} GiB RAM min<div className="muted">{item.model.diskGiB} GiB disk</div></td>
            <td>{item.model.tags.join(' · ')}</td>
            <td><Fit status={item.compatibility.status} /><div className="muted">{item.compatibility.summary}</div>{item.compatibility.reasons.map(reason => <div className="muted" key={reason}>{reason}</div>)}</td>
            <td><button className="primary" disabled={!canWrite || !overview?.agentAvailable || item.compatibility.status === 'insufficient' || busy === 'install:' + item.model.id} onClick={() => void install(item.model.id)}>{busy === 'install:' + item.model.id ? 'Installing…' : 'Install'}</button></td>
          </tr>)}
          {!overview?.catalog?.length && <tr><td colSpan={9} className="muted">Select a hardware node to evaluate the model catalog.</td></tr>}
        </tbody></table>
      </div>
      <p className="muted">Compatibility is a planning heuristic, not a performance guarantee. Context length, KV cache, quantization and concurrency materially change memory demand; benchmark before enabling production traffic.</p>
    </section>

    <section className="panel">
      <div className="panelTitle"><h2>Installed models on this hardware</h2><span>Lifecycle state is synchronized with routing safety.</span></div>
      <table><thead><tr><th>Logical model</th><th>Provider</th><th>Agent state</th><th>Runtime endpoint</th><th>Routing</th><th>Actions</th></tr></thead><tbody>
        {(overview?.installations ?? []).map(item => <tr key={item.id}>
          <td><strong>{item.logicalModel ?? item.catalogModelId ?? item.modelId}</strong><div className="muted mono">{item.managedInstallationId}</div></td>
          <td className="mono">{item.providerModelName ?? '—'}</td>
          <td><Status value={item.agentStatus} />{item.agentState?.error && <div className="muted">{item.agentState.error}</div>}</td>
          <td className="mono">{item.runtimeBaseAddress ?? item.agentState?.runtimeBaseAddress ?? 'not running'}</td>
          <td>{item.enabled ? 'Enabled' : 'Disabled'}</td>
          <td className="actions">
            <button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() === 'running'} onClick={() => void action('start', item.id)}>Start</button>
            <button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() !== 'running'} onClick={() => void action('stop', item.id)}>Stop</button>
            <button disabled={!canWrite || busy !== null} onClick={() => void action('remove', item.id)}>Remove</button>
          </td>
        </tr>)}
        {!overview?.installations?.length && <tr><td colSpan={6} className="muted">No LlmProxy-managed models are installed on this hardware yet.</td></tr>}
      </tbody></table>
    </section>
  </div>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Fit({ status }: { status: string }) { return <span className={'fit fit-' + status}>{status}</span> }
function Status({ value }: { value: string }) { return <span className={'status status-' + value.toLowerCase()}><i />{value}</span> }
function formatGiB(value: number) { return Number.isFinite(value) ? value.toFixed(value >= 100 ? 0 : 1) + ' GiB' : '—' }
function formatTokens(value: number) { return value >= 1000 ? Math.round(value / 1024) + 'K' : String(value) }
