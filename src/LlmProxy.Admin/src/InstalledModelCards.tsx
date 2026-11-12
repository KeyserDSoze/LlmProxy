import { ModelCapabilitySummary } from './ModelCapabilityPanel'
import HuggingFaceAccessGuide from './HuggingFaceAccessGuide'
import StartupDiagnosis from './StartupDiagnosis'
import { InfoIcon } from './UiPrimitives'
import { useT } from './uiPreferences'
import type { TranslationKey } from './uiPreferences'
import type { ManagedInstallation, ModelRevisionStatus, ModelStartJob } from './types'

export type InstalledAction = 'start' | 'activate' | 'stop' | 'forceStop' | 'remove'

const typedRuntimes = ['faster-whisper', 'laya', 'kokoro', 'diffusers']

type CardState = { tone: 'success' | 'neutral' | 'info' | 'danger'; label: TranslationKey }

function cardState(item: ManagedInstallation, job?: ModelStartJob): CardState {
  const status = item.agentStatus.toLowerCase()
  if (job && (job.status === 'queued' || job.status === 'starting')) return { tone: 'info', label: 'model.state.starting' }
  if (status === 'running') return { tone: 'success', label: 'model.state.running' }
  if (status === 'installing') return { tone: 'info', label: 'model.state.installing' }
  if (status === 'error' || job?.status === 'failed' || job?.status === 'interrupted')
    return { tone: 'danger', label: 'model.state.failed' }
  return { tone: 'neutral', label: 'model.state.stopped' }
}

export default function InstalledModelCards({ nodeId, installations, startJobs, upstreamRevisions, canWrite, busy,
  agentAvailable, onAction, onCancelStart, onEdit, onCapabilities, onBenchmark }: {
  nodeId?: string
  installations: ManagedInstallation[]
  startJobs: ModelStartJob[]
  upstreamRevisions: ModelRevisionStatus[]
  canWrite: boolean
  busy: string | null
  agentAvailable: boolean
  onAction: (kind: InstalledAction, deploymentId: string) => void
  onCancelStart: (jobId: string) => void
  onEdit: (item: ManagedInstallation) => void
  onCapabilities: (item: ManagedInstallation) => void
  onBenchmark: (deploymentId: string) => void
}) {
  const t = useT()
  return <div className="installedModelGrid">
    {installations.map(item => {
      const name = item.logicalModel ?? item.catalogModelId ?? item.modelId
      const job = startJobs.find(entry => entry.installationId === item.managedInstallationId)
      const state = cardState(item, job)
      const running = item.agentStatus.toLowerCase() === 'running'
      const starting = job?.status === 'queued' || job?.status === 'starting'
      const typed = typedRuntimes.includes(item.runtime ?? '')
      const revision = upstreamRevisions.find(entry => entry.installationId === item.managedInstallationId)
      const failure = job && (job.status === 'failed' || job.status === 'interrupted') && job.error
        ? job.error : item.agentState?.error
      const endpoint = managedPublicEndpoint(item.runtime, name)
      return <article key={item.id} className={'installedModelCard installedModelCard--' + state.tone}
        aria-label={name}>
        <header className="installedModelHeader">
          <div className="installedModelTitle">
            <span className={'pill pill-' + state.tone}><i aria-hidden="true" />{t(state.label)}</span>
            <h3>{name}</h3>
          </div>
          <span className={'pill ' + (item.enabled ? 'pill-success' : running ? 'pill-warning' : 'pill-neutral')}>
            {item.enabled ? t('model.routed') : t('model.notRouted')}</span>
        </header>
        <div className="installedModelSubtitle mono">
          {item.providerModelName ?? '—'} · {item.runtime ?? 'vllm'}
          {item.agentState?.port ? ' · :' + item.agentState.port : ''}
        </div>
        {running && !item.enabled && <p className="installedModelHint">{t('catalog.unrouted')}</p>}

        <dl className="installedModelFacts" aria-label={t('model.factsLabel')}>
          {typed ? <div><dt>{t('model.factsLabel')}</dt><dd>{typedRuntimeSummary(item.runtime)}</dd></div> : <>
            <div><dt>{t('catalog.tokenContext')}</dt>
              <dd>{item.agentState?.maxModelLen?.toLocaleString() ?? (item.runtime === 'airllm' ? '8,192' : 'default')} tokens</dd></div>
            <div><dt>Seq</dt><dd>{item.agentState?.maxNumSeqs ?? 'default'}</dd></div>
            <div><dt>{t('catalog.output')}</dt>
              <dd>{item.runtime === 'airllm' ? (item.agentState?.maxOutputTokens ?? 256) : t('catalog.perRequest')}</dd></div>
          </>}
          <div><dt>{t('catalog.revision')}</dt>
            <dd className="mono">{item.agentState?.sourceRevision?.slice(0, 12) ?? t('catalog.unknownRevision')}</dd></div>
          {item.agentState?.modelFile && <div className="installedModelWide"><dt>{t('model.ggufFile')}</dt>
            <dd className="mono">{item.agentState.modelFile}</dd></div>}
        </dl>
        {revision && <div className="notice">{revision.update.message}</div>}

        <div className="installedModelRow">
          <span className="installedModelLabel">{t('catalog.capabilities')}</span>
          <ModelCapabilitySummary profile={item.capabilities} />
        </div>
        <div className="installedModelRow">
          <span className="installedModelLabel">{t('model.loadLabel')}</span>
          {item.runtimeMetrics?.available
            ? <span><strong>{item.runtimeMetrics.runningRequests} running · {item.runtimeMetrics.waitingRequests} queued</strong>
              <span className="muted"> · {item.runtimeMetrics.runtime} · cache {item.runtimeMetrics.cacheUsageRatio == null
                ? '—' : (item.runtimeMetrics.cacheUsageRatio * 100).toFixed(0) + '%'}</span></span>
            : <span className="muted">{item.runtimeMetrics?.error ?? t('catalog.emptyMetrics')}</span>}
        </div>
        <div className="installedModelRow">
          <span className="installedModelLabel">{t('model.endpointLabel')}</span>
          <span className="installedModelEndpoint">
            <code>POST {endpoint}</code>
            <button className="secondary compact" type="button"
              onClick={() => void navigator.clipboard.writeText(window.location.origin + endpoint)}>
              {t('catalog.copyEndpoint')}</button>
            <button className="secondary compact" type="button" title={t('catalog.copyCurlHelp')}
              onClick={() => void navigator.clipboard.writeText(managedCurlExample(item.runtime, name, window.location.origin))}>
              {t('catalog.copyCurl')}</button>
          </span>
        </div>

        {starting && job && <div className="notice installedModelProgress" role="status">
          <strong>{t('model.queueDetail')}: {job.status}</strong>
          <p>{job.detail}</p>
          <button className="secondary" type="button" onClick={() => onCancelStart(job.id)}>Cancel startup</button>
        </div>}
        {job?.status === 'completed' && !item.enabled &&
          <p className="muted">Runtime started; verifying health and routing activation.</p>}
        {failure && !starting && <StartupDiagnosis
          title={job && (job.status === 'failed' || job.status === 'interrupted')
            ? t('model.startupFailedTitle') : t('model.lastError')}
          text={failure}
          guide={nodeId && <HuggingFaceAccessGuide nodeId={nodeId} failure={failure} canWrite={canWrite}
            onConfigured={() => onAction('start', item.id)} />} />}

        <footer className="installedModelActions">
          <button className={running ? 'secondary' : 'primary'}
            disabled={!canWrite || busy !== null || running || starting}
            onClick={() => onAction('start', item.id)}>{t('catalog.start')}</button>
          {running && !item.enabled &&
            <button className="primary" disabled={!canWrite || busy !== null || !agentAvailable}
              title={t('catalog.healthTip')}
              onClick={() => onAction('activate', item.id)}>{t('catalog.activate')}</button>}
          <button className="secondary" disabled={!canWrite || busy !== null || !running}
            onClick={() => onAction('stop', item.id)}>{t('catalog.stop')}</button>
          <button className="secondary" disabled={!canWrite || busy !== null || !agentAvailable || typed || running}
            title={typed ? 'This typed media runtime has fixed concurrency and no Chat-token parameters.'
              : running ? t('catalog.startStopped') : t('catalog.editStopped')}
            onClick={() => onEdit(item)}>{t('catalog.edit')}</button>
          {!typed && <button className="secondary" disabled={!running}
            onClick={() => onBenchmark(item.id)}>{t('catalog.benchmark')}</button>}
          {item.capabilities && <button type="button" className="secondary capabilityActionButton"
            aria-label={`${t('cap.details')} · ${t('catalog.capabilities')} · ${item.logicalModel ?? item.providerModelName ?? 'Model'}`}
            title={t('cap.inspectDetails')} onClick={() => onCapabilities(item)}>
            <InfoIcon /> {t('cap.details')}
          </button>}
          <span className="installedModelSpacer" />
          <button className="danger" disabled={!canWrite || busy !== null}
            onClick={() => onAction('remove', item.id)}>{t('catalog.uninstall')}</button>
        </footer>
        <div className="installedModelId mono muted">{item.managedInstallationId}</div>
      </article>
    })}
  </div>
}

