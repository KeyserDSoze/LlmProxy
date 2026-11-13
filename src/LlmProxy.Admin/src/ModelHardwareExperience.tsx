import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import { urlForAdminView, useRouteHardware, useRouteTab } from './adminNavigation'
import { useT } from './uiPreferences'
import { AdminLink, InfoIcon, Modal, Tabs } from './UiPrimitives'
import ModelCapabilityPanel, { ModelCapabilitySummary } from './ModelCapabilityPanel'
import ModelsDeployments from './ModelsDeployments'
import HuggingFaceDiscoveryPanel from './HuggingFaceDiscoveryPanel'
import InstalledModelCards from './InstalledModelCards'
import type { VerificationState } from './InstalledModelCards'
import StartupDiagnosis from './StartupDiagnosis'
import HuggingFaceAccessGuide from './HuggingFaceAccessGuide'
import HuggingFaceRepositoryInspector from './HuggingFaceRepositoryInspector'
import type { ModelCapabilityProfile, ModelStartJob, ModelInstallJob, ManagedBenchmarkJob, ModelManagementOverview, ManagedInstallation, ModelRevisionStatus, NodeResourceSnapshot, Node, Model, Deployment, GgufVariant } from './types'

type WorkspacePanel = 'catalog' | 'installed' | 'activity' | 'advanced'

/**
 * mode="models": install, run, troubleshoot and publish models on the selected server.
 * mode="hardware": the server's hardware, host readiness and Agent configuration.
 */
