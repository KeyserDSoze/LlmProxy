import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import { useRouteHardware, useRouteTab } from './adminNavigation'
import { useT } from './uiPreferences'
import { Modal, Tabs } from './UiPrimitives'
import ModelCapabilityPanel from './ModelCapabilityPanel'
import HuggingFaceDiscoveryPanel from './HuggingFaceDiscoveryPanel'
import type { ModelCapabilityProfile, ModelInstallJob, ManagedBenchmarkJob, ModelManagementOverview, ManagedInstallation, ModelRevisionStatus, Node } from './types'

export default function ModelHardwareExperience({ nodes, canWrite, refresh, embedded = false, defaultTab = 'inventory', onRecoverAgent }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void>; embedded?: boolean; defaultTab?: 'inventory' | 'deploy'; onRecoverAgent?: () => void }) {
  const t = useT()
  const [tab, setTab] = useRouteTab<'inventory' | 'deploy'>('tab', ['inventory', 'deploy'], defaultTab)
  const [agentOpen, setAgentOpen] = useState(false)
  const [nodeId, setNodeId] = useRouteHardware(nodes)
  const currentNodeId = useRef(nodeId)
  currentNodeId.current = nodeId
  const [overview, setOverview] = useState<ModelManagementOverview | null>(null)
  const [managementBaseAddress, setManagementBaseAddress] = useState('')
  const [bearerToken, setBearerToken] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [installCatalogId, setInstallCatalogId] = useState<string | null>(null)
  const [capabilityDetails, setCapabilityDetails] = useState<{ title: string; profile: ModelCapabilityProfile } | null>(null)
  const [installCapabilities, setInstallCapabilities] = useState<ModelCapabilityProfile | null>(null)
  const [runtimeChoice, setRuntimeChoice] = useState<'vllm' | 'sglang' | 'llama.cpp' | 'airllm' | 'faster-whisper' | 'diffusers'>('vllm')
  const isMediaRuntime = runtimeChoice === 'faster-whisper' || runtimeChoice === 'diffusers'
  const [tensorParallelSize, setTensorParallelSize] = useState(1)
  const [gpuDevices, setGpuDevices] = useState('')
  const [extraArgumentsText, setExtraArgumentsText] = useState('')
  const [customRepository, setCustomRepository] = useState('')
  const [customConfirmed, setCustomConfirmed] = useState(false)
  const [downloadJobs, setDownloadJobs] = useState<ModelInstallJob[]>([])
  const finalizing = useRef(new Set<string>())
  const [maxNumSeqs, setMaxNumSeqs] = useState(4)
  const [maxModelLen, setMaxModelLen] = useState(8192)
  const [maxOutputTokens, setMaxOutputTokens] = useState(256)
  const [editingInstall, setEditingInstall] = useState<ManagedInstallation | null>(null)
  const [editContext, setEditContext] = useState(8192)
  const [editSequences, setEditSequences] = useState(1)
  const [editOutput, setEditOutput] = useState(256)
  const [upstreamRevisions, setUpstreamRevisions] = useState<ModelRevisionStatus[]>([])
  const [revisionBusy, setRevisionBusy] = useState(false)
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

  const installDescriptor = overview?.catalog.find(item => item.model.id === installCatalogId)?.model
  const installProviderName = installCatalogId === 'custom' ? customRepository.trim() : installDescriptor?.providerModelName ?? ''
  useEffect(() => {
    if (!installCatalogId || !installProviderName || !installProviderName.includes('/')) {
      setInstallCapabilities(null)
      return
    }
    let active = true
    const timeout = window.setTimeout(() => {
      void api.inspectModelCapabilities(installProviderName, runtimeChoice, isMediaRuntime ? null : maxModelLen, runtimeChoice === 'airllm' ? maxOutputTokens : null)
        .then(profile => { if (active) setInstallCapabilities(profile) })
        .catch(() => { if (active) setInstallCapabilities(null) })
    }, 350)
    return () => { active = false; window.clearTimeout(timeout) }
  }, [installCatalogId, installProviderName, runtimeChoice, maxModelLen, maxOutputTokens])


  useEffect(() => {
    // Hardware can change via dropdown or browser history; never show a stale
    // inventory or let an old request re-enable installation on another node.
    setOverview(null); setDownloadJobs([]); setInstallCatalogId(null); setUpstreamRevisions([]); setEditingInstall(null)
    setMessage(null); setError(null)
  }, [nodeId])
  const load = useCallback(async () => {
    if (!nodeId) { setOverview(null); return }
    setLoading(true); setError(null)
    try {
      const next = await api.modelManagementOverview(nodeId)
      if (currentNodeId.current !== nodeId) return
      setOverview(next)
      setManagementBaseAddress(next.node.managementBaseAddress ?? '')
    } catch (reason) {
      if (currentNodeId.current === nodeId)
        setError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      if (currentNodeId.current === nodeId) setLoading(false)
    }
  }, [nodeId])
  useEffect(() => { void load() }, [load])
  // Fresh host telemetry without requiring the operator to repeatedly press Refresh.
  useEffect(() => {
    if (!nodeId || tab !== 'inventory') return
    const timer = window.setInterval(() => {
      void api.modelManagementOverview(nodeId).then(next => {
        if (currentNodeId.current === nodeId) setOverview(next)
      }).catch(() => {})
    }, 15000)
    return () => window.clearInterval(timer)
  }, [nodeId, tab])
  const hardware = overview?.hardware; const gpus = hardware?.gpus ?? []; const totals = useMemo(() => ({ gpuMemory: gpus.reduce((sum, gpu) => sum + gpu.memoryTotalGiB, 0), gpuFree: gpus.reduce((sum, gpu) => sum + gpu.memoryFreeGiB, 0) }), [gpus])
  const unifiedMemory = gpus.length > 0 && gpus.every(gpu => gpu.memoryType === 'unified')
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

  useEffect(() => {
    if (!nodeId) { setDownloadJobs([]); return }
    let active = true
    const poll = async () => {
      try {
        const jobs = await api.installJobs(nodeId)
        if (!active) return
        setDownloadJobs(jobs)
        for (const job of jobs) {
          if (!active || !canWrite || job.status !== 'completed' || !job.result?.installationId) continue
          if (overview?.installations.some(item => item.managedInstallationId === job.result?.installationId)) continue
          const key = nodeId + ':' + job.id
          if (finalizing.current.has(key)) continue
          finalizing.current.add(key)
          try {
            await api.finalizeModelInstall(nodeId, job.id)
            if (!active) return
            setMessage('Download completed; deployment registered. You can start the model.')
            await Promise.all([load(), refresh()])
          } catch (reason) {
            finalizing.current.delete(key)
            if (active) setError('Deployment registration: ' + (reason instanceof Error ? reason.message : String(reason)))
          }
        }
      } catch { /* Agent offline; retain last known job list. */ }
    }
    void poll()
    const timer = window.setInterval(() => { void poll() }, 2500)
    return () => { active = false; window.clearInterval(timer) }
  }, [nodeId, canWrite, load, refresh, overview?.installations])

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

  async function prepareHost() {
    setBusy('prepare'); setMessage(null); setError(null)
    try {
      const result = await api.prepareNodeHost(nodeId)
      setMessage(result.success ? 'Host prerequisites verified and ready.' :
        'Host preparation requires attention: ' + result.code)
      await load()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  async function configure(event: FormEvent) { event.preventDefault(); if (!nodeId) return; setBusy('configure'); setMessage(null); setError(null); try { await api.configureNodeManagement(nodeId, { managementBaseAddress: managementBaseAddress.trim() || null, bearerToken: bearerToken.trim() || null }); setBearerToken(''); setMessage('Management agent configuration updated.'); setAgentOpen(false); await Promise.all([load(), refresh()]) } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }
  async function clearCredential() { if (!nodeId) return; setBusy('credential'); setMessage(null); setError(null); try { await api.configureNodeManagement(nodeId, { managementBaseAddress: managementBaseAddress.trim() || null, clearBearerToken: true }); setBearerToken(''); setMessage('Management-agent bearer removed.'); await Promise.all([load(), refresh()]) } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) } }
  async function install(catalogId: string) {
    setBusy('install:' + catalogId); setMessage(null); setError(null)
    try {
      const job = await api.queueModelInstall(nodeId, catalogId, {
        publicName: logicalAlias.trim() || (catalogId === 'custom' ? customRepository.replace(/[^a-zA-Z0-9_-]/g, '-') : null),
        providerModelName: catalogId === 'custom' ? customRepository.trim() : null,
        force: catalogId === 'custom' ? customConfirmed : false,
        port: installPort ? Number(installPort) : null,
        runtime: runtimeChoice,
        tensorParallelSize: runtimeChoice === 'llama.cpp' || runtimeChoice === 'airllm' || isMediaRuntime ? 1 : tensorParallelSize,
        gpuDevices: runtimeChoice === 'faster-whisper' ? null : runtimeChoice === 'diffusers' ? (gpuDevices.trim() || '0') : gpuDevices.trim() || null,
        extraArguments: isMediaRuntime ? [] : extraArgumentsText.split('\n').map(arg => arg.trim()).filter(Boolean),
        maxNumSeqs: isMediaRuntime ? 1 : maxNumSeqs,
        maxModelLen: isMediaRuntime ? null : maxModelLen,
        maxOutputTokens: runtimeChoice === 'airllm' ? maxOutputTokens : null,
        kvCacheDtype: runtimeChoice === 'vllm' ? kvCacheDtype : null,
        cpuOffloadGiB: runtimeChoice === 'vllm' && cpuOffloadGiB > 0 ? cpuOffloadGiB : null
      })
      setDownloadJobs(current => [job, ...current.filter(item => item.id !== job.id)])
      setInstallCatalogId(null)
      setMessage('Background download scheduled. Follow image and model progress below; you can leave the page.')
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function cancelInstall(jobId: string) {
    setBusy('cancel:' + jobId)
    try { await api.cancelModelInstall(nodeId, jobId); setDownloadJobs(await api.installJobs(nodeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function action(kind: 'start' | 'activate' | 'stop' | 'remove', deploymentId: string) {
    setBusy(kind + ':' + deploymentId); setMessage(null); setError(null)
    try {
      if (kind === 'start') await api.startManagedDeployment(deploymentId)
      if (kind === 'activate') await api.activateManagedDeployment(deploymentId)
      if (kind === 'stop') await api.stopManagedDeployment(deploymentId)
      if (kind === 'remove') {
        if (!window.confirm('Uninstall this runtime container and remove its deployment? Cached downloaded model weights are retained for reuse.')) return
        await api.removeManagedDeployment(deploymentId)
      }
      setMessage(kind === 'start' ? 'Model started and enabled for routing.' :
        kind === 'activate' ? 'Runtime /health verified; existing model enabled for routing.' :
        kind === 'stop' ? 'Model removed from routing and stopped.' : 'Managed runtime uninstalled; cached model files are preserved.')
      await Promise.all([load(), refresh()])
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  function openProfileEdit(item: ManagedInstallation) {
    setEditingInstall(item)
    setEditContext(item.agentState?.maxModelLen ?? 8192)
    setEditSequences(item.agentState?.maxNumSeqs ?? 1)
    setEditOutput(item.agentState?.maxOutputTokens ?? 256)
    setMessage(null); setError(null)
  }
  async function saveProfile(event: FormEvent) {
    event.preventDefault()
    if (!editingInstall) return
    setBusy('edit:' + editingInstall.id); setError(null)
    try {
      await api.configureManagedDeployment(editingInstall.id, {
        maxModelLen: editContext, maxNumSeqs: editSequences,
        maxOutputTokens: editingInstall.runtime === 'airllm' ? editOutput : null
      })
      setEditingInstall(null)
      setMessage('Runtime limits updated without deleting model weights. Press Start to apply the new limits.')
      await Promise.all([load(), refresh()])
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function checkRevisions() {
    setRevisionBusy(true); setError(null)
    try { setUpstreamRevisions(await api.checkManagedModelUpdates(nodeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setRevisionBusy(false) }
  }
  function reviewHubModel(repoId: string) {
    setInstallCatalogId('custom'); setRuntimeChoice('vllm')
    setCustomRepository(repoId); setCustomConfirmed(false)
    setLogicalAlias(''); setExtraArgumentsText(''); setInstallPort('')
    setMaxModelLen(8192); setMaxOutputTokens(256); setMaxNumSeqs(1)
    setTensorParallelSize(1); setGpuDevices('')
  }

  return <div className="stack compactPage modelHardware">
    <section className="panel pageToolbar"><div><h2>{defaultTab === 'deploy' ? t('model.header') : embedded ? t('model.inventoryHeader') : t('model.managementHeader')}</h2><p className="muted">{defaultTab === 'deploy' ? t('model.deployGuide') : t('model.inventoryGuide')}</p>{defaultTab === 'deploy' && <p><a href="/admin/system-one">{t('model.systemOneLink')} →</a></p>}</div><div className="actions"><select aria-label={t('model.hardwareSelect')} value={nodeId} onChange={event => setNodeId(event.target.value)}><option value="">{t('model.selectHardware')}</option>{nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select><button className="secondary" disabled={!nodeId || loading} onClick={() => void load()}>{loading ? t('model.reading') : t('model.refresh')}</button><span className="muted">{t('model.pairingHint')}</span><button className="primary" disabled={!canWrite || !nodeId} onClick={() => setAgentOpen(true)}>{t('model.configureAgent')}</button></div></section>
    {!nodeId && nodes.length > 0 && <div className="notice" role="alert">{t('model.invalidNode')}</div>}
    {error && <div className="error">{error}</div>}{message && <div className="notice">{message}</div>}{overview?.agentError && <div className="notice">Management agent: {overview.agentError}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'inventory', label: t('model.inventoryTab'), count: gpus.length }, { value: 'deploy', label: t('model.deployTab'), count: overview?.installations.length ?? 0 }]} />

    {tab === 'inventory' && <section className="panel"><div className="panelTitle"><h2>{t('model.inventoryTab')}</h2><span>{overview?.agentAvailable ? t('model.agentLive') : overview?.inventoryStale ? t('model.agentStale') : t('model.agentUnavailable')}</span></div>{hardware ? <><div className="cards cardsFive compactCards"><Metric label="CPU" value={hardware.cpuLogicalCores + ' threads' + (hardware.cpuUtilizationPercent == null ? '' : ' · ' + hardware.cpuUtilizationPercent.toFixed(0) + '% busy')} /><Metric label="System RAM" value={formatGiB(hardware.systemMemoryAvailableGiB) + ' free'} /><Metric label="GPU" value={gpus.length} /><Metric label={unifiedMemory ? "Shared CPU/GPU RAM" : "GPU memory"} value={unifiedMemory ? formatGiB(hardware.systemMemoryAvailableGiB) + " available total" : formatGiB(totals.gpuFree) + ' free'} /><Metric label="Disk" value={formatGiB(hardware.diskAvailableGiB) + ' free'} /></div>{unifiedMemory && <p className="notice">NVIDIA unified memory: CPU and GPU share the same system RAM. Available RAM is not additional dedicated GPU VRAM; model fit reserves host memory and combines CPU/GPU estimates.</p>}<div className="inventoryMeta"><strong>{hardware.hostname}</strong><span>{hardware.operatingSystem ?? 'OS unknown'} · {hardware.architecture ?? 'arch unknown'} · {hardware.runtime ?? 'runtime unknown'} {hardware.runtimeVersion ?? ''} · {gpus.length} GPU(s) detected</span></div>
      {hardware.readiness && <section className="stack">
        <div className="actions"><strong>Host prerequisites</strong><button className="secondary"
          disabled={!canWrite || !overview?.agentAvailable || busy !== null}
          onClick={() => { if (window.confirm('Host preparation may update packages or restart Docker. Stop all models on this node first. Continue?')) void prepareHost() }}>
          Prepare / repair host
        </button></div>
        <div>Docker engine: {hardware.readiness.dockerDaemonReady ? 'ready' : 'not ready'} · NVIDIA driver: {hardware.readiness.nvidiaDriverDetected ? 'detected' : 'not detected'} · NVIDIA toolkit: {hardware.readiness.nvidiaToolkitReady ? 'ready or not needed' : 'missing'}</div>
        {hardware.readiness.issues.map(issue => <p className="notice" key={issue}>{issue}</p>)}
      </section>}<table><thead><tr><th>GPU</th><th>Total VRAM</th><th>Free VRAM</th><th>Driver</th><th>Compute</th><th>GPU load</th><th>Temp.</th><th>Power</th></tr></thead><tbody>{gpus.map((gpu,index) => <tr key={index}><td><strong>#{index} · {gpu.name}</strong></td><td>{gpu.memoryType === 'unified' ? 'Shared RAM' : gpu.memoryType === 'unknown' ? 'Unknown' : formatGiB(gpu.memoryTotalGiB)}</td><td>{gpu.memoryType === 'unified' ? 'Shared with CPU' : gpu.memoryType === 'unknown' ? 'Unknown' : formatGiB(gpu.memoryFreeGiB)}</td><td>{gpu.driverVersion ?? '—'}</td><td>{gpu.computeCapability ?? '—'}</td><td>{gpu.utilizationPercent == null ? '—' : gpu.utilizationPercent.toFixed(0) + '%'}</td><td>{gpu.temperatureCelsius == null ? '—' : gpu.temperatureCelsius.toFixed(0) + ' °C'}</td><td>{gpu.powerWatts == null ? '—' : gpu.powerWatts.toFixed(1) + ' W'}</td></tr>)}</tbody></table></> : <div className="emptyState"><strong>No inventory yet</strong><p>Install and configure the management agent on this node to collect RAM, CPU, disk and accelerator inventory.</p><p>Use Infrastructure → Fleet & access → Pair Linux agent to download the release and generate a pairing code.</p></div>}</section>}

    {tab === 'deploy' && <>
      {overview && !overview.agentAvailable && <section className="notice" role="alert">
        <strong>{t('catalog.offlineTitle')}</strong>
        <p>{t('catalog.offlineHelp')}</p>
        {overview.agentError && <p className="muted">Connection detail: {overview.agentError}</p>}
        {onRecoverAgent && canWrite && <button className="secondary" onClick={onRecoverAgent}>{t('catalog.recover')}</button>}
        <button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? t('catalog.checking') : t('catalog.retry')}</button>
      </section>}
      <section className="panel">
        <div className="panelTitle"><h2>{t('catalog.downloads')}</h2><span>{t('catalog.jobs')}</span></div>
        {downloadJobs.length === 0 ? <p className="muted">{t('catalog.noDownloads')}</p> :
          <div className="stack">{downloadJobs.slice(0, 20).map(job => <div className="notice" key={job.id}>
            <div className="actions"><strong>{job.request.catalogModelId} · {job.request.runtime}</strong><Status value={job.status}/>
              {(job.status === 'queued' || job.status === 'running') &&
                <button className="secondary" disabled={!canWrite || busy !== null} onClick={() => void cancelInstall(job.id)}>Cancel</button>}
            </div>
            <div>{job.stage === 'container-image' ? 'Container image' : job.stage === 'model-weights' ? 'Model weights' : job.stage} · {job.detail}</div>
            {job.percent != null ? <div className="stack">
              <progress max="100" value={Math.min(100, Math.max(0, job.percent))} aria-label={'Download stage progress ' + job.request.catalogModelId} style={{width: '100%'}} />
              <strong>{job.percent.toFixed(1)}% <span className="muted">of current stage/file</span></strong>
            </div> : job.status === 'running' && <p className="muted">Preparing… percentage unavailable for this operation.</p>}
            {job.completedBytes != null && job.totalBytes != null &&
              <p className="muted">{(job.completedBytes / 1048576).toFixed(1)} / {(job.totalBytes / 1048576).toFixed(1)} MiB</p>}
            {job.status === 'completed' && <p className="muted">Download complete; deployment registration is automatic while Admin is open.</p>}
            {job.error && <p className="error">{job.error}</p>}
          </div>)}</div>}
      </section>
      <section className="panel"><div className="panelTitle"><div><h2>{t('catalog.title')}</h2><span>{t('catalog.subtitle')}</span></div>
          <div className="actions"><span>{t('catalog.fitDescription')}</span>
            <button className="secondary" disabled={!canWrite || !overview?.agentAvailable}
              title={!overview?.agentAvailable ? 'Connect or recover the Node Agent in Fleet & access first.' : undefined}
              onClick={() => { setInstallCatalogId('custom'); setRuntimeChoice('vllm'); setCustomRepository(''); setCustomConfirmed(false); setLogicalAlias(''); setExtraArgumentsText(''); setInstallPort(''); setMaxModelLen(8192); setMaxOutputTokens(256); setMaxNumSeqs(1); setTensorParallelSize(1); setGpuDevices('') }}>{t('catalog.custom')}</button>
          </div></div><div className="tableScroll"><table><thead><tr><th>{t('catalog.model')}</th><th>{t('catalog.license')}</th><th>{t('catalog.size')}</th><th>{t('catalog.context')}</th><th>{t('catalog.gpu')}</th><th>{t('catalog.ram')}</th><th>{t('catalog.fit')}</th><th>{t('catalog.action')}</th></tr></thead><tbody>{(overview?.catalog ?? []).map(item => <tr key={item.model.id}><td><strong>{item.model.displayName}</strong><div className="muted mono">{item.model.providerModelName}</div><div className="muted">{t('catalog.run')}: {item.model.runtime ?? 'vllm'}</div><div className="muted">{item.model.notes}</div><a href={item.model.sourceUrl} target="_blank" rel="noreferrer">{t('catalog.modelCard')}</a>{item.capabilities && <>
              {' · '}<button type="button" className="secondary" onClick={() => setCapabilityDetails({ title: item.model.displayName, profile: item.capabilities! })}>{t('catalog.capabilities')}</button>
            </>}</td><td>{item.model.license}</td><td>{item.model.parameterBillions}B · {item.model.precision}</td><td>{item.model.runtime === 'faster-whisper' ? 'Audio ≤25 MiB' : item.model.runtime === 'diffusers' ? t('catalog.imageSizes') : formatTokens(item.model.contextTokens)}</td><td>{item.model.runtime === 'faster-whisper' ? 'CPU-only' : item.model.runtime === 'diffusers' ? t('catalog.imageGpu') : <>{item.model.minimumGpuMemoryGiB} GiB min · {item.model.recommendedGpuMemoryGiB} GiB rec</>}<div className="muted">{item.model.runtime === 'faster-whisper' ? 'x64 / ARM64 · int8 CPU' : `${item.model.minimumGpuCount} GPU min · TP ${item.compatibility.suggestedTensorParallelSize}`}</div></td><td>{item.model.minimumSystemMemoryGiB} GiB RAM min<div className="muted">{item.model.diskGiB} GiB disk</div></td><td><Fit status={item.compatibility.status} /><div className="muted">{item.compatibility.summary}</div>{!overview?.agentAvailable && <div className="muted">{t('catalog.cached')}</div>}</td><td><button className="primary" disabled={!canWrite || !overview?.agentAvailable || hardware?.readiness?.dockerDaemonReady === false || item.compatibility.status === 'insufficient' || busy === 'install:' + item.model.id}
              title={!canWrite ? t('catalog.requiresAdmin') : !overview?.agentAvailable ? t('catalog.requiresAgent') : hardware?.readiness?.dockerDaemonReady === false ? t('catalog.requiresDocker') : item.compatibility.status === 'insufficient' ? t('catalog.insufficient') : t('catalog.installHere')}
              onClick={() => { setInstallCatalogId(item.model.id); setRuntimeChoice(item.model.runtime === 'diffusers' ? 'diffusers' : item.model.runtime === 'faster-whisper' ? 'faster-whisper' : item.model.runtime === 'llama.cpp' ? 'llama.cpp' : item.model.runtime === 'sglang' ? 'sglang' : item.model.runtime === 'airllm' ? 'airllm' : 'vllm'); setTensorParallelSize(item.compatibility.suggestedTensorParallelSize); setGpuDevices(''); setExtraArgumentsText(''); setMaxModelLen(8192); setMaxOutputTokens(256); setMaxNumSeqs(item.model.runtime === 'faster-whisper' || item.model.runtime === 'diffusers' ? 1 : 4); setKvCacheDtype('auto'); setCpuOffloadGiB(0); setLogicalAlias(''); setInstallPort('') }}>{busy === 'install:' + item.model.id ? t('catalog.installing') : t('catalog.install')}</button></td></tr>)}</tbody></table></div><p className="muted">{t('catalog.fitCaution')}</p></section>
      <HuggingFaceDiscoveryPanel canWrite={canWrite} onReview={reviewHubModel} />
      <section className="panel"><div className="panelTitle"><h2>{t('catalog.installed')}</h2>
        <div className="actions"><span>{t('catalog.editTip')}</span>
          <button className="secondary" disabled={!overview?.agentAvailable || revisionBusy}
            onClick={() => void checkRevisions()}>{revisionBusy ? t('catalog.checking') : t('catalog.checkUpdates')}</button>
        </div></div><table><thead><tr><th>{t('catalog.logical')}</th><th>{t('catalog.provider')}</th><th>{t('catalog.agentState')}</th><th>{t('catalog.load')}</th><th>{t('catalog.endpoint')}</th><th>{t('catalog.routing')}</th><th>{t('catalog.actions')}</th></tr></thead><tbody>{(overview?.installations ?? []).map(item => <tr key={item.id}><td><strong>{item.logicalModel ?? item.catalogModelId ?? item.modelId}</strong><div className="muted mono">{item.managedInstallationId}</div></td><td className="mono">{item.providerModelName ?? '—'}<div className="muted">{item.runtime ?? 'vllm'} · seq {item.agentState?.maxNumSeqs ?? 'default'}</div>
              {item.runtime === 'faster-whisper' ? <div className="muted">Audio ≤25 MiB · Whisper CPU · /v1/audio/transcriptions and /translations</div> : <div className="muted">{t('catalog.tokenContext')}: {item.agentState?.maxModelLen ?? (item.runtime === 'airllm' ? 8192 : 'default')} tokens · {t('catalog.output')}: {item.runtime === 'airllm' ? (item.agentState?.maxOutputTokens ?? 256) : t('catalog.perRequest')}</div>}
              <div className="muted">{t('catalog.revision')}: {item.agentState?.sourceRevision?.slice(0,12) ?? t('catalog.unknownRevision')}</div>
              {upstreamRevisions.find(rev => rev.installationId === item.managedInstallationId) &&
                <div className="notice">{upstreamRevisions.find(rev => rev.installationId === item.managedInstallationId)?.update.message}</div>}
            {item.capabilities && <button type="button" className="secondary" onClick={() => setCapabilityDetails({ title: item.logicalModel ?? item.providerModelName ?? 'Model', profile: item.capabilities! })}>Capabilities & client setup</button>}</td><td><Status value={item.agentStatus} />{item.agentState?.error && <div className="muted">{item.agentState.error}</div>}</td><td>{item.runtimeMetrics?.available
          ? <><strong>{item.runtimeMetrics.runningRequests} running · {item.runtimeMetrics.waitingRequests} queued</strong><div className="muted">{item.runtimeMetrics.runtime} · cache {item.runtimeMetrics.cacheUsageRatio == null ? '—' : (item.runtimeMetrics.cacheUsageRatio * 100).toFixed(0) + '%'}</div></>
          : <span className="muted">{item.runtimeMetrics?.error ?? t('catalog.emptyMetrics')}</span>}</td><td className="mono">{item.runtimeBaseAddress ?? item.agentState?.runtimeBaseAddress ?? t('catalog.notRunning')}</td><td>{item.enabled ? t('catalog.enabled') :
          item.agentStatus.toLowerCase() === 'running'
            ? <><strong>{t('catalog.disabled')}</strong><div className="muted">{t('catalog.unrouted')}</div></>
            : t('catalog.disabled')}</td><td className="actions">
          <button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() === 'running'}
            onClick={() => void action('start', item.id)}>{t('catalog.start')}</button>
          {item.agentStatus.toLowerCase() === 'running' && !item.enabled &&
            <button className="primary" disabled={!canWrite || busy !== null || !overview?.agentAvailable}
              title={t('catalog.healthTip')}
              onClick={() => void action('activate', item.id)}>{t('catalog.activate')}</button>}
          <button disabled={!canWrite || busy !== null || item.agentStatus.toLowerCase() !== 'running'} onClick={() => void action('stop', item.id)}>{t('catalog.stop')}</button>
          <button disabled={!canWrite || busy !== null || !overview?.agentAvailable || (item.runtime === 'faster-whisper' || item.runtime === 'diffusers') || item.agentStatus.toLowerCase() === 'running'}
            title={item.runtime === 'faster-whisper' || item.runtime === 'diffusers' ? 'Media runtime has fixed concurrency and no Chat token parameters.' : item.agentStatus.toLowerCase() === 'running' ? t('catalog.startStopped') : t('catalog.editStopped')}
            onClick={() => openProfileEdit(item)}>{t('catalog.edit')}</button>
          <button disabled={!canWrite || busy !== null} onClick={() => void action('remove', item.id)}>{t('catalog.uninstall')}</button>{item.runtime !== 'faster-whisper' && item.runtime !== 'diffusers' && <button disabled={item.agentStatus.toLowerCase() !== 'running'} onClick={() => setBenchmarkDeploymentId(item.id)}>{t('catalog.benchmark')}</button>}</td></tr>)}</tbody></table></section></>}

    <Modal open={Boolean(capabilityDetails)} title={`Capabilities · ${capabilityDetails?.title ?? ''}`}
      description="Automatically detected from known model declarations and the installed serving adapter."
      onClose={() => setCapabilityDetails(null)}>
      {capabilityDetails && <ModelCapabilityPanel profile={capabilityDetails.profile} />}
    </Modal>

    <Modal open={Boolean(editingInstall)} title={`${t('catalog.editTitle')} · ${editingInstall?.logicalModel ?? ''}`}
      description={t('catalog.editDescription')}
      onClose={() => setEditingInstall(null)}>
      <form className="formPanel" onSubmit={event => void saveProfile(event)}>
        <p className="notice">Current: {editingInstall?.agentState?.maxModelLen ?? 8192} context tokens,
          {editingInstall?.runtime === 'airllm' ? ` ${editingInstall.agentState?.maxOutputTokens ?? 256} maximum output tokens` : ' output controlled by caller'}.
          Higher values may increase memory usage, latency and GPU pressure.</p>
        <label>Maximum context (prompt + completion) tokens
          <input aria-label={t('model.editContext')} type="number" min="256"
            max={overview?.catalog.find(x => x.model.id === editingInstall?.catalogModelId)?.model.contextTokens ?? 262144}
            value={editContext} onChange={event => setEditContext(Number(event.target.value))} required />
        </label>
        {editingInstall?.runtime === 'airllm' && <label>Maximum output tokens per request
          <input aria-label={t('model.editOutput')} type="number" min="1" max={Math.min(4096, editContext)}
            value={editOutput} onChange={event => setEditOutput(Number(event.target.value))} required />
        </label>}
        <label>Maximum concurrent sequences / queue
          <input aria-label={t('model.editSequences')} type="number" min="1" max="128"
            value={editSequences} onChange={event => setEditSequences(Number(event.target.value))} required />
        </label>
        <p className="muted">{t('model.editNotice')}</p>
        <div className="modalActions"><button className="secondary" type="button" onClick={() => setEditingInstall(null)}>Cancel</button>
          <button className="primary" disabled={!canWrite || busy !== null || editOutput > editContext}>{t('catalog.saveLimits')}</button></div>
      </form>
    </Modal>

    <Modal open={installCatalogId !== null} title={t('catalog.installTitle')} description={t('catalog.installDescription')} onClose={() => setInstallCatalogId(null)}>
      <form className="formPanel" onSubmit={event => { event.preventDefault(); if (installCatalogId) void install(installCatalogId) }}>
        <div className="notice">{installDescriptor?.displayName ?? (installCatalogId === 'custom' ? 'Custom Hugging Face model' : 'Model')} · {runtimeChoice}. Sequence count is a benchmark candidate, not guaranteed user capacity.</div>
        {runtimeChoice === 'diffusers' && <div className="notice"><strong>{t('catalog.imageInstall')}</strong><p>{t('catalog.imageDescription')}</p></div>}
        {runtimeChoice === 'faster-whisper' && <div className="notice"><strong>Speech-to-text / English translation (CPU)</strong><p>OpenAI-compatible audio upload: POST /v1/audio/transcriptions and /v1/audio/translations. CPU int8 on x64/ARM64. Audio uploads up to 25 MiB; no Chat context, GPU acceleration, SSE or speech generation. After installing, start and activate the deployment.</p></div>}
        {installCapabilities && <details className="notice">
          <summary><strong>Automatically detected capabilities & client token limits</strong> — {installCapabilities.toolCalling.serving === 'supported' ? 'tools' : 'no verified tools'}, {installCapabilities.streaming.serving === 'supported' ? 'SSE' : 'no SSE'}, {installCapabilities.vision.serving === 'supported' ? 'vision' : 'no verified vision'}</summary>
          <ModelCapabilityPanel profile={installCapabilities} />
        </details>}
        <label>{t('model.engine')}
          <select aria-label={t('model.engine')} value={runtimeChoice} onChange={event => setRuntimeChoice(event.target.value as typeof runtimeChoice)}>
            {installCatalogId === 'custom'
              ? <><option value="vllm">vLLM</option><option value="sglang">SGLang (experimental)</option><option value="llama.cpp">llama.cpp (GGUF :quant)</option><option value="airllm">AirLLM layer streaming (experimental)</option></>
              : overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime === 'llama.cpp'
              ? <option value="llama.cpp">llama.cpp (GGUF)</option>
              : overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime === 'airllm'
              ? <option value="airllm">AirLLM (experimental layer streaming)</option>
              : overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime === 'faster-whisper'
              ? <option value="faster-whisper">Whisper (CPU speech-to-text / translation)</option>
              : overview?.catalog.find(item => item.model.id === installCatalogId)?.model.runtime === 'diffusers'
              ? <option value="diffusers">Diffusers (SSD-1B image generation / NVIDIA GPU)</option>
              : <><option value="vllm">vLLM</option><option value="sglang">SGLang (experimental)</option></>}
          </select>
        </label>
        {installCatalogId === 'custom' && <>
          <label>{t('model.repository')} (org/model, org/model:quant GGUF)
            <input aria-label={t('model.repository')} maxLength={220} value={customRepository}
              onChange={event => setCustomRepository(event.target.value)}
              placeholder="Qwen/Qwen3-8B" required />
          </label>
          <label><input type="checkbox" checked={customConfirmed} onChange={event => setCustomConfirmed(event.target.checked)} />
            {t('model.licenseConfirm')}</label>
          <p className="notice">{t('model.licenseNotice')}</p>
        </>}
        {runtimeChoice === 'airllm' && <p className="notice">{t('model.airllmNotice')}</p>}
        {runtimeChoice !== 'llama.cpp' && runtimeChoice !== 'airllm' && !isMediaRuntime && <label>{t('model.tensorParallel')}
          <input aria-label="Tensor parallel size" type="number" min="1" max="64" value={tensorParallelSize}
            onChange={event => setTensorParallelSize(Number(event.target.value))} required />
        </label>}
        {runtimeChoice !== 'airllm' && !isMediaRuntime && <label>{t('model.advancedArguments')} (one argument per line)
          <textarea aria-label={t('model.advancedArguments')} rows={3} value={extraArgumentsText}
            onChange={event => setExtraArgumentsText(event.target.value)}
            placeholder={"--gpu-memory-utilization\\n0.85"} />
        </label>}
        {runtimeChoice !== 'faster-whisper' && <label>{t('model.gpuDevices')} (0, 0,1)
          <input aria-label={t('model.gpuDevices')} value={gpuDevices}
            onChange={event => setGpuDevices(event.target.value)}
            placeholder={runtimeChoice === 'diffusers' ? '0 (single CUDA GPU)' : 'All GPUs (existing default)'} pattern={runtimeChoice === 'diffusers' ? '[0-9]{1,2}' : '[0-9]{1,2}(,[0-9]{1,2})*'} />
        </label>}
        {runtimeChoice !== 'faster-whisper' && <p className="muted">{runtimeChoice === 'diffusers' ? t('catalog.imageGpuHelp') : t('model.gpuHelp')}</p>}
        <label>{t('model.logicalAlias')} (optional)<input aria-label={t('model.logicalAlias')} maxLength={160} placeholder={installCatalogId ?? ''} value={logicalAlias} onChange={event => setLogicalAlias(event.target.value)} /></label>
        <p className="muted">{t('model.logicalHelp')}</p>
        <label>{t('model.port')} (optional)<input aria-label={t('model.port')} type="number" min="1024" max="65535" value={installPort} onChange={event => setInstallPort(event.target.value)} placeholder="Auto" /></label>
        {!isMediaRuntime && <p className="notice"><strong>{t('model.defaults')}:</strong> context {maxModelLen.toLocaleString()} tokens; {runtimeChoice === 'airllm' ? `maximum output ${maxOutputTokens.toLocaleString()} tokens` : 'output is controlled per inference request'}; {maxNumSeqs} queued/concurrent sequences. Context is the combined prompt + completion budget.</p>}
        {!isMediaRuntime && <label>{t('model.sequences')}<input aria-label={t('model.sequences')} type="number" min="1" max="128" value={maxNumSeqs} onChange={event => setMaxNumSeqs(Number(event.target.value))} required /></label>}
        {!isMediaRuntime && <label>{runtimeChoice === 'llama.cpp' ? t('model.totalContext') : t('model.requestContext')}<input aria-label={t('model.maxContext')} type="number" min="256" max={overview?.catalog.find(item => item.model.id === installCatalogId)?.model.contextTokens ?? 262144} value={maxModelLen} onChange={event => setMaxModelLen(Number(event.target.value))} required /></label>}
        {runtimeChoice === 'airllm' && <label>{t('model.maxOutput')}
          <input aria-label={t('model.maxOutput')} type="number" min="1" max={Math.min(4096, maxModelLen)}
            value={maxOutputTokens} onChange={event => setMaxOutputTokens(Number(event.target.value))} required />
        </label>}
        {runtimeChoice !== 'airllm' && !isMediaRuntime && <p className="muted">{t('model.perRequestOutput')}</p>}
        {runtimeChoice === 'vllm' && <>
          <label>KV cache precision<select aria-label="Runtime KV cache precision" value={kvCacheDtype} onChange={event => setKvCacheDtype(event.target.value as 'auto' | 'fp8')}><option value="auto">Auto (baseline)</option><option value="fp8">FP8 (requires runtime/model support)</option></select></label>
          <label>CPU weight offload (GiB, 0 disables)<input aria-label="Runtime CPU offload" type="number" min="0" max="1024" step="1" value={cpuOffloadGiB} onChange={event => setCpuOffloadGiB(Number(event.target.value))} required /></label>
        </>}
        {!isMediaRuntime && <p className="muted">{t('model.profileInfo')}</p>}
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setInstallCatalogId(null)}>Cancel</button><button className="primary" disabled={!canWrite || busy !== null || (installCatalogId === 'custom' && (!customConfirmed || !customRepository.trim()))}>{busy?.startsWith('install:') ? t('model.schedule') : t('model.downloadInstall')}</button></div>
      </form>
    </Modal>
    <Modal open={benchmarkDeploymentId !== null} title="Automated inference benchmark" description="Launch a background synthetic prompt sweep through the selected managed runtime, without using SSH or copying any commands." onClose={() => { setBenchmarkDeploymentId(null); setBenchmarkJobs([]) }}>
      <div className="stack">
        <p className="muted">{benchmarkInstallation?.runtime ?? 'unknown'} · {benchmarkInstallation?.logicalModel ?? 'unknown'}</p>
        <p className="muted">{benchmarkInstallation?.runtime === 'airllm'
          ? 'AirLLM non-streaming experimental sweep: 16 requests at concurrency 1, 2, 4, 8, 12 and 16, 8 output tokens each. Measures completed throughput and queues, not concurrent token generation. TTFT is unavailable.'
          : 'The sweep runs 40 requests at concurrency 1, 2, 4, 8, 12 and 16. It measures streamed completions and does not automatically change production limits.'}</p>
        <div className="formGridTwo">
          {benchmarkInstallation?.runtime !== 'airllm' && <label>Maximum P95 TTFT (ms)<input aria-label="Benchmark P95 TTFT limit" type="number" min="100" max="300000" value={benchmarkP95} onChange={e => setBenchmarkP95(Number(e.target.value))}/></label>}
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
