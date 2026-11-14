import { ModelCapabilitySummary } from './ModelCapabilityPanel'
import HuggingFaceAccessGuide from './HuggingFaceAccessGuide'
import StartupDiagnosis from './StartupDiagnosis'
import { CopyButton, InfoIcon } from './UiPrimitives'
import { useT } from './uiPreferences'
import type { TranslationKey } from './uiPreferences'
import type { AdminOperation, FamilyProfileInfo, ManagedInstallation, ModelRevisionStatus, ModelStartJob, ModelVerification } from './types'

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

export type VerificationState = ModelVerification | 'loading' | { error: string }

export default function InstalledModelCards({ nodeId, installations, startJobs, operations = [], upstreamRevisions, canWrite, busy,
  agentAvailable, verifications, onVerify, onAction, onCancelStart, onEdit, onEditProperties, onCapabilities, onBenchmark }: {
  nodeId?: string
  installations: ManagedInstallation[]
  startJobs: ModelStartJob[]
  operations?: AdminOperation[]
  upstreamRevisions: ModelRevisionStatus[]
  canWrite: boolean
  busy: string | null
  agentAvailable: boolean
  verifications: Record<string, VerificationState>
  onVerify: (deploymentId: string) => void
  onAction: (kind: InstalledAction, deploymentId: string) => void
  onCancelStart: (jobId: string) => void
  onEdit: (item: ManagedInstallation) => void
  onEditProperties?: (item: ManagedInstallation) => void
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
      const working = operations.find(entry => entry.deploymentId === item.id && entry.status === 'Running')
      const typed = typedRuntimes.includes(item.runtime ?? '')
      // Embedding and streaming-voice models have no chat features to summarise or tune.
      const otherKind = item.surface === 'Embedding' || item.surface === 'Realtime'
      const revision = upstreamRevisions.find(entry => entry.installationId === item.managedInstallationId)
      const failure = job && (job.status === 'failed' || job.status === 'interrupted') && job.error
        ? job.error : item.agentState?.error
      const endpoint = managedPublicEndpoint(item.runtime, name, item.surface)
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
        {!running && item.agentState?.autoStart && <p className={'installedModelHint' + (item.agentState.autoStart === 'gave_up' ? ' installedModelHint--warn' : '')} role="status">
          {item.agentState.autoStart === 'gave_up' ? 'Automatic restart stopped. ' : 'Restarting automatically. '}{item.agentState.autoStartDetail}</p>}

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

        {!otherKind && <div className="installedModelRow">
          <span className="installedModelLabel">{t('catalog.capabilities')}</span>
          <ModelCapabilitySummary profile={item.capabilities} />
        </div>}
        {!typed && item.properties && item.surface !== 'Realtime' && <div className="installedModelRow">
          <span className="installedModelLabel">{t('props.published')}</span>
          <span className="publishedProperties">
            <span className="publishedChips">
              {item.surface !== 'Embedding' && <>
                <PropertyChip on={item.properties.tools} label={t('props.tools')} />
                <PropertyChip on={item.properties.vision} label={t('props.vision')} />
                <PropertyChip on={item.properties.reasoning} label={t('props.reasoning')} />
                <PropertyChip on={item.properties.streaming} label={t('props.streaming')} />
              </>}
              <span className="pill pill-neutral">{t('props.contextShort')} {item.properties.contextWindow ? item.properties.contextWindow.toLocaleString() : '—'}</span>
              {item.surface !== 'Embedding' && <span className="pill pill-neutral">{t('props.outputShort')} {item.properties.maxOutputTokens ? item.properties.maxOutputTokens.toLocaleString() : '—'}</span>}
            </span>
            {canWrite && onEditProperties && <button type="button" className="secondary compact" onClick={() => onEditProperties(item)}>{t('props.edit')}</button>}
          </span>
        </div>}
        {!typed && !otherKind && <div className="installedModelRow">
          <span className="installedModelLabel">{t('profile.title')}</span>
          <ModelProfile profile={item.agentState?.familyProfile} />
        </div>}
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
            <code>{item.surface === 'Realtime' ? 'GET' : 'POST'} {endpoint}</code>
            <CopyButton className="secondary compact" value={window.location.origin + endpoint}>
              {t('catalog.copyEndpoint')}</CopyButton>
            <CopyButton className="secondary compact" title={t('catalog.copyCurlHelp')} value={managedCurlExample(item.runtime, name, window.location.origin, item.surface)}>
              {t('catalog.copyCurl')}</CopyButton>
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

        {verifications[item.id] && verifications[item.id] !== 'loading' &&
          <VerificationResults state={verifications[item.id] as ModelVerification | { error: string }} />}

        <footer className="installedModelActions">
          {working && <p className="installedModelWorking" role="status"><span className="miniSpinner" aria-hidden="true" /> {working.title}: {working.detail === 'Queued' ? 'working…' : working.detail}</p>}
          <button className={running ? 'secondary' : 'primary'}
            disabled={!canWrite || (busy !== null || !!working) || running || starting}
            onClick={() => onAction('start', item.id)}>{t('catalog.start')}</button>
          {running && !item.enabled &&
            <button className="primary" disabled={!canWrite || (busy !== null || !!working) || !agentAvailable}
              title={t('catalog.healthTip')}
              onClick={() => onAction('activate', item.id)}>{t('catalog.activate')}</button>}
          <button className="secondary" disabled={!canWrite || (busy !== null || !!working) || !running}
            onClick={() => onAction('stop', item.id)}>{t('catalog.stop')}</button>
          <button className="secondary" disabled={!canWrite || (busy !== null || !!working) || !agentAvailable || typed || running}
            title={typed ? 'This typed media runtime has fixed concurrency and no Chat-token parameters.'
              : running ? t('catalog.startStopped') : t('catalog.editStopped')}
            onClick={() => onEdit(item)}>{t('catalog.edit')}</button>
          {!typed && <button className="secondary" disabled={!running}
            onClick={() => onBenchmark(item.id)}>{t('catalog.benchmark')}</button>}
          {!typed && <button className="secondary" disabled={!canWrite || !running || verifications[item.id] === 'loading'}
            title={t('verify.hint')} onClick={() => onVerify(item.id)}>
            {verifications[item.id] === 'loading' ? t('verify.running') : t('verify.action')}</button>}
          {item.capabilities && <button type="button" className="secondary capabilityActionButton"
            aria-label={`${t('cap.details')} · ${t('catalog.capabilities')} · ${item.logicalModel ?? item.providerModelName ?? 'Model'}`}
            title={t('cap.inspectDetails')} onClick={() => onCapabilities(item)}>
            <InfoIcon /> {t('cap.details')}
          </button>}
          <span className="installedModelSpacer" />
          <button className="danger" disabled={!canWrite || (busy !== null || !!working)}
            onClick={() => onAction('remove', item.id)}>{t('catalog.uninstall')}</button>
        </footer>
        <div className="installedModelId mono muted">{item.managedInstallationId}</div>
      </article>
    })}
  </div>
}

