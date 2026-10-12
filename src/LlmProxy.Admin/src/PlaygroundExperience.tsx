import { FormEvent, useEffect, useMemo, useRef, useState } from 'react'
import { api, testChatStream } from './api'
import type { ChatTestProgress } from './chatTestStream'
import { useRouteTab } from './adminNavigation'
import { Tabs } from './UiPrimitives'
import type { AdminTestResult, Model, ModelRouteDiagnosis, SystemOneStatus } from './types'
import { useT } from './uiPreferences'

const defaultClassifierBody = JSON.stringify({ state: { document: 'The customer reports a duplicate card charge' }, questions: { billing: { type: 'noul', instructions: 'Is this request about billing?' } } }, null, 2)

export default function PlaygroundExperience({ models }: { models: Model[] }) {
  const t = useT()
  const [tab, setTab] = useRouteTab<'chat' | 'classifier'>('tab', ['chat', 'classifier'], 'chat')
  const openAiModels = useMemo(() => models.filter(model => model.enabled && (model.surface ?? 'OpenAi') === 'OpenAi'), [models])
  const systemOneModels = useMemo(() => models.filter(model => model.enabled && model.surface === 'SystemOne'), [models])
  const [model, setModel] = useState('')
  const [classifierModel, setClassifierModel] = useState('')
  const [systemPrompt, setSystemPrompt] = useState('You are a concise assistant.')
  const [userPrompt, setUserPrompt] = useState('Reply with exactly: LlmProxy model test OK')
  const [chatResult, setChatResult] = useState<AdminTestResult | null>(null)
  const [maxChatTokens, setMaxChatTokens] = useState(8)
  const [chatProgress, setChatProgress] = useState<ChatTestProgress | null>(null)
  const runningChatController = useRef<AbortController | null>(null)
  const [routeDiagnosis, setRouteDiagnosis] = useState<ModelRouteDiagnosis | null>(null)
  const [repairing, setRepairing] = useState(false)
  const [classifierStatus, setClassifierStatus] = useState<SystemOneStatus | null>(null)
  const [classifierBody, setClassifierBody] = useState(defaultClassifierBody)
  const [classifierResult, setClassifierResult] = useState<AdminTestResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [running, setRunning] = useState<'chat' | 'classifier' | null>(null)

  useEffect(() => { if (!model && openAiModels[0]) setModel(openAiModels[0].publicName) }, [openAiModels, model])
  useEffect(() => {
    if (!classifierModel) {
      const configured = classifierStatus?.defaultModel
      setClassifierModel(systemOneModels.find(item => item.publicName === configured)?.publicName ?? systemOneModels[0]?.publicName ?? '')
    }
  }, [systemOneModels, classifierStatus, classifierModel])
  useEffect(() => () => runningChatController.current?.abort(), [])
  useEffect(() => { void api.systemOneStatus().then(setClassifierStatus).catch(err => setError(err instanceof Error ? err.message : String(err))) }, [])
  useEffect(() => {
    setRouteDiagnosis(null)
    if (!model || typeof api.chatRouteDiagnosis !== 'function') return
    let active = true
    void api.chatRouteDiagnosis(model).then(result => {
      if (active) setRouteDiagnosis(result)
    }).catch(() => {})
    return () => { active = false }
  }, [model])

  async function activate(deploymentId: string) {
    setRepairing(true); setError(null)
    try {
      await api.activateManagedDeployment(deploymentId)
      setRouteDiagnosis(await api.chatRouteDiagnosis(model))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason))
    } finally { setRepairing(false) }
  }

  async function runChat(event: FormEvent) {
    event.preventDefault()
    if (runningChatController.current) return
    const controller = new AbortController()
    runningChatController.current = controller
    setError(null); setChatResult(null); setChatProgress(null); setRunning('chat')
    try {
      const result = await testChatStream({
        model, systemPrompt, userPrompt, maxTokens: maxChatTokens, temperature: 0
      }, setChatProgress, controller.signal)
      setChatResult(result)
      if (!result.success && result.message) setError(result.message)
    } catch (err) {
      setError(controller.signal.aborted ? t('playground.cancelled') :
        err instanceof Error ? err.message : String(err))
      void api.chatRouteDiagnosis(model).then(setRouteDiagnosis).catch(() => {})
    } finally {
      runningChatController.current = null
      setChatProgress(null); setRunning(null)
    }
  }

  async function runClassifier(event: FormEvent) {
    event.preventDefault(); setError(null); setRunning('classifier')
    try {
      setClassifierResult(await api.testSystemOne(JSON.parse(classifierBody) as unknown, classifierModel))
      setClassifierStatus(await api.systemOneStatus())
    } catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setRunning(null) }
  }

  return <div className="stack compactPage">
    {error && <div className="error">{error}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'chat', label: 'Model chat' }, { value: 'classifier', label: 'System One classifier' }]} />

    {tab === 'chat' && routeDiagnosis && routeDiagnosis.code !== 'candidate_configured' &&
      <section className="notice" role="status">
        <strong>{t('model.routeDiagnosisTitle')} · {routeDiagnosis.model}</strong>
        <p>{routeDiagnosis.message}</p>
        {routeDiagnosis.deployments.map(row => <div className="actions" key={row.deploymentId}>
          <strong>{row.nodeName}</strong>
          <span>{row.nodeStatus} · {row.routingEnabled ? t('model.routeOn') : t('model.routeOff')}</span>
          <span>{row.message}</span>
          {row.lastHealthError && <span className="muted">{t('model.lastHealthError')}: {row.lastHealthError}</span>}
          {row.managed && row.code === 'deployment_disabled' &&
            <button className="primary" disabled={repairing} onClick={() => void activate(row.deploymentId)}>
              {repairing ? t('model.checkingHealth') : t('catalog.activate')}
            </button>}
        </div>)}
        <p><a href={routeDiagnosis.nextAction === 'servers' ? '/admin/infrastructure?section=fleet' :
          routeDiagnosis.nextAction === 'routing' ? '/admin/routing' :
          routeDiagnosis.nextAction === 'models' ? '/admin/catalog?tab=deploy&panel=advanced' :
          '/admin/catalog?tab=deploy&panel=installed' }>{t('model.fixInManager')} →</a></p>
      </section>}

    {tab === 'chat' && <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>Model chat test</h2><span>OpenAI surface · routed</span></div>
        <form onSubmit={runChat}>
          <label>Active OpenAI logical model<select value={model} onChange={event => setModel(event.target.value)} required>{openAiModels.length === 0 && <option value="">No active OpenAI models</option>}{openAiModels.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}</select></label>
          <label>System prompt<textarea value={systemPrompt} onChange={event => setSystemPrompt(event.target.value)} rows={4} /></label>
          <label>User prompt<textarea value={userPrompt} onChange={event => setUserPrompt(event.target.value)} rows={7} required /></label>
          <label>{t('playground.outputTokens')}
            <select aria-label={t('playground.outputTokens')} value={maxChatTokens}
              disabled={running !== null} onChange={event => setMaxChatTokens(Number(event.target.value))}>
              {[1, 4, 8, 16, 32, 64, 128, 256].map(value =>
                <option key={value} value={value}>{value}</option>)}
            </select>
          </label>
          <p className="muted">{t('playground.slowModelHint')}</p>
          {running === 'chat' && <div role="status" className="notice">
            <strong>{t('playground.waitingForModel')}</strong>
            <p>{chatProgress ? `${chatProgress.secondsElapsed} s · ${t('playground.waitingNote')}` :
              t('playground.connecting')}</p>
          </div>}
          <div className="actions">
            <button className="primary" disabled={!model || running !== null}>
              {running === 'chat' ? t('playground.running') : 'Run chat test'}
            </button>
            {running === 'chat' && <button type="button" className="secondary"
              onClick={() => runningChatController.current?.abort()}>{t('playground.cancel')}</button>}
          </div>
        </form>
      </section>
      <ResultPanel title="Chat test result" result={chatResult} />
    </div>}

    {tab === 'classifier' && <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>System One classifier</h2><span>{systemOneModels.length ? `${systemOneModels.length} routed model${systemOneModels.length === 1 ? '' : 's'}` : 'No routed model'}</span></div>
        <div className="statusGrid">
          <div><span>Public endpoint</span><strong>{classifierStatus?.publicEndpoint ?? '/v1/systemone'}</strong></div>
          <div><span>Routing</span><strong>Model → deployment → node</strong></div>
          <div><span>Failover</span><strong>Up to 3 deployments</strong></div>
          <div><span>Timeout</span><strong>{classifierStatus ? String(classifierStatus.timeoutSeconds) + 's' : '—'}</strong></div>
        </div>
        {classifierStatus?.configurationError && systemOneModels.length === 0 && <div className="error">{classifierStatus.configurationError}</div>}
        <form onSubmit={runClassifier}>
          <label>System One logical model<select aria-label="System One logical model" value={classifierModel} onChange={event => setClassifierModel(event.target.value)} required>
            {systemOneModels.length === 0 && <option value="">No System One models</option>}
            {systemOneModels.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}
          </select></label>
          <label>Classifier request body<textarea className="codeArea" value={classifierBody} onChange={event => setClassifierBody(event.target.value)} rows={16} required /></label>
          <button className="primary" disabled={!classifierModel || running !== null}>{running === 'classifier' ? 'Running…' : 'Run classifier test'}</button>
        </form>
      </section>
      <ResultPanel title="Classifier result" result={classifierResult} />
    </div>}
  </div>
}

