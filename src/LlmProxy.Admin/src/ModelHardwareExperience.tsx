import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Modal, Tabs } from './UiPrimitives'
import type { ManagedBenchmarkJob, ModelManagementOverview, Node } from './types'

export default function ModelHardwareExperience({ nodes, canWrite, refresh, embedded = false }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void>; embedded?: boolean }) {
  const [tab, setTab] = useState<'inventory' | 'deploy'>('inventory')
  const [agentOpen, setAgentOpen] = useState(false)
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? '')
  const [overview, setOverview] = useState<ModelManagementOverview | null>(null)
  const [managementBaseAddress, setManagementBaseAddress] = useState('')
  const [bearerToken, setBearerToken] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [installCatalogId, setInstallCatalogId] = useState<string | null>(null)
  const [maxNumSeqs, setMaxNumSeqs] = useState(4)
  const [maxModelLen, setMaxModelLen] = useState(8192)
  const [kvCacheDtype, setKvCacheDtype] = useState<'auto' | 'fp8'>('auto')
  const [cpuOffloadGiB, setCpuOffloadGiB] = useState(0)
  const [logicalAlias, setLogicalAlias] = useState('')
  const [installPort, setInstallPort] = useState('')
  const [benchmarkDeploymentId, setBenchmarkDeploymentId] = useState<string | null>(null)
  const [benchmarkJobs, setBenchmarkJobs] = useState<ManagedBenchmarkJob[]>([])
  const [benchmarkP95, setBenchmarkP95] = useState(5000)
  const [benchmarkSuccess, setBenchmarkSuccess] = useState(99)
  const [benchmarkBusy, setBenchmarkBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { if (!nodeId && nodes[0]) setNodeId(nodes[0].id) }, [nodeId, nodes])
  const load = useCallback(async () => { if (!nodeId) { setOverview(null); return } setLoading(true); setError(null); try { const next = await api.modelManagementOverview(nodeId); setOverview(next); setManagementBaseAddress(next.node.managementBaseAddress ?? '') } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setLoading(false) } }, [nodeId])
  useEffect(() => { void load() }, [load])
  const hardware = overview?.hardware; const gpus = hardware?.gpus ?? []; const totals = useMemo(() => ({ gpuMemory: gpus.reduce((sum, gpu) => sum + gpu.memoryTotalGiB, 0), gpuFree: gpus.reduce((sum, gpu) => sum + gpu.memoryFreeGiB, 0) }), [gpus])
  const benchmarkInstallation = overview?.installations.find(item => item.id === benchmarkDeploymentId) ?? null
  useEffect(() => {
    if (!benchmarkDeploymentId) return
    let active = true
    const poll = () => { void api.benchmarkJobs(benchmarkDeploymentId).then(rows => {
      if (active) setBenchmarkJobs(rows)
    }).catch(() => {}) }
    poll()
    const timer = window.setInterval(poll, 3000)
    return () => { active = false; window.clearInterval(timer) }
  }, [benchmarkDeploymentId])

  async function cancelBenchmark() {
    if (!recentBenchmark) return
    setBenchmarkBusy(true)
    try { await api.cancelBenchmark(recentBenchmark.id); setBenchmarkJobs(await api.benchmarkJobs(recentBenchmark.deploymentId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBenchmarkBusy(false) }
  }

  async function runBenchmark() {
    if (!benchmarkDeploymentId) return
    setBenchmarkBusy(true); setError(null)
    try {
      await api.startBenchmark(benchmarkDeploymentId, {
        maxP95TtftMilliseconds: benchmarkP95, minSuccessRatePercent: benchmarkSuccess
      })
      setBenchmarkJobs(await api.benchmarkJobs(benchmarkDeploymentId))
      setMessage('Benchmark queued. Results and provisional capacity will appear here automatically.')
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBenchmarkBusy(false) }
  }

  const recentBenchmark = benchmarkJobs[0] ?? null
  const benchmarkReport = (() => {
    if (!recentBenchmark?.reportJson) return null
    try { return JSON.parse(recentBenchmark.reportJson) as {
      recommendation?: { recommendedMaxConcurrency?: number | null; summary?: string }
      levels?: Array<{ concurrency: number; attempted: number; successRatePercent: number; p95TtftMilliseconds?: number | null; outputTokensPerSecond?: number | null }>
    } } catch { return null }
  })()

  async function configure(event: FormEvent) { event.preventDefault(); if (!nodeId) return; setBusy('configure'); setMessage(null); setError(null); try { await api.configureNodeManagement(nodeId, { managementBaseAddress: managementBaseAddress.trim() || null, bearerToken: bearerToken.trim() || null }); setBearerToken(''); setMessage('Management agent configuration updated.'); setAgentOpen(false); await Promise.all([load(), refresh()]) } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }
  async function clearCredential() { if (!nodeId) return; setBusy('credential'); setMessage(null); setError(null); try { await api.configureNodeManagement(nodeId, { managementBaseAddress: managementBaseAddress.trim() || null, clearBearerToken: true }); setBearerToken(''); setMessage('Management-agent bearer removed.'); await Promise.all([load(), refresh()]) } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }
  async function install(catalogId: string) { setBusy('install:' + catalogId); setMessage(null); setError(null); try {
    const runtime = overview?.catalog.find(item => item.model.id === catalogId)?.model.runtime ?? 'vllm'
    await api.installManagedModel(nodeId, catalogId, {
      publicName: logicalAlias.trim() || null,
      port: installPort ? Number(installPort) : null,
      maxNumSeqs, maxModelLen,
      kvCacheDtype: runtime === 'vllm' ? kvCacheDtype : null,
      cpuOffloadGiB: runtime === 'vllm' && cpuOffloadGiB > 0 ? cpuOffloadGiB : null
    })
    setInstallCatalogId(null)
    setMessage('Model installation registered. Start it and benchmark the real concurrency before increasing capacity.')
    await Promise.all([load(), refresh()])
  } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }
  async function action(kind: 'start' | 'stop' | 'remove', deploymentId: string) { setBusy(kind + ':' + deploymentId); setMessage(null); setError(null); try { if (kind === 'start') await api.startManagedDeployment(deploymentId); if (kind === 'stop') await api.stopManagedDeployment(deploymentId); if (kind === 'remove') await api.removeManagedDeployment(deploymentId); setMessage(kind === 'start' ? 'Model started and enabled for routing.' : kind === 'stop' ? 'Model removed from routing and stopped.' : 'Managed model removed from the hardware.'); await Promise.all([load(), refresh()]) } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }

  return <div className="stack compactPage modelHardware">
    <section className="panel pageToolbar"><div><h2>{embedded ? 'Inventory & model lifecycle' : 'Model & hardware control plane'}</h2><p className="muted">Hardware inventory is live machine data from the management agent: CPU, RAM, disk, accelerators, drivers and runtime. It is used to evaluate model fit before installation.</p></div><div className="actions"><select aria-label="Managed hardware node" value={nodeId} onChange={event => { setNodeId(event.target.value); setMessage(null) }}><option value="">Select hardware</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select><button className="secondary" disabled={!nodeId || loading} onClick={() => void load()}>{loading ? 'Reading…' : 'Refresh'}</button><a className="buttonLink secondary" href="/admin/downloads/install-node-agent.sh" download>Download management agent</a><button className="primary" disabled={!canWrite || !nodeId} onClick={() => setAgentOpen(true)}>Configure agent</button></div></section>
    {error && <div className="error">{error}</div>}{message && <div className="notice">{message}</div>}{overview?.agentError && <div className="notice">Management agent: {overview.agentError}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'inventory', label: 'Hardware inventory', count: gpus.length }, { value: 'deploy', label: 'Deploy models', count: overview?.installations.length ?? 0 }]} />

    {tab === 'inventory' && <section className="panel"><div className="panelTitle"><h2>Hardware inventory</h2><span>{overview?.agentAvailable ? 'Live from management agent' : overview?.inventoryStale ? 'Last reported inventory (offline)' : 'Agent unavailable'}</span></div>{hardware ? <><div className="cards cardsFive compactCards"><Metric label="CPU" value={hardware.cpuLogicalCores + ' threads'} /><Metric label="System RAM" value={formatGiB(hardware.systemMemoryAvailableGiB) + ' free'} /><Metric label="GPU" value={gpus.length} /><Metric label="GPU memory" value={formatGiB(totals.gpuFree) + ' free'} /><Metric label="Disk" value={formatGiB(hardware.diskAvailableGiB) + ' free'} /></div><div className="inventoryMeta"><strong>{hardware.hostname}</strong><span>{hardware.operatingSystem ?? 'OS unknown'} · {hardware.architecture ?? 'arch unknown'} · {hardware.runtime ?? 'runtime unknown'} {hardware.runtimeVersion ?? ''}</span></div><table><thead><tr><th>GPU</th><th>Total VRAM</th><th>Free VRAM</th><th>Driver</th><th>Compute</th></tr></thead><tbody>{gpus.map((gpu,index) => <tr key={index}><td><strong>{gpu.name}</strong></td><td>{formatGiB(gpu.memoryTotalGiB)}</td><td>{formatGiB(gpu.memoryFreeGiB)}</td><td>{gpu.driverVersion ?? '—'}</td><td>{gpu.computeCapability ?? '—'}</td></tr>)}</tbody></table></> : <div className="emptyState"><strong>No inventory yet</strong><p>Install and configure the management agent on this node to collect RAM, CPU, disk and accelerator inventory.</p><a className="buttonLink primary" href="/admin/downloads/install-node-agent.sh" download>Download agent installer</a></div>}</section>}

    {tab === 'deploy' && <><section className="panel"><div className="panelTitle"><div><h2>Deployable model catalog</h2><span>Curated runtimes with conservative serving estimates.</span></div><span>Fit uses current free RAM / VRAM / disk</span></div><div className="tableScroll"><table><thead><tr><th>Model</th><th>License</th><th>Size</th><th>Context</th><th>GPU plan</th><th>RAM / disk</th><th>Fit</th><th>Action</th></tr></thead><tbody>{(overview?.catalog ?? []).map(item => <tr key={item.model.id}><td><strong>{item.model.displayName}</strong><div className="muted mono">{item.model.providerModelName}</div><div className="muted">Runtime: {item.model.runtime ?? 'vllm'}</div><div className="muted">{item.model.notes}</div><a href={item.model.sourceUrl} target="_blank" rel="noreferrer">Model card</a></td><td>{item.model.license}</td><td>{item.model.parameterBillions}B · {item.model.precision}</td><td>{formatTokens(item.model.contextTokens)}</td><td>{item.model.minimumGpuMemoryGiB} GiB min · {item.model.recommendedGpuMemoryGiB} GiB rec<div className="muted">{item.model.minimumGpuCount} GPU min · TP {item.compatibility.suggestedTensorParallelSize}</div></td><td>{item.model.minimumSystemMemoryGiB} GiB RAM min<div className="muted">{item.model.diskGiB} GiB disk</div></td><td><Fit status={item.compatibility.status} /><div className="muted">{item.compatibility.summary}</div></td><td><button className="primary" disabled={!canWrite || !overview?.agentAvailable || item.compatibility.status === 'insufficient' || busy === 'install:' + item.model.id} onClick={() => { setInstallCatalogId(item.model.id); setMaxModelLen(8192); setMaxNumSeqs(4); setKvCacheDtype('auto'); setCpuOffloadGiB(0); setLogicalAlias(''); setInstallPort('') }}>{busy === 'install:' + item.model.id ? 'Installing…' : 'Install'}</button></td></tr>)}</tbody></table></div><p className="muted">Compatibility is a planning heuristic, not a performance guarantee. Benchmark before production traffic.</p></section><section className="panel"><div className="panelTitle"><h2>Installed models on this hardware</h2><span>Lifecycle state is synchronized with routing safety.</span></div><table><thead><tr><th>Logical model</th><th>Provider</th><th>Agent state</th><th>Live runtime load</th><th>Runtime endpoint</th><th>Routing</th><th>Actions</th></tr></thead><tbody>{(overview?.installations ?? []).map(item => <tr key={item.id}><td><strong>{item.logicalModel ?? item.catalogModelId ?? item.modelId}</strong><div className="muted mono">{item.managedInstallationId}</div></td><td className="mono">{item.providerModelName ?? '—'}<div className="muted">{item.runtime ?? 'vllm'} · seq {item.agentState?.maxNumSeqs ?? 'default'}</div></td><td><Status value={item.agentStatus} />{item.agentState?.error && <div className="muted">{item.agentState.error}</div>}</td><td>{item.runtimeMetrics?.available
          ? <><strong>{item.runtimeMetrics.runningRequests} running · {item.runtimeMetrics.waitingRequests} queued</strong><div className="muted">{item.runtimeMetrics.runtime} · cache {item.runtimeMetrics.cacheUsageRatio == null ? '—' : (item.runtimeMetrics.cacheUsageRatio * 100).toFixed(0) + '%'}</div></>
          : <span className="muted">{item.runtimeMetrics?.error ?? 'No runtime metrics yet'}</span>}</td><td className="mono">{item.runtimeBaseAddress ?? item.agentState?.runtimeBaseAddress ?? 'not running'}</td><td>{item.enabled ? 'Enabled' : 'Disabled'}</td><td className="actions"><button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() === 'running'} onClick={() => void action('start', item.id)}>Start</button><button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() !== 'running'} onClick={() => void action('stop', item.id)}>Stop</button><button disabled={!canWrite || busy !== null} onClick={() => void action('remove', item.id)}>Remove</button><button disabled={item.agentStatus.toLowerCase() !== 'running'} onClick={() => setBenchmarkDeploymentId(item.id)}>Benchmark</button></td></tr>)}</tbody></table></section></>}

    <Modal open={installCatalogId !== null} title="Install inference profile" description="An installation profile controls runtime behavior, not the gateway physical concurrency ceiling." onClose={() => setInstallCatalogId(null)}>
      <form className="formPanel" onSubmit={event => { event.preventDefault(); if (installCatalogId) void install(installCatalogId) }}>
        <div className="notice">{overview?.catalog.find(item => item.model.id === installCatalogId)?.model.displayName} · {overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime ?? 'vllm'}. Sequence count is a benchmark candidate, not guaranteed user capacity.</div>
        <label>Logical model alias (optional, default is catalog ID)<input aria-label="Install logical model alias" maxLength={160} placeholder={installCatalogId ?? ''} value={logicalAlias} onChange={event => setLogicalAlias(event.target.value)} /></label>
        <p className="muted">Reuse an existing logical alias only for the same provider checkpoint ID. Multiple deployments under one alias share a routing pool; each retains its own capacity limit.</p>
        <label>Dedicated host port (optional, auto-assigned otherwise)<input aria-label="Install runtime port" type="number" min="1024" max="65535" value={installPort} onChange={event => setInstallPort(event.target.value)} placeholder="Auto" /></label>
        <label>Maximum concurrent sequences<input aria-label="Runtime maximum sequences" type="number" min="1" max="128" value={maxNumSeqs} onChange={event => setMaxNumSeqs(Number(event.target.value))} required /></label>
        <label>{overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime === 'llama.cpp' ? 'Total context pool (tokens, shared among slots)' : 'Maximum context per request (tokens)'}<input aria-label="Runtime maximum context" type="number" min="256" max={overview?.catalog.find(item => item.model.id === installCatalogId)?.model.contextTokens ?? 262144} value={maxModelLen} onChange={event => setMaxModelLen(Number(event.target.value))} required /></label>
        {(overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime ?? 'vllm') === 'vllm' && <>
          <label>KV cache precision<select aria-label="Runtime KV cache precision" value={kvCacheDtype} onChange={event => setKvCacheDtype(event.target.value as 'auto' | 'fp8')}><option value="auto">Auto (baseline)</option><option value="fp8">FP8 (requires runtime/model support)</option></select></label>
          <label>CPU weight offload (GiB, 0 disables)<input aria-label="Runtime CPU offload" type="number" min="0" max="1024" step="1" value={cpuOffloadGiB} onChange={event => setCpuOffloadGiB(Number(event.target.value))} required /></label>
        </>}
        <p className="muted">Changing a profile requires stopping/removing its current installation. The physical concurrency limit remains unchanged until separately updated with benchmark evidence.</p>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setInstallCatalogId(null)}>Cancel</button><button className="primary" disabled={!canWrite || busy !== null}>{busy?.startsWith('install:') ? 'Installing…' : 'Install profile'}</button></div>
      </form>
    </Modal>
    <Modal open={benchmarkDeploymentId !== null} title="Automated inference benchmark" description="Launch a background synthetic prompt sweep through the selected managed runtime, without using SSH or copying any commands." onClose={() => { setBenchmarkDeploymentId(null); setBenchmarkJobs([]) }}>
      <div className="stack">
        <p className="muted">{benchmarkInstallation?.runtime ?? 'unknown'} · {benchmarkInstallation?.logicalModel ?? 'unknown'}</p>
        <p className="muted">The sweep runs 40 requests at concurrency 1, 2, 4, 8, 12, and 16. It measures streamed completions and does not automatically change the production limits.</p>
        <div className="formGridTwo">
          <label>Maximum P95 TTFT (ms)<input aria-label="Benchmark P95 TTFT limit" type="number" min="100" max="300000" value={benchmarkP95} onChange={e => setBenchmarkP95(Number(e.target.value))}/></label>
          <label>Minimum success rate (%)<input aria-label="Benchmark success threshold" type="number" min="1" max="100" step="1" value={benchmarkSuccess} onChange={e => setBenchmarkSuccess(Number(e.target.value))}/></label>
        </div>
        <button className="primary" disabled={!canWrite || benchmarkBusy || recentBenchmark?.status === 'running' || recentBenchmark?.status === 'pending'} onClick={() => void runBenchmark()}>{benchmarkBusy ? 'Scheduling…' : 'Start benchmark on this deployment'}</button>
        {recentBenchmark && (recentBenchmark.status === 'running' || recentBenchmark.status === 'pending') &&
          <button className="secondary" disabled={benchmarkBusy} onClick={() => void cancelBenchmark()}>Cancel benchmark</button>}
        {recentBenchmark && <div className="notice">
          <strong>Latest run: {recentBenchmark.status}</strong>
          <div className="muted">Requested {new Date(recentBenchmark.requestedAtUtc).toLocaleString()}</div>
          {recentBenchmark.error && <p>{recentBenchmark.error}</p>}
        </div>}
        {benchmarkReport?.recommendation && <p><strong>Provisional capacity: {benchmarkReport.recommendation.recommendedMaxConcurrency ?? 'Not established'}</strong> — {benchmarkReport.recommendation.summary}</p>}
        {benchmarkReport?.levels && <div className="tableScroll"><table><thead><tr><th>Concurrent</th><th>Requests</th><th>Success</th><th>P95 TTFT</th><th>Output tokens/s</th></tr></thead><tbody>
          {benchmarkReport.levels.map(level => <tr key={level.concurrency}>
            <td>{level.concurrency}</td><td>{level.attempted}</td><td>{level.successRatePercent.toFixed(1)}%</td>
            <td>{level.p95TtftMilliseconds == null ? 'n/a' : level.p95TtftMilliseconds.toFixed(0) + ' ms'}</td>
            <td>{level.outputTokensPerSecond == null ? 'n/a' : level.outputTokensPerSecond.toFixed(1)}</td>
          </tr>)}
        </tbody></table></div>}
        <p className="muted">Any successful recommendation is saved as capacity evidence only. Applying it requires explicit approval in Infrastructure → Capacity & telemetry.</p>
      </div>
    </Modal>
    <Modal open={agentOpen} title="Management agent" description="Configure the management service root. The bearer is encrypted at rest and never returned by the API." onClose={() => setAgentOpen(false)}><form className="formPanel" onSubmit={configure}><label>Management service root<input aria-label="Management service root" value={managementBaseAddress} onChange={event => setManagementBaseAddress(event.target.value)} placeholder="http://10.0.0.21:9900" /></label><label>Bearer token<input aria-label="Management bearer token" type="password" value={bearerToken} onChange={event => setBearerToken(event.target.value)} placeholder={overview?.node.hasManagementCredential ? 'Configured · leave blank to keep it' : 'Optional only on isolated development networks'} /></label><div className="modalActions"><button type="button" className="secondary" disabled={!overview?.node.hasManagementCredential || busy === 'credential'} onClick={() => void clearCredential()}>Clear bearer</button><button className="primary" disabled={!canWrite || busy === 'configure'}>{busy === 'configure' ? 'Saving…' : 'Save agent'}</button></div></form></Modal>
  </div>
}
function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Fit({ status }: { status: string }) { return <span className={'fit fit-' + status}>{status}</span> }
function Status({ value }: { value: string }) { return <span className={'status status-' + value.toLowerCase()}><i />{value}</span> }
function formatGiB(value: number) { return Number.isFinite(value) ? value.toFixed(value >= 100 ? 0 : 1) + ' GiB' : '—' }
function formatTokens(value: number) { return value >= 1000 ? Math.round(value / 1024) + 'K' : String(value) }
function shellQuote(value: string) { return "'" + value.replace(/'/g, "'\\''") + "'" }