function PropertyChip({ on, label }: { on: boolean; label: string }) {
  return <span className={'pill ' + (on ? 'pill-success' : 'pill-neutral')}>{on ? '✓' : '—'} {label}</span>
}

function typedRuntimeSummary(runtime?: string) {
  return runtime === 'faster-whisper' ? 'Audio input ≤25 MiB' :
    runtime === 'laya' ? 'Typed English classifier' :
    runtime === 'kokoro' ? 'English voice af_heart · WAV' : 'NVIDIA PNG · 512–1024'
}

function shellQuote(value: string) { return "'" + value.replace(/'/g, "'\\''") + "'" }

export function managedPublicEndpoint(runtime: string | undefined, model: string, surface?: string | null): string {
  if (surface === 'Embedding') return '/v1/embeddings'
  if (surface === 'Realtime') return '/v1/realtime?model=' + encodeURIComponent(model)
  switch (runtime) {
    case 'laya': return '/v1/systemone/' + encodeURIComponent(model)
    case 'kokoro': return '/v1/audio/speech'
    case 'diffusers': return '/v1/images/generations'
    case 'faster-whisper': return '/v1/audio/transcriptions'
    default: return '/v1/chat/completions'
  }
}

/** Ready-to-copy public API examples. Use the logical alias, never upstream HF name. */
export function managedCurlExample(runtime: string | undefined, model: string, origin: string, surface?: string | null): string {
  const endpoint = managedPublicEndpoint(runtime, model, surface)
  if (surface === 'Realtime')
    return `websocat -H "Authorization: Bearer $LLMPROXY_API_KEY" ${shellQuote(origin.replace(/^http/, 'ws') + endpoint)}`
  const base = `curl --fail-with-body -sS -X POST ${shellQuote(origin + endpoint)} -H "Authorization: Bearer $LLMPROXY_API_KEY"`
  const json = (body: object) =>
    `${base} -H 'Content-Type: application/json' -d ${shellQuote(JSON.stringify(body))}`
  if (surface === 'Embedding') return json({ model, input: ['The first sentence.', 'The second sentence.'] })
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

function ModelProfile({ profile }: { profile?: FamilyProfileInfo | null }) {
  const t = useT()
  if (!profile) return <span className="muted">{t('profile.none')}</span>
  return <div className="modelProfile">
    <strong>{profile.title}</strong>
    {!profile.enabled && <span className="muted"> · {t('profile.disabled')}</span>}
    {profile.appliedArguments.length > 0 && <div><span className="muted">{t('profile.applied')}: </span>
      <code>{profile.appliedArguments.join(' ')}</code></div>}
    {profile.overriddenByOperator.length > 0 && <div><span className="muted">{t('profile.overridden')}: </span>
      <code>{profile.overriddenByOperator.join(' ')}</code></div>}
    {profile.requirement && <div className="notice modelProfileRequirement"><strong>{t('profile.requirement')}: </strong>{profile.requirement}</div>}
    <div className="muted modelProfileSource">{t('profile.source')}: <a href={profile.source} target="_blank" rel="noopener noreferrer">{profile.source}</a> · {t('profile.checked')} {profile.verifiedOn}</div>
  </div>
}

const checkLabel: Record<string, TranslationKey> = {
  chat: 'verify.chat', reasoning: 'verify.reasoning', streaming: 'verify.streaming', tools: 'verify.tools'
}
const statusLabel: Record<string, TranslationKey> = {
  ok: 'verify.ok', warning: 'verify.warning', failed: 'verify.failed', inconclusive: 'verify.inconclusive'
}
const statusTone: Record<string, string> = { ok: 'success', warning: 'warning', failed: 'danger', inconclusive: 'neutral' }

function VerificationResults({ state }: { state: ModelVerification | { error: string } }) {
  const t = useT()
  if ('error' in state) return <p className="error" role="alert">{state.error}</p>
  return <section className="modelVerification" aria-label={t('verify.title')}>
    <strong>{t('verify.title')}</strong>
    <ul>
      {state.checks.map(check => <li key={check.id}>
        <span className={'pill pill-' + (statusTone[check.status] ?? 'neutral')}><i aria-hidden="true" />
          {t(statusLabel[check.status] ?? 'verify.inconclusive')}</span>
        <span className="modelVerificationName">{checkLabel[check.id] ? t(checkLabel[check.id]) : check.id}</span>
        <span className="muted">{check.detail}</span>
      </li>)}
    </ul>
  </section>
}