function ResultPanel({ title, result }: { title: string; result: AdminTestResult | null }) {
  return <section className="panel formPanel">
    <div className="panelTitle tuningTitle"><h2>{title}</h2><span>{result ? 'HTTP ' + result.statusCode : 'Not run yet'}</span></div>
    {!result ? <p className="muted">Run the diagnostic to see the exact request, selected deployment/node and raw response.</p> : <>
      <div className="statusGrid">
        <div><span>Success</span><strong>{result.success ? 'Yes' : 'No'}</strong></div>
        <div><span>Latency</span><strong>{result.latencyMilliseconds} ms</strong></div>
        <div><span>Request ID</span><strong className="mono">{result.requestId}</strong></div>
        {result.logicalModel && <div><span>Logical model</span><strong>{result.logicalModel}</strong></div>}
        {result.nodeName && <div><span>Node</span><strong>{result.nodeName}</strong></div>}
        {result.deploymentId && <div><span>Deployment</span><strong className="mono">{result.deploymentId}</strong></div>}
      </div>
      {result.requestBody && <><h3>Request</h3><pre className="payload">{pretty(result.requestBody)}</pre></>}
      {result.responseBody && <><h3>Response</h3><pre className="payload">{pretty(result.responseBody)}</pre></>}
    </>}
  </section>
}

function pretty(value: string) { try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value } }