export default function ModelHardwareExperience({ nodes, canWrite, refresh, mode = 'models', defaultPanel = 'catalog', onRecoverAgent, models = [], deployments = [] }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void>; mode?: 'models' | 'hardware'; defaultPanel?: WorkspacePanel; onRecoverAgent?: () => void; models?: Model[]; deployments?: Deployment[] }) {
  const t = useT()
  const [panel, setPanel] = useRouteTab<WorkspacePanel>('panel',
    ['catalog', 'installed', 'activity', 'advanced'], defaultPanel)
  const [modelKind, setModelKind] = useRouteTab<'all' | 'OpenAi' | 'SystemOne' | 'ImageGeneration' | 'SpeechSynthesis' | 'AudioTranscription'>(
    'kind', ['all', 'OpenAi', 'SystemOne', 'ImageGeneration', 'SpeechSynthesis', 'AudioTranscription'], 'all')
  const [showJobHistory, setShowJobHistory] = useState(false)
  const [agentOpen, setAgentOpen] = useState(false)
  const [resourcesOpen, setResourcesOpen] = useState(false)
  const [resources, setResources] = useState<NodeResourceSnapshot | null>(null)
  const [resourcesLoading, setResourcesLoading] = useState(false)
  const [resourcesError, setResourcesError] = useState<string | null>(null)
  const [nodeId, setNodeId] = useRouteHardware(nodes)
  const currentNodeId = useRef(nodeId)
  currentNodeId.current = nodeId
  const [overview, setOverview] = useState<ModelManagementOverview | null>(null)
  const [managementBaseAddress, setManagementBaseAddress] = useState('')
  const [bearerToken, setBearerToken] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [installCatalogId, setInstallCatalogId] = useState<string | null>(null)
  const [startAfterInstall, setStartAfterInstall] = useState(true)
  const [forceHardwareAcknowledged, setForceHardwareAcknowledged] = useState(false)
  const [capabilityDetails, setCapabilityDetails] = useState<{ title: string; profile: ModelCapabilityProfile; logicalModel?: string } | null>(null)
  const [installCapabilities, setInstallCapabilities] = useState<ModelCapabilityProfile | null>(null)
  const [runtimeChoice, setRuntimeChoice] = useState<'vllm' | 'sglang' | 'llama.cpp' | 'airllm' | 'faster-whisper' | 'laya' | 'kokoro' | 'diffusers'>('vllm')
  const [tensorParallelSize, setTensorParallelSize] = useState(1)
  const [gpuDevices, setGpuDevices] = useState('')
  const [extraArgumentsText, setExtraArgumentsText] = useState('')
  const [customRepository, setCustomRepository] = useState('')
  const [customConfirmed, setCustomConfirmed] = useState(false)
  const [ggufVariants, setGgufVariants] = useState<GgufVariant[]>([])
  const [ggufVariantMessage, setGgufVariantMessage] = useState<string | null>(null)
  const [downloadJobs, setDownloadJobs] = useState<ModelInstallJob[]>([])
  const [startJobs, setStartJobs] = useState<ModelStartJob[]>([])
  const activatingStarts = useRef(new Set<string>())
  const finalizing = useRef(new Set<string>())
  const [maxNumSeqs, setMaxNumSeqs] = useState(4)
  const [maxModelLen, setMaxModelLen] = useState(8192)
  const [maxOutputTokens, setMaxOutputTokens] = useState(256)
  const [editingInstall, setEditingInstall] = useState<ManagedInstallation | null>(null)
  const [editContext, setEditContext] = useState(8192)
  const [editSequences, setEditSequences] = useState(1)
  const [editOutput, setEditOutput] = useState(256)
  const [editGpuMemoryFraction, setEditGpuMemoryFraction] = useState(0.7)
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

  const installCatalogEntry = overview?.catalog.find(item => item.model.id === installCatalogId)
  const installDescriptor = installCatalogEntry?.model
  const installContextCeiling = Math.min(262144, installDescriptor?.contextTokens ?? 262144)
  const editCatalogMax = overview?.catalog.find(item => item.model.id === editingInstall?.catalogModelId)?.model.contextTokens
  const editContextCeiling = Math.min(262144, editCatalogMax ?? 262144)
  const fixedMediaRuntime = runtimeChoice === 'faster-whisper' || runtimeChoice === 'laya' ||
    runtimeChoice === 'kokoro' || runtimeChoice === 'diffusers'
  const cpuMediaRuntime = runtimeChoice === 'faster-whisper' || runtimeChoice === 'laya' || runtimeChoice === 'kokoro'
  const requiresHardwareOverride = installCatalogEntry?.compatibility.status === 'insufficient'
  useEffect(() => { setForceHardwareAcknowledged(false) }, [installCatalogId, nodeId])
  const installProviderName = installCatalogId === 'custom' ? customRepository.trim() : installDescriptor?.providerModelName ?? ''
  const customBaseRepository = customRepository.trim().split(':')[0]
  useEffect(() => {
    if (installCatalogId !== 'custom' || runtimeChoice !== 'llama.cpp' ||
        !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(customBaseRepository)) {
      setGgufVariants([]); setGgufVariantMessage(null)
      return
    }
    let live = true
    setGgufVariants([]); setGgufVariantMessage(null)
    void api.huggingFaceGgufVariants(customBaseRepository)
      .then(result => {
        if (!live) return
        setGgufVariants(result.variants)
        if (result.variants.length === 0)
          setGgufVariantMessage('No selectable single-file GGUF quantizations were returned. Check the Files tab before installing.')
      })
      .catch(() => { if (live) setGgufVariantMessage('Could not inspect GGUF files. You may enter a verified quantization manually.') })
    return () => { live = false }
  }, [installCatalogId, runtimeChoice, customBaseRepository])
  useEffect(() => {
    if (!installCatalogId || !installProviderName || !installProviderName.includes('/')) {
      setInstallCapabilities(null)
      return
    }
    let active = true
    const timeout = window.setTimeout(() => {
      void api.inspectModelCapabilities(installProviderName, runtimeChoice, fixedMediaRuntime ? null : maxModelLen, runtimeChoice === 'airllm' ? maxOutputTokens : null)
        .then(profile => { if (active) setInstallCapabilities(profile) })
        .catch(() => { if (active) setInstallCapabilities(null) })
    }, 350)
    return () => { active = false; window.clearTimeout(timeout) }
  }, [installCatalogId, installProviderName, runtimeChoice, maxModelLen, maxOutputTokens, fixedMediaRuntime])


  useEffect(() => {
    // Hardware can change via dropdown or browser history; never show a stale
    // inventory or let an old request re-enable installation on another node.
    setOverview(null); setDownloadJobs([]); setStartJobs([]); setInstallCatalogId(null); setUpstreamRevisions([]); setEditingInstall(null)
    setResourcesOpen(false); setResources(null); setResourcesError(null)
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
    if (!nodeId || mode !== 'hardware') return
    const timer = window.setInterval(() => {
      void api.modelManagementOverview(nodeId).then(next => {
        if (currentNodeId.current === nodeId) setOverview(next)
      }).catch(() => {})
    }, 15000)
    return () => window.clearInterval(timer)
  }, [nodeId, mode])
  // A WebSocket or Cloudflare interruption is transient. Keep cached
  // installations visible and retry the read-only management overview in
  // the model workspace too, without requiring the operator to repeatedly click.
  useEffect(() => {
    if (!nodeId || overview?.agentAvailable || mode === 'hardware') return
    const timer = window.setInterval(() => {
      void api.modelManagementOverview(nodeId).then(next => {
        if (currentNodeId.current !== nodeId) return
        setOverview(next)
        if (next.agentAvailable) setError(null)
      }).catch(() => { /* Keep the last useful cached inventory while offline. */ })
    }, 12000)
    return () => window.clearInterval(timer)
  }, [nodeId, overview?.agentAvailable, mode])

  const hardware = overview?.hardware; const gpus = hardware?.gpus ?? []; const totals = useMemo(() => ({ gpuMemory: gpus.reduce((sum, gpu) => sum + gpu.memoryTotalGiB, 0), gpuFree: gpus.reduce((sum, gpu) => sum + gpu.memoryFreeGiB, 0) }), [gpus])
  const unifiedMemory = gpus.length > 0 && gpus.every(gpu => gpu.memoryType === 'unified')
  const filteredCatalog = (overview?.catalog ?? []).filter(item => modelKind === 'all' ||
    (item.model.surface ?? 'OpenAi') === modelKind)
  const installedForJob = (job: ModelInstallJob) => overview?.installations.find(item =>
    !!job.result?.installationId && item.managedInstallationId === job.result.installationId)
  const visibleJobs = showJobHistory ? downloadJobs : downloadJobs.filter(job =>
    job.status !== 'completed' || !overview?.installations.some(item =>
      item.managedInstallationId === job.result?.installationId && item.enabled))
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
            setMessage(job.request.startAfterInstall && job.result?.status === 'running'
              ? t('catalog.autoStartCompleted') : t('catalog.readyToStart'))
            await Promise.all([load(), refresh()])
          } catch (reason) {
            const text = reason instanceof Error ? reason.message : String(reason)
            // Uninstalled models stay in the Agent job history; keep the key so they are not retried.
            if (text.includes('no longer exists on the Agent')) continue
            finalizing.current.delete(key)
            if (active) setError('Deployment registration: ' + text)
          }
        }
      } catch { /* Agent offline; retain last known job list. */ }
    }
    void poll()
    const timer = window.setInterval(() => { void poll() }, 2500)
    return () => { active = false; window.clearInterval(timer) }
  }, [nodeId, canWrite, load, refresh, overview?.installations])

  // Agent-owned startup jobs survive WebSocket/Cloudflare and browser
  // disconnections; polling is read-only. On completion, reuse the Gateway's
  // explicit /health-verified Activate flow rather than assuming Docker
  // running equals a routable model. A failed activation is never retried in
  // a loop, and operators retain the manual health/activate control.
  useEffect(() => {
    if (!nodeId) return
    let mounted = true
    const poll = async () => {
      try {
        const jobs = await api.modelStartJobs(nodeId)
        if (!mounted || currentNodeId.current !== nodeId) return
        setStartJobs(jobs)
        for (const job of jobs) {
          if (!canWrite || job.status !== 'completed' || job.result?.status !== 'running') continue
          const installation = overview?.installations.find(x =>
            x.managedInstallationId === job.installationId)
          if (!installation || installation.enabled) continue
          const key = nodeId + ':' + job.id
          if (activatingStarts.current.has(key)) continue
          activatingStarts.current.add(key)
          try {
            await api.activateManagedDeployment(installation.id)
            if (!mounted) return
            setMessage('Startup completed. Runtime /health verified; routing enabled.')
            await Promise.all([load(), refresh()])
          } catch (error) {
            if (mounted) setError('Model is running but routing activation needs attention: ' +
              (error instanceof Error ? error.message : String(error)))
          }
        }
      } catch { /* Agent offline: keep last useful job state. */ }
    }
    void poll()
    const timer = window.setInterval(() => { void poll() }, 3000)
    return () => { mounted = false; window.clearInterval(timer) }
  }, [nodeId, canWrite, overview?.installations, load, refresh])

  async function cancelStartJob(jobId: string) {
    if (!nodeId || !window.confirm('Cancel this pending startup? The Agent will stop and clean up the runtime; cached weights are preserved.')) return
    try {
      await api.cancelManagedStartJob(nodeId, jobId)
      setMessage('Startup cancellation requested on Agent.')
    } catch (error) { setError(error instanceof Error ? error.message : String(error)) }
  }

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
        startAfterInstall: catalogId !== 'custom' && startAfterInstall,
        force: catalogId === 'custom' ? customConfirmed : (requiresHardwareOverride && forceHardwareAcknowledged),
        port: installPort ? Number(installPort) : null,
        runtime: runtimeChoice,
        tensorParallelSize: runtimeChoice === 'llama.cpp' || runtimeChoice === 'airllm' || fixedMediaRuntime ? 1 : tensorParallelSize,
        gpuDevices: cpuMediaRuntime ? null : runtimeChoice === 'diffusers' ? (gpuDevices.trim() || '0') : gpuDevices.trim() || null,
        extraArguments: fixedMediaRuntime ? [] : extraArgumentsText.split('\n').map(arg => arg.trim()).filter(Boolean),
        maxNumSeqs: fixedMediaRuntime ? 1 : maxNumSeqs,
        maxModelLen: fixedMediaRuntime ? null : maxModelLen,
        maxOutputTokens: runtimeChoice === 'airllm' ? maxOutputTokens : null,
        kvCacheDtype: runtimeChoice === 'vllm' ? kvCacheDtype : null,
        cpuOffloadGiB: runtimeChoice === 'vllm' && cpuOffloadGiB > 0 ? cpuOffloadGiB : null
      })
      setDownloadJobs(current => [job, ...current.filter(item => item.id !== job.id)])
      setInstallCatalogId(null)
      setPanel('activity')
      setMessage(catalogId !== 'custom' && startAfterInstall ?
        t('catalog.autoStartQueued') : t('catalog.jobQueued'))
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function retryInstall(job: ModelInstallJob) {
    setBusy('retry:' + job.id); setError(null); setMessage(null)
    try {
      const next = await api.retryModelInstall(nodeId, job.id)
      setDownloadJobs(current => [next, ...current])
      setPanel('activity')
      setMessage(t('catalog.retryQueued'))
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function finishInstallation(job: ModelInstallJob) {
    setBusy('finalize:' + job.id); setError(null)
    try {
      await api.finalizeModelInstall(nodeId, job.id)
      await Promise.all([load(), refresh()])
      setPanel('installed')
      setMessage(t('catalog.readyToStart'))
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  const [verifications, setVerifications] = useState<Record<string, VerificationState>>({})
  async function verifyCapabilities(deploymentId: string) {
    setVerifications(current => ({ ...current, [deploymentId]: 'loading' }))
    try {
      const result = await api.verifyManagedDeployment(deploymentId)
      setVerifications(current => ({ ...current, [deploymentId]: result }))
    } catch (reason) {
      setVerifications(current => ({ ...current, [deploymentId]: { error: reason instanceof Error ? reason.message : String(reason) } }))
    }
  }

  async function cancelInstall(jobId: string) {
    setBusy('cancel:' + jobId)
    try { await api.cancelModelInstall(nodeId, jobId); setDownloadJobs(await api.installJobs(nodeId)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }
  async function loadResources() {
    if (!nodeId) return
    const requestedNode = nodeId
    setResourcesLoading(true); setResourcesError(null)
    try {
      const next = await api.nodeResourceDiagnostics(requestedNode)
      if (currentNodeId.current === requestedNode) setResources(next)
    } catch (reason) {
      if (currentNodeId.current === requestedNode)
        setResourcesError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      if (currentNodeId.current === requestedNode) setResourcesLoading(false)
    }
  }
  async function action(kind: 'start' | 'activate' | 'stop' | 'forceStop' | 'remove', deploymentId: string) {
    setBusy(kind + ':' + deploymentId); setMessage(null); setError(null)
    try {
      if (kind === 'start') {
        const job = await api.queueManagedStartJob(deploymentId)
        setStartJobs(current => [job, ...current.filter(x => x.id !== job.id)])
      }
      if (kind === 'activate') await api.activateManagedDeployment(deploymentId)
      if (kind === 'stop') await api.stopManagedDeployment(deploymentId)
      if (kind === 'forceStop') {
        if (!window.confirm(t('model.forceStopConfirm'))) return
        await api.forceStopManagedDeployment(deploymentId)
      }
      if (kind === 'remove') {
        if (!window.confirm('Uninstall this runtime container and remove its deployment? Cached downloaded model weights are retained for reuse.')) return
        await api.removeManagedDeployment(deploymentId)
      }
      setMessage(kind === 'start'
        ? 'Model startup queued on the Agent. It continues through browser and tunnel disconnects; follow the job status below.' :
        kind === 'activate' ? 'Runtime /health verified; existing model enabled for routing.' :
        kind === 'stop' || kind === 'forceStop' ? t('model.stopVerified') : 'Managed runtime uninstalled; cached model files are preserved.')
      await Promise.all([load(), refresh(), ...(resourcesOpen ? [loadResources()] : [])])
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  function openProfileEdit(item: ManagedInstallation) {
    setEditingInstall(item)
    setEditContext(item.agentState?.maxModelLen ?? 8192)
    setEditSequences(item.agentState?.maxNumSeqs ?? 1)
    setEditOutput(item.agentState?.maxOutputTokens ?? 256)
    setEditGpuMemoryFraction(item.agentState?.gpuMemoryUtilization ?? 0.7)
    setMessage(null); setError(null)
  }
  async function saveProfile(event: FormEvent) {
    event.preventDefault()
    if (!editingInstall) return
    setBusy('edit:' + editingInstall.id); setError(null)
    try {
      await api.configureManagedDeployment(editingInstall.id, {
        maxModelLen: editContext, maxNumSeqs: editSequences,
        maxOutputTokens: editingInstall.runtime === 'airllm' ? editOutput : null,
        gpuMemoryUtilization: editingInstall.runtime === 'vllm' ? editGpuMemoryFraction : null
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
  function reviewHubModel(repoId: string, hasGguf: boolean) {
    setInstallCatalogId('custom'); setStartAfterInstall(false); setRuntimeChoice(hasGguf ? 'llama.cpp' : 'vllm')
    setCustomRepository(repoId); setCustomConfirmed(false)
    setLogicalAlias(''); setExtraArgumentsText(''); setInstallPort('')
    setMaxModelLen(8192); setMaxOutputTokens(256); setMaxNumSeqs(1)
    setTensorParallelSize(1); setGpuDevices('')
  }

  const agentLabel = overview?.agentAvailable ? t('model.agentLive') :
    overview?.inventoryStale ? t('model.agentStale') : t('model.agentUnavailable')
  return <div className="stack compactPage modelHardware">
    <section className="modelWorkspaceToolbar">
      <div className="modelWorkspaceNode">
        <label htmlFor="model-workspace-node">{t('model.hardwareSelect')}</label>
        <select id="model-workspace-node" aria-label={t('model.hardwareSelect')}
          value={nodeId} onChange={event => setNodeId(event.target.value)}>
          <option value="">{t('model.selectHardware')}</option>
          {nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}
        </select>
        {nodeId && <span className={'pill ' + (overview?.agentAvailable ? 'pill-success modelAgentConnected' : 'pill-warning')}>
          <i aria-hidden="true" />{agentLabel}
        </span>}
      </div>
      {mode === 'models' && hardware && <div className="serverSummary" role="group" aria-label={t('model.serverStatus')}>
        <span><strong>{gpus.length}</strong> {t('model.gpus')}</span>
        <span><strong>{formatGiB(hardware.systemMemoryAvailableGiB)}</strong> {t('model.freeMemory')}</span>
        <span><strong>{formatGiB(hardware.diskAvailableGiB)}</strong> {t('model.freeDisk')}</span>
      </div>}
      <div className="modelWorkspaceToolbarActions">
        <button className="secondary" disabled={!nodeId || !overview?.agentAvailable}
          onClick={() => { setResourcesOpen(true); void loadResources() }}>{t('model.memoryDiagnostics')}</button>
        <button className="secondary" disabled={!nodeId || loading} onClick={() => void load()}>
          {loading ? t('model.reading') : t('model.refresh')}</button>
        <button className="secondary" disabled={!canWrite || !nodeId} onClick={() => setAgentOpen(true)}>
          {t('model.configureAgent')}</button>
        {mode === 'models' && nodeId && <AdminLink className="buttonLink secondary"
          href={urlForAdminView('infrastructure') + '?section=hardware&node=' + encodeURIComponent(nodeId)}>
          {t('model.hardwareDetails')} →</AdminLink>}
        {mode === 'hardware' && nodeId && <AdminLink className="buttonLink primary"
          href={urlForAdminView('models') + '?node=' + encodeURIComponent(nodeId)}>
          {t('model.manageModels')} →</AdminLink>}
      </div>
    </section>
    {!nodeId && nodes.length > 0 && <div className="notice" role="alert">{t('model.invalidNode')}</div>}
    {error && <div className="error" role="alert">{error}</div>}{message && <div className="notice">{message}</div>}{overview?.agentError && <div className="notice">Management agent: {overview.agentError}</div>}

    {mode === 'hardware' && <section className="panel"><div className="panelTitle"><h2>{t('model.inventoryTab')}</h2><span>{overview?.agentAvailable ? t('model.agentLive') : overview?.inventoryStale ? t('model.agentStale') : t('model.agentUnavailable')}</span></div>{hardware ? <><div className="cards cardsFive compactCards"><Metric label="CPU" value={hardware.cpuLogicalCores + ' threads' + (hardware.cpuUtilizationPercent == null ? '' : ' · ' + hardware.cpuUtilizationPercent.toFixed(0) + '% busy')} /><Metric label="System RAM" value={formatGiB(hardware.systemMemoryAvailableGiB) + ' free'} /><Metric label="GPU" value={gpus.length} /><Metric label={unifiedMemory ? "Shared CPU/GPU RAM" : "GPU memory"} value={unifiedMemory ? formatGiB(hardware.systemMemoryAvailableGiB) + " available total" : formatGiB(totals.gpuFree) + ' free'} /><Metric label="Disk" value={formatGiB(hardware.diskAvailableGiB) + ' free'} /></div>{unifiedMemory && <p className="notice">NVIDIA unified memory: CPU and GPU share the same system RAM. Available RAM is not additional dedicated GPU VRAM; model fit reserves host memory and combines CPU/GPU estimates.</p>}<div className="inventoryMeta"><strong>{hardware.hostname}</strong><span>{hardware.operatingSystem ?? 'OS unknown'} · {hardware.architecture ?? 'arch unknown'} · {hardware.runtime ?? 'runtime unknown'} {hardware.runtimeVersion ?? ''} · {gpus.length} GPU(s) detected</span></div>
      {hardware.readiness && <section className="stack hardwareReadiness">
        <div className="actions"><strong>Host prerequisites</strong><button className="secondary"
          disabled={!canWrite || !overview?.agentAvailable || busy !== null}
          onClick={() => { if (window.confirm('Host preparation may update packages or restart Docker. Stop all models on this node first. Continue?')) void prepareHost() }}>
          Prepare / repair host
        </button></div>
        <div>Docker engine: {hardware.readiness.dockerDaemonReady ? 'ready' : 'not ready'} · NVIDIA driver: {hardware.readiness.nvidiaDriverDetected ? 'detected' : 'not detected'} · NVIDIA toolkit: {hardware.readiness.nvidiaToolkitReady ? 'ready or not needed' : 'missing'}</div>
        {hardware.readiness.issues.map(issue => <p className="notice" key={issue}>{issue}</p>)}
      </section>}<div className="tableScroll"><table><thead><tr><th>GPU</th><th>Total VRAM</th><th>Free VRAM</th><th>Driver</th><th>Compute</th><th>GPU load</th><th>Temp.</th><th>Power</th></tr></thead><tbody>{gpus.map((gpu,index) => <tr key={index}><td><strong>#{index} · {gpu.name}</strong></td><td>{gpu.memoryType === 'unified' ? 'Shared RAM' : gpu.memoryType === 'unknown' ? 'Unknown' : formatGiB(gpu.memoryTotalGiB)}</td><td>{gpu.memoryType === 'unified' ? 'Shared with CPU' : gpu.memoryType === 'unknown' ? 'Unknown' : formatGiB(gpu.memoryFreeGiB)}</td><td>{gpu.driverVersion ?? '—'}</td><td>{gpu.computeCapability ?? '—'}</td><td>{gpu.utilizationPercent == null ? '—' : gpu.utilizationPercent.toFixed(0) + '%'}</td><td>{gpu.temperatureCelsius == null ? '—' : gpu.temperatureCelsius.toFixed(0) + ' °C'}</td><td>{gpu.powerWatts == null ? '—' : gpu.powerWatts.toFixed(1) + ' W'}</td></tr>)}</tbody></table></div></> : <div className="emptyState"><strong>No inventory yet</strong><p>Install and configure the management agent on this node to collect RAM, CPU, disk and accelerator inventory.</p><p>Use Servers & hardware → Fleet & access → Pair Linux agent to download the release and generate a pairing code.</p></div>}</section>}

    {mode === 'models' && <>
      {overview && !overview.agentAvailable && <section className="notice" role="alert">
        <strong>{t('catalog.offlineTitle')}</strong>
        <p>{t('catalog.offlineHelp')}</p>
        {overview.agentError && <p className="muted">Connection detail: {overview.agentError}</p>}
        {onRecoverAgent && canWrite && <button className="secondary" onClick={onRecoverAgent}>{t('catalog.recover')}</button>}
        <button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? t('catalog.checking') : t('catalog.retry')}</button>
      </section>}
      <Tabs value={panel} onChange={setPanel} items={[
        { value: 'catalog' as const, label: t('model.workspaceCatalog'), count: overview?.catalog.length ?? 0 },
        { value: 'installed' as const, label: t('model.workspaceInstalled'), count: overview?.installations.length ?? 0 },
        { value: 'activity' as const, label: t('model.workspaceActivity'),
          count: downloadJobs.filter(job => job.status === 'queued' || job.status === 'running' || job.status === 'interrupted' || job.status === 'failed').length },
        { value: 'advanced' as const, label: t('model.workspaceAdvanced') }
      ]} />
      <p className="workspaceHint">{t('model.lifecycleHint')}</p>
      {panel === 'activity' && <section className="panel">
        <div className="panelTitle"><h2>{t('catalog.downloads')}</h2>
          <div className="actions"><span>{t('catalog.jobs')}</span>
            <button className="secondary" onClick={() => setShowJobHistory(value => !value)}>
              {showJobHistory ? t('catalog.hideHistory') : t('catalog.showHistory')}
            </button>
          </div>
        </div>
        {visibleJobs.length === 0 ? <p className="muted">{t('catalog.noDownloads')}</p> :
          <div className="modelJobList">{visibleJobs.slice(0, 20).map(job => <div className="modelJobCard" key={job.id}>
            <div className="actions"><strong>{job.request.catalogModelId} · {job.request.runtime}</strong><Status value={job.status}/>
              {(job.status === 'queued' || job.status === 'running') &&
                <button className="secondary" disabled={!canWrite || busy !== null} onClick={() => void cancelInstall(job.id)}>{t('catalog.cancelJob')}</button>}
              {(job.status === 'interrupted' || job.status === 'failed' || job.status === 'cancelled') &&
                <button className="primary" disabled={!canWrite || busy !== null || !overview?.agentAvailable}
                  onClick={() => void retryInstall(job)}>{t('catalog.retryJob')}</button>}
              {job.status === 'completed' && !overview?.installations.some(item => item.managedInstallationId === job.result?.installationId) &&
                <button className="primary" disabled={!canWrite || busy !== null}
                  onClick={() => void finishInstallation(job)}>{t('catalog.finishJob')}</button>}
              {job.status === 'completed' && installedForJob(job) && installedForJob(job)!.agentStatus.toLowerCase() !== 'running' &&
                <button className="primary" disabled={!canWrite || busy !== null}
                  onClick={() => void action('start', installedForJob(job)!.id)}>{t('catalog.start')}</button>}
              {job.status === 'completed' && installedForJob(job)?.agentStatus.toLowerCase() === 'running' && !installedForJob(job)?.enabled &&
                <button className="primary" disabled={!canWrite || busy !== null}
                  onClick={() => void action('activate', installedForJob(job)!.id)}>{t('catalog.activate')}</button>}
              {job.status === 'completed' && installedForJob(job) &&
                <button className="secondary" onClick={() => setPanel('installed')}>{t('catalog.goToInstalled')}</button>}
            </div>
            <div>{job.stage === 'container-image' ? 'Container image' : job.stage === 'model-weights' ? 'Model weights' : job.stage} · {job.detail}</div>
            {job.percent != null ? <div className="stack">
              <progress max="100" value={Math.min(100, Math.max(0, job.percent))} aria-label={'Download stage progress ' + job.request.catalogModelId} style={{width: '100%'}} />
              <strong>{job.percent.toFixed(1)}% <span className="muted">of current stage/file</span></strong>
            </div> : job.status === 'running' && <p className="muted">Preparing… percentage unavailable for this operation.</p>}
            {job.completedBytes != null && job.totalBytes != null &&
              <p className="muted">{(job.completedBytes / 1048576).toFixed(1)} / {(job.totalBytes / 1048576).toFixed(1)} MiB</p>}
            {job.status === 'completed' && <p className="muted">
              {job.request.startAfterInstall && job.result?.status === 'running'
                ? t('catalog.autoStartCompleted') : t('catalog.completeNext')}
            </p>}
            {job.error && <StartupDiagnosis title={t('model.installFailedTitle')} text={job.error}
              guide={<HuggingFaceAccessGuide nodeId={nodeId} failure={job.error} canWrite={canWrite}
                onConfigured={() => retryInstall(job)} />} />}
          </div>)}</div>}
      </section>}

      {panel === 'catalog' && <section className="panel modelCatalogPanel">
        <div className="panelTitle">
          <div><h2>{t('catalog.title')}</h2><span>{t('catalog.subtitle')}</span></div>
          <div className="actions">
            <span>{t('catalog.fitDescription')}</span>
            {(modelKind === 'all' || modelKind === 'OpenAi') && <button className="secondary" disabled={!canWrite || !overview?.agentAvailable}
              title={!overview?.agentAvailable ? t('catalog.requiresAgent') : undefined}
              onClick={() => {
                setInstallCatalogId('custom'); setStartAfterInstall(false); setRuntimeChoice('vllm')
                setCustomRepository(''); setCustomConfirmed(false)
                setLogicalAlias(''); setExtraArgumentsText(''); setInstallPort('')
                setMaxModelLen(8192); setMaxOutputTokens(256); setMaxNumSeqs(1)
                setTensorParallelSize(1); setGpuDevices('')
              }}>{t('catalog.custom')}</button>}
          </div>
        </div>
        <div className="modelSurfaceChooser" role="group" aria-label={t('model.modelType')}>
          {([
            ['all', t('model.kindAll')],
            ['OpenAi', t('model.kindChat')],
            ['SystemOne', t('model.kindSystemOne')],
            ['ImageGeneration', t('model.kindImage')],
            ['SpeechSynthesis', t('model.kindTts')],
            ['AudioTranscription', t('model.kindStt')]
          ] as const).map(([kind, label]) =>
            <button key={kind} type="button" className={modelKind === kind ? 'primary' : 'secondary'}
              aria-pressed={modelKind === kind} onClick={() => setModelKind(kind)}>{label}</button>)}
        </div>
        {filteredCatalog.length > 0 && <div className="tableScroll">
          <table className="modelCatalogTable">
            <thead><tr>
              <th>{t('catalog.model')}</th><th>{t('catalog.modelFacts')}</th>
              <th>{t('catalog.modelMemory')}</th><th>{t('catalog.fit')}</th>
              <th>{t('catalog.capabilities')}</th><th>{t('catalog.action')}</th>
            </tr></thead>
            <tbody>{filteredCatalog.map(item => <tr key={item.model.id}>
              <td className="modelCatalogIdentity">
                <strong>{item.model.displayName}</strong>
                <div className="muted mono">{item.model.providerModelName}</div>
                <div className="muted">{t('catalog.run')}: {item.model.runtime ?? 'vllm'}</div>
                <p className="muted modelCatalogNotes">{item.model.notes}</p>
                <div className="modelCatalogLinks">
                  <a href={item.model.sourceUrl} target="_blank" rel="noreferrer">{t('catalog.modelCard')}</a>

                </div>
              </td>
              <td className="modelCatalogFacts">
                <span>{item.model.license}</span>
                <span>{item.model.parameterBillions}B · {item.model.precision}</span>
                <span>{item.model.runtime === 'faster-whisper' ? 'Audio ≤25 MiB' :
                  item.model.runtime === 'laya' ? 'System One classifier' :
                  item.model.runtime === 'kokoro' ? 'Speech WAV · af_heart' :
                  item.model.runtime === 'diffusers' ? 'Images · PNG 512–1024' :
                  formatTokens(item.model.contextTokens) + ' native max'}</span>
              </td>
              <td className="modelCatalogRequirements">
                <strong>{['faster-whisper', 'laya', 'kokoro'].includes(item.model.runtime ?? '') ? 'CPU · x64 / ARM64' :
                  item.model.minimumGpuMemoryGiB + ' GiB GPU min'}</strong>
                <span>{['faster-whisper', 'laya', 'kokoro'].includes(item.model.runtime ?? '') ? 'No GPU needed' :
                  item.model.recommendedGpuMemoryGiB + ' GiB GPU recommended'}</span>
                <span>{item.model.minimumSystemMemoryGiB} GiB RAM · {item.model.diskGiB} GiB disk</span>
                <span className="muted">{['faster-whisper', 'laya', 'kokoro'].includes(item.model.runtime ?? '') ? 'CPU serving' :
                  item.model.minimumGpuCount + ' GPU min · TP ' + item.compatibility.suggestedTensorParallelSize}</span>
              </td>
              <td className="modelCatalogFit">
                <Fit status={item.compatibility.status} />
                <p className="muted">{item.compatibility.summary}</p>
                {!overview?.agentAvailable && <p className="muted">{t('catalog.cached')}</p>}
              </td>
              <td className="modelCatalogCapabilities">
                <ModelCapabilitySummary profile={item.capabilities}/>
              </td>
              <td className="modelCatalogAction">
                <button className="primary" type="button"
                  disabled={!canWrite || !overview?.agentAvailable ||
                    hardware?.readiness?.dockerDaemonReady === false || busy === 'install:' + item.model.id}
                  title={!canWrite ? t('catalog.requiresAdmin') : !overview?.agentAvailable ?
                    t('catalog.requiresAgent') : hardware?.readiness?.dockerDaemonReady === false ?
                    t('catalog.requiresDocker') : item.compatibility.status === 'insufficient' ?
                    t('catalog.forceMismatch') : t('catalog.installHere')}
                  onClick={() => {
                    setInstallCatalogId(item.model.id)
                    setStartAfterInstall(true)
                    setRuntimeChoice(item.model.runtime === 'faster-whisper' ? 'faster-whisper' :
                      item.model.runtime === 'laya' ? 'laya' :
                      item.model.runtime === 'kokoro' ? 'kokoro' :
                      item.model.runtime === 'diffusers' ? 'diffusers' :
                      item.model.runtime === 'llama.cpp' ? 'llama.cpp' :
                      item.model.runtime === 'sglang' ? 'sglang' :
                      item.model.runtime === 'airllm' ? 'airllm' : 'vllm')
                    setTensorParallelSize(item.compatibility.suggestedTensorParallelSize)
                    setGpuDevices(''); setExtraArgumentsText(''); setMaxModelLen(8192)
                    setMaxOutputTokens(256); setMaxNumSeqs(['faster-whisper', 'laya', 'kokoro', 'diffusers'].includes(item.model.runtime ?? '') ? 1 : 4)
                    setKvCacheDtype('auto'); setCpuOffloadGiB(0); setLogicalAlias(''); setInstallPort('')
                  }}>{item.compatibility.status === 'insufficient' ?
                    t('catalog.inspectFit') : t('catalog.install')}</button>
                {item.capabilities && <button type="button" className="secondary capabilityActionButton"
                  aria-label={`${t('cap.details')} · ${t('catalog.capabilities')} · ${item.model.displayName}`}
                  title={t('cap.inspectDetails')}
                  onClick={() => setCapabilityDetails({ title: item.model.displayName,
                    profile: item.capabilities!, logicalModel: item.model.id })}>
                  <InfoIcon /> {t('cap.details')}
                </button>}
              </td>
            </tr>)}</tbody>
          </table>
        </div>}
        {(modelKind === 'all' || modelKind === 'SystemOne' ||
          modelKind === 'ImageGeneration' || modelKind === 'SpeechSynthesis') && <div className="modelCatalogPreviews">
          {([
            { surface: 'SystemOne', title: t('catalog.typeSystemOne'),
              model: 'convaiinnovations/laya', runtime: 'laya (CPU)',
              pr: 'https://github.com/KeyserDSoze/LlmProxy/pull/28' },
            { surface: 'ImageGeneration', title: t('catalog.typeImage'),
              model: 'segmind/SSD-1B', runtime: 'diffusers (NVIDIA GPU)',
              pr: 'https://github.com/KeyserDSoze/LlmProxy/pull/31' },
            { surface: 'SpeechSynthesis', title: t('catalog.typeTts'),
              model: 'Kokoro-82M / af_heart', runtime: 'kokoro (CPU)',
              pr: 'https://github.com/KeyserDSoze/LlmProxy/pull/30' }
          ] as const).filter(item =>
            (modelKind === 'all' || modelKind === item.surface) &&
            !(overview?.catalog ?? []).some(ready => ready.model.surface === item.surface)
          ).map(item =>
            <article className="modelCatalogPreview" key={item.surface}>
              <div className="modelCatalogPreviewHeader">
                <strong>{item.title}</strong>
                <span className="modelPreviewStatus">{t('catalog.availableSoon')}</span>
              </div>
              <div className="mono muted">{item.model}</div>
              <div className="muted">{t('catalog.modelRuntime')}: {item.runtime}</div>
              <p>{t('catalog.runtimePreview')}</p>
              <p className="muted">{t('catalog.typeComing')}</p>
              <a href={item.pr} target="_blank" rel="noreferrer">{t('catalog.moreInfo')} ↗</a>
            </article>)}
        </div>}
        {filteredCatalog.length === 0 && modelKind === 'AudioTranscription' &&
          <div className="emptyState">
            <strong>{t('model.noModelsForType')}</strong><p>{t('model.typeAvailability')}</p>
          </div>}
        <p className="muted modelCatalogFootnote">{t('catalog.fitCaution')}</p>
      </section>}
      {panel === 'catalog' && modelKind === 'OpenAi' && <HuggingFaceDiscoveryPanel canWrite={canWrite} onReview={reviewHubModel} />}
      {panel === 'installed' && <section className="panel installedPanel">
        <div className="panelTitle"><div><h2>{t('catalog.installed')}</h2><span>{t('catalog.editTip')}</span></div>
          <div className="actions">
            <button className="secondary" disabled={!overview?.agentAvailable || revisionBusy}
              onClick={() => void checkRevisions()}>{revisionBusy ? t('catalog.checking') : t('catalog.checkUpdates')}</button>
          </div></div>
        <InstalledModelCards nodeId={nodeId} installations={overview?.installations ?? []} startJobs={startJobs}
          upstreamRevisions={upstreamRevisions} canWrite={canWrite} busy={busy}
          agentAvailable={Boolean(overview?.agentAvailable)}
          verifications={verifications} onVerify={id => void verifyCapabilities(id)}
          onAction={(kind, id) => void action(kind, id)}
          onCancelStart={jobId => void cancelStartJob(jobId)}
          onEdit={openProfileEdit}
          onCapabilities={item => setCapabilityDetails({ title: item.logicalModel ?? item.providerModelName ?? 'Model',
            profile: item.capabilities!, logicalModel: item.logicalModel ?? item.catalogModelId ?? item.modelId })}
          onBenchmark={id => setBenchmarkDeploymentId(id)} />
        {(overview?.installations.length ?? 0) === 0 && <div className="emptyState">
          <strong>{t('model.noInstalled')}</strong><p>{t('model.installFirst')}</p>
          <button className="primary" onClick={() => setPanel('catalog')}>{t('model.workspaceCatalog')}</button>
        </div>}
      </section>}
      {panel === 'advanced' &&
        <ModelsDeployments models={models} deployments={deployments} nodes={nodes}
          canWrite={canWrite} refresh={refresh} embedded />}
    </>}

    <Modal open={resourcesOpen} title={t('model.memoryDiagnostics')}
      description={t('model.memoryDescription')} onClose={() => setResourcesOpen(false)}>
      <div className="stack">
        <div className="actions"><strong>{overview?.node.name ?? ''}</strong>
          <button className="secondary" disabled={resourcesLoading} onClick={() => void loadResources()}>
            {resourcesLoading ? t('model.memoryLoading') : t('model.memoryRefresh')}</button></div>
        {resourcesError && <div className="error" role="alert">{resourcesError}</div>}
        {resources?.thermal && <div className={resources.thermal.startupBlocked ? 'error' : 'notice'}
          role={resources.thermal.startupBlocked ? 'alert' : 'status'}>
          <strong>{t('model.thermalStatus')}</strong>
          <p>{t('model.thermalObserved')}: {resources.thermal.maximumObservedCelsius == null
            ? t('model.thermalUnknown') : resources.thermal.maximumObservedCelsius.toFixed(1) + ' °C'}
            {' · '}{t('model.thermalStartLimit')}: {resources.thermal.startupLimitCelsius.toFixed(0)} °C</p>
          <p>{resources.thermal.explanation}</p>
          <p className="muted">{t('model.thermalDisclaimer')}</p>
        </div>}
        {overview?.hardware && <div className="cards compactCards">
          <Metric label={t('model.memoryAvailable')} value={formatGiB(overview.hardware.systemMemoryAvailableGiB)} />
          <Metric label={t('model.memoryTotal')} value={formatGiB(overview.hardware.systemMemoryTotalGiB)} />
        </div>}
        {(resources?.recentVllmMemoryFailures.length ?? 0) > 0 && <div className="notice">
          <strong>{t('model.memoryVllmFailure')}</strong>
          {resources!.recentVllmMemoryFailures.map(issue => <p key={issue.installationId}>
            <strong>{issue.modelName}</strong> · GPU {issue.gpuIndex}
            {' · '}{t('model.memoryRequested')} {formatGiB(issue.requestedAtStartupGiB)}
            {' · '}{t('model.memoryFreeAtStartup')} {formatGiB(issue.freeAtStartupGiB)}
            {' · '}{t('model.memoryShortfall')} <strong>{formatGiB(issue.shortfallAtStartupGiB)}</strong>
          </p>)}
          <p className="muted">{t('model.memoryHistorical')}</p>
        </div>}
        <section className="stack">
          <h3>{t('model.gpuProcessTitle')}</h3>
          <p className="muted">{t('model.gpuProcessDisclaimer')}</p>
          <div className="tableScroll"><table><thead><tr>
            <th>{t('model.gpuProcessName')}</th><th>PID</th><th>GPU</th>
            <th>{t('model.gpuProcessMemory')}</th>
          </tr></thead><tbody>
            {(resources?.gpuProcesses ?? []).map(process => <tr key={process.pid}>
              <td className="mono">{process.processName}</td>
              <td className="mono">{process.pid}</td><td>{process.gpuIndex}</td>
              <td>{process.reportedGpuMemoryGiB == null ? '—' : formatGiB(process.reportedGpuMemoryGiB)}</td>
            </tr>)}
          </tbody></table></div>
          {resources && resources.gpuProcesses.length === 0 && <p className="muted">{t('model.gpuProcessEmpty')}</p>}
        </section>
        <div className="tableScroll"><table><thead><tr>
          <th>{t('model.memoryContainer')}</th><th>{t('model.memoryDockerUsage')}</th>
          <th>{t('model.memoryOwnership')}</th><th>{t('catalog.actions')}</th>
        </tr></thead><tbody>{(resources?.containers ?? []).map(item => {
          const deployment = overview?.installations.find(row => row.managedInstallationId === item.installationId)
          return <tr key={item.name}>
            <td><strong>{item.modelName ?? item.name}</strong><div className="muted mono">{item.name}</div></td>
            <td>{item.dockerMemoryGiB == null ? '—' : formatGiB(item.dockerMemoryGiB)}</td>
            <td>{item.installationId ? t('model.memoryManaged') : t('model.memoryExternal')}</td>
            <td>{deployment && canWrite && <div className="actions">
              <button className="secondary" disabled={busy !== null}
                onClick={() => void action('stop', deployment.id)}>{t('catalog.stop')}</button>
              <button className="secondary" disabled={busy !== null}
                onClick={() => void action('forceStop', deployment.id)}>{t('model.forceStop')}</button>
            </div>}</td>
          </tr>
        })}</tbody></table></div>
        {resources && resources.containers.length === 0 && <p className="muted">{t('model.memoryNoContainers')}</p>}
        {resources?.warnings.map((warning, index) => <p className="muted" key={index}>{warning}</p>)}
        <p className="muted">{t('model.memorySafety')}</p>
      </div>
    </Modal>
    <Modal open={Boolean(capabilityDetails)} title={`${t('catalog.capabilities')} · ${capabilityDetails?.title ?? ''}`}
      description={t('cap.dialogDescription')} className="capabilitiesModal"
      onClose={() => setCapabilityDetails(null)}>
      {capabilityDetails && <ModelCapabilityPanel profile={capabilityDetails.profile}
        logicalModel={capabilityDetails.logicalModel}/>}
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
            max={editContextCeiling}
            value={editContext} onChange={event => setEditContext(Number(event.target.value))} required />
        </label>
        <ContextPresets value={editContext} onChange={setEditContext}
          ceiling={editContextCeiling} curated={editCatalogMax != null}
          sharedPool={editingInstall?.runtime === 'llama.cpp'} />
        {editingInstall?.runtime === 'airllm' && <label>Maximum output tokens per request
          <input aria-label={t('model.editOutput')} type="number" min="1" max={Math.min(4096, editContext)}
            value={editOutput} onChange={event => setEditOutput(Number(event.target.value))} required />
        </label>}
        <label>Maximum concurrent sequences / queue
          <input aria-label={t('model.editSequences')} type="number" min="1" max="128"
            value={editSequences} onChange={event => setEditSequences(Number(event.target.value))} required />
        </label>
        {editingInstall?.runtime === 'vllm' && <label>vLLM GPU memory utilization (0.10–0.95)
          <input aria-label="vLLM GPU memory fraction" type="number" min="0.1" max="0.95" step="0.05"
            value={editGpuMemoryFraction} onChange={event => setEditGpuMemoryFraction(Number(event.target.value))} required />
          <span className="muted">Suggested conservative starting value on unified-memory systems: 0.70.
            This reserves a fraction of GPU-visible memory; it does not guarantee that enough RAM is currently free.
            Check competing GPU processes and hardware cooling before starting.</span>
        </label>}
        <p className="muted">{t('model.editNotice')}</p>
        <div className="modalActions"><button className="secondary" type="button" onClick={() => setEditingInstall(null)}>Cancel</button>
          <button className="primary" disabled={!canWrite || busy !== null || editOutput > editContext ||
            (editingInstall?.runtime === 'vllm' &&
              (!Number.isFinite(editGpuMemoryFraction) || editGpuMemoryFraction < 0.1 || editGpuMemoryFraction > 0.95))}>{t('catalog.saveLimits')}</button></div>
      </form>
    </Modal>

    <Modal open={installCatalogId !== null} title={t('catalog.installTitle')} description={t('catalog.installDescription')} onClose={() => setInstallCatalogId(null)}>
      <form className="formPanel" onSubmit={event => { event.preventDefault(); if (installCatalogId) void install(installCatalogId) }}>
        <div className="notice">{installDescriptor?.displayName ?? (installCatalogId === 'custom' ? 'Custom Hugging Face model' : 'Model')} · {runtimeChoice}. Sequence count is a benchmark candidate, not guaranteed user capacity.</div>
        {installCatalogId !== 'custom' && <label className="hardwareOverrideConsent">
          <input type="checkbox" aria-label={t('catalog.startAfterInstall')}
            checked={startAfterInstall} onChange={event => setStartAfterInstall(event.target.checked)} />
          {t('catalog.startAfterInstall')}
        </label>}
        {installCatalogId !== 'custom' && <p className="muted">{t('catalog.startAfterInstallHelp')}</p>}
        {requiresHardwareOverride && <section className="hardwareOverride" role="group"
          aria-label={t('catalog.forceTitle')}>
          <strong>{t('catalog.forceTitle')}</strong>
          <p>{t('catalog.forceIntro')}</p>
          <p>{t('catalog.forceRisk')}</p>
          {installCatalogEntry?.compatibility.reasons.map(reason =>
            <p key={reason} className="muted">{reason}</p>)}
          <label className="hardwareOverrideConsent"><input type="checkbox"
            checked={forceHardwareAcknowledged} onChange={event => setForceHardwareAcknowledged(event.target.checked)} />
            {t('catalog.forceConsent')}
          </label>
        </section>}
        {runtimeChoice === 'laya' && <div className="notice"><strong>System One / Laya · CPU (experimental)</strong><p>{t('catalog.layaInstallInfo')}</p></div>}
        {runtimeChoice === 'kokoro' && <div className="notice"><strong>Kokoro Text-to-speech · CPU (experimental)</strong><p>{t('catalog.kokoroInstallInfo')}</p></div>}
        {runtimeChoice === 'diffusers' && <div className="notice"><strong>SSD-1B Images · NVIDIA GPU (experimental)</strong><p>{t('catalog.diffusersInstallInfo')}</p></div>}
        {runtimeChoice === 'faster-whisper' && <div className="notice"><strong>Speech-to-text / English translation (CPU)</strong><p>OpenAI-compatible audio upload: POST /v1/audio/transcriptions and /v1/audio/translations. CPU int8 on x64/ARM64. Audio uploads up to 25 MiB; no Chat context, GPU acceleration, SSE or speech generation. Start and routing are automatic when the checkbox is selected.</p></div>}
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
              : installDescriptor?.runtime === 'laya'
              ? <option value="laya">Laya (CPU System One classifier)</option>
              : installDescriptor?.runtime === 'kokoro'
              ? <option value="kokoro">Kokoro (CPU Text-to-speech)</option>
              : installDescriptor?.runtime === 'diffusers'
              ? <option value="diffusers">Diffusers (NVIDIA GPU Images)</option>
              : <><option value="vllm">vLLM</option><option value="sglang">SGLang (experimental)</option></>}
          </select>
        </label>
        {installCatalogId === 'custom' && <>
          <label>{t('model.repository')} (org/model, org/model:quant GGUF)
            <input aria-label={t('model.repository')} maxLength={220} value={customRepository}
              onChange={event => setCustomRepository(event.target.value)}
              placeholder="Qwen/Qwen3-8B" required />
          </label>
          <HuggingFaceRepositoryInspector repository={customBaseRepository}
            onChoose={choice => {
              setCustomRepository(choice.repository)
              setRuntimeChoice(choice.runtime)
              if (choice.maxModelLen) setMaxModelLen(Math.min(choice.maxModelLen, installContextCeiling))
            }} />
          <label><input type="checkbox" checked={customConfirmed} onChange={event => setCustomConfirmed(event.target.checked)} />
            {t('model.licenseConfirm')}</label>
          {runtimeChoice === 'llama.cpp' && <>
            <p className="notice">GGUF / llama.cpp accepts a repository alone, or an explicit quantization
              (such as <code>:Q4_K_M</code>). Without a suffix llama.cpp selects Q4_K_M
              if available, otherwise its first matching file. Prefer selecting an actual filename.</p>
            {ggufVariants.length > 0 && <label>Available GGUF quantization
              <select aria-label="Available GGUF quantizations"
                value={customRepository.includes(':') ? customRepository.split(':')[1] : ''}
                onChange={event => setCustomRepository(customBaseRepository +
                  (event.target.value ? ':' + event.target.value : ''))}>
                <option value="">Automatic llama.cpp selection</option>
                {ggufVariants.map(variant => <option key={variant.quantization} value={variant.quantization}>
                  {variant.quantization}{variant.sizeBytes ? ' · ' + formatBytes(variant.sizeBytes) : ''}
                  {(variant.parts ?? 1) > 1 ? ' · ' + variant.parts + ' parts' : ''} · {variant.fileName}
                </option>)}
              </select>
            </label>}
            {ggufVariantMessage && <p className="muted">{ggufVariantMessage}</p>}
            <p className="muted">Third-party architecture, GPU memory and multimodal serving have not been verified on this hardware.
              Downloading does not imply that the selected llama.cpp image can run the model.</p>
          </>}
          <p className="notice">{t('model.licenseNotice')}</p>
        </>}
        {runtimeChoice === 'airllm' && <p className="notice">{t('model.airllmNotice')}</p>}
        {runtimeChoice !== 'llama.cpp' && runtimeChoice !== 'airllm' && !fixedMediaRuntime && <label>{t('model.tensorParallel')}
          <input aria-label="Tensor parallel size" type="number" min="1" max="64" value={tensorParallelSize}
            onChange={event => setTensorParallelSize(Number(event.target.value))} required />
        </label>}
        {runtimeChoice !== 'airllm' && !fixedMediaRuntime && <label>{t('model.advancedArguments')} (one argument per line)
          <textarea aria-label={t('model.advancedArguments')} rows={3} value={extraArgumentsText}
            onChange={event => setExtraArgumentsText(event.target.value)}
            placeholder={"--gpu-memory-utilization\\n0.85"} />
        </label>}
        {!cpuMediaRuntime && <label>{t('model.gpuDevices')} (0, 0,1)
          <input aria-label={t('model.gpuDevices')} value={gpuDevices}
            onChange={event => setGpuDevices(event.target.value)}
            placeholder={runtimeChoice === 'diffusers' ? '0 (one CUDA GPU)' : 'All GPUs (existing default)'}
            pattern={runtimeChoice === 'diffusers' ? '[0-9]{1,2}' : '[0-9]{1,2}(,[0-9]{1,2})*'} />
        </label>}
        {!cpuMediaRuntime && <p className="muted">{t('model.gpuHelp')}</p>}
        <label>{t('model.logicalAlias')} (optional)<input aria-label={t('model.logicalAlias')} maxLength={160} placeholder={installCatalogId ?? ''} value={logicalAlias} onChange={event => setLogicalAlias(event.target.value)} /></label>
        <p className="muted">{t('model.logicalHelp')}</p>
        <label>{t('model.port')} (optional)<input aria-label={t('model.port')} type="number" min="1024" max="65535" value={installPort} onChange={event => setInstallPort(event.target.value)} placeholder="Auto" /></label>
        {!fixedMediaRuntime && <p className="notice"><strong>{t('model.defaults')}:</strong> context {maxModelLen.toLocaleString()} tokens; {runtimeChoice === 'airllm' ? `maximum output ${maxOutputTokens.toLocaleString()} tokens` : 'output is controlled per inference request'}; {maxNumSeqs} queued/concurrent sequences. Context is the combined prompt + completion budget.</p>}
        {!fixedMediaRuntime && <label>{t('model.sequences')}<input aria-label={t('model.sequences')} type="number" min="1" max="128" value={maxNumSeqs} onChange={event => setMaxNumSeqs(Number(event.target.value))} required /></label>}
        {!fixedMediaRuntime && <label>{runtimeChoice === 'llama.cpp' ? t('model.totalContext') : t('model.requestContext')}<input aria-label={t('model.maxContext')} type="number" min="256" max={installContextCeiling} value={maxModelLen} onChange={event => setMaxModelLen(Number(event.target.value))} required /></label>}
        {!fixedMediaRuntime && <ContextPresets value={maxModelLen} onChange={setMaxModelLen}
          ceiling={installContextCeiling} curated={Boolean(installDescriptor)}
          sharedPool={runtimeChoice === 'llama.cpp'} />}
        {runtimeChoice === 'airllm' && <label>{t('model.maxOutput')}
          <input aria-label={t('model.maxOutput')} type="number" min="1" max={Math.min(4096, maxModelLen)}
            value={maxOutputTokens} onChange={event => setMaxOutputTokens(Number(event.target.value))} required />
        </label>}
        {runtimeChoice !== 'airllm' && !fixedMediaRuntime && <p className="muted">{t('model.perRequestOutput')}</p>}
        {runtimeChoice === 'vllm' && <>
          <label>KV cache precision<select aria-label="Runtime KV cache precision" value={kvCacheDtype} onChange={event => setKvCacheDtype(event.target.value as 'auto' | 'fp8')}><option value="auto">Auto (baseline)</option><option value="fp8">FP8 (requires runtime/model support)</option></select></label>
          <label>CPU weight offload (GiB, 0 disables)<input aria-label="Runtime CPU offload" type="number" min="0" max="1024" step="1" value={cpuOffloadGiB} onChange={event => setCpuOffloadGiB(Number(event.target.value))} required /></label>
        </>}
        {!fixedMediaRuntime && <p className="muted">{t('model.profileInfo')}</p>}
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setInstallCatalogId(null)}>Cancel</button><button className="primary" disabled={!canWrite || busy !== null || (requiresHardwareOverride && !forceHardwareAcknowledged) || (installCatalogId === 'custom' && (!customConfirmed ||
            !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?::[A-Za-z0-9_.-]+)?$/.test(customRepository.trim()) ||
            (runtimeChoice !== 'llama.cpp' && customRepository.includes(':'))))}>{busy?.startsWith('install:') ? t('model.schedule') : requiresHardwareOverride ? t('catalog.forceAction') : t('model.downloadInstall')}</button></div>
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

function ContextPresets({ value, onChange, ceiling, curated, sharedPool }: {
  value: number; onChange: (next: number) => void; ceiling: number;
  curated: boolean; sharedPool: boolean
}) {
  const t = useT()
  const options = [8192, 16384, 32768, 65536, 131072].filter(limit => limit < ceiling)
  return <div className="stack">
    <div className="actions" role="group" aria-label={t('model.contextPresets')}>
      {options.map(limit => <button key={limit} className="secondary" type="button"
        disabled={value === limit} onClick={() => onChange(limit)}>{formatTokens(limit)}</button>)}
      <button className="secondary" type="button" disabled={value === ceiling}
        onClick={() => onChange(ceiling)}>{t('model.useContextMaximum')} · {formatTokens(ceiling)}</button>
    </div>
    <p className="muted">{curated ? t('model.contextNativeDisclaimer') : t('model.contextUnknownDisclaimer')}
      {' '}{sharedPool ? t('model.contextSharedDisclaimer') : t('model.contextMemoryDisclaimer')}</p>
  </div>
}
function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function Fit({ status }: { status: string }) { return <span className={'fit fit-' + status}>{status}</span> }
function Status({ value }: { value: string }) { return <span className={'status status-' + value.toLowerCase()}><i />{value}</span> }
function formatGiB(value: number) { return Number.isFinite(value) ? value.toFixed(value >= 100 ? 0 : 1) + ' GiB' : '—' }
function formatBytes(value: number) { return value >= 1024 ** 3 ? (value / 1024 ** 3).toFixed(1) + ' GiB' : (value / 1024 ** 2).toFixed(0) + ' MiB' }
function formatTokens(value: number) { return value >= 1000 ? Math.round(value / 1024) + 'K' : String(value) }