function typedRuntimeSummary(runtime?: string) {
  return runtime === 'faster-whisper' ? 'Audio input ≤25 MiB' :
    runtime === 'laya' ? 'Typed English classifier' :
    runtime === 'kokoro' ? 'English voice af_heart · WAV' : 'NVIDIA PNG · 512–1024'
}

function shellQuote(value: string) { return "'" + value.replace(/'/g, "'\\''") + "'" }

export function managedPublicEndpoint(runtime: string | undefined, model: string): string {
  switch (runtime) {
    case 'laya': return '/v1/systemone/' + encodeURIComponent(model)
    case 'kokoro': return '/v1/audio/speech'
    case 'diffusers': return '/v1/images/generations'
    case 'faster-whisper': return '/v1/audio/transcriptions'
    default: return '/v1/chat/completions'
  }
}

/** Ready-to-copy public API examples. Use the logical alias, never upstream HF name. */
export function managedCurlExample(runtime: string | undefined, model: string, origin: string): string {
  const endpoint = managedPublicEndpoint(runtime, model)
  const base = `curl --fail-with-body -sS -X POST ${shellQuote(origin + endpoint)} -H "Authorization: Bearer $LLMPROXY_API_KEY"`
  const json = (body: object) =>
    `${base} -H 'Content-Type: application/json' -d ${shellQuote(JSON.stringify(body))}`
  switch (runtime) {
    case 'laya':
      return json({
        state: 'The customer was charged twice.',
        questions: { team: { type: 'choice', instructions: 'Which team should help?',
          criteria: { billing: 'payments and refunds', general: 'general assistance' } } }
      })
    case 'kokoro':
      return json({ model, input: 'Hello from LLMProxy.', voice: 'af_heart',
        response_format: 'wav' }) + ' --output speech.wav'
    case 'diffusers':
      return json({ model, prompt: 'A lighthouse on a hill at sunset',
        size: '512x512', n: 1, response_format: 'b64_json' })
    case 'faster-whisper':
      return `${base} -F ${shellQuote('model=' + model)} -F 'file=@./audio.wav'`
    default:
      return json({ model, messages: [{ role: 'user', content: 'Hello!' }], max_tokens: 64 })
  }
}
