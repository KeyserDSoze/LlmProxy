import { FormEvent, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Tabs } from './UiPrimitives'
import type { AdminTestResult, Model, SystemOneStatus } from './types'

const defaultClassifierBody = JSON.stringify({ state: { document: 'The customer reports a duplicate card charge' }, questions: { billing: { type: 'noul', instructions: 'Is this request about billing?' } } }, null, 2)

export default function PlaygroundExperience({ models }: { models: Model[] }) {
  const [tab, setTab] = useState<'chat' | 'classifier'>('chat')
  const openAiModels = useMemo(() => models.filter(model => model.enabled && (model.surface ?? 'OpenAi') === 'OpenAi'), [models])
  const systemOneModels = useMemo(() => models.filter(model => model.enabled && model.surface === 'SystemOne'), [models])
  const [model, setModel] = useState('')
  const [classifierModel, setClassifierModel] = useState('')
  const [systemPrompt, setSystemPrompt] = useState('You are a concise assistant.')
  const [userPrompt, setUserPrompt] = useState('Reply with exactly: LlmProxy model test OK')
  const [chatResult, setChatResult] = useState<AdminTestResult | null>(null)
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
  useEffect(() => { void api.systemOneStatus().then(setClassifierStatus).catch(err => setError(err instanceof Error ? err.message : String(err))) }, [])

  async function runChat(event: FormEvent) {
    event.preventDefault(); setError(null); setRunning('chat')
    try { setChatResult(await api.testChat({ model, systemPrompt, userPrompt, maxTokens: 256, temperature: 0.2 })) }
    catch (err) { setError(err instanceof Error ? err.message : String(err)) }
    finally { setRunning(null) }
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

    {tab === 'chat' && <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>Model chat test</h2><span>OpenAI surface · routed</span></div>
        <form onSubmit={runChat}>
          <label>Active OpenAI logical model<select value={model} onChange={event => setModel(event.target.value)} required>{openAiModels.length === 0 && <option value="">No active OpenAI models</option>}{openAiModels.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}</select></label>
          <label>System prompt<textarea value={systemPrompt} onChange={event => setSystemPrompt(event.target.value)} rows={4} /></label>
          <label>User prompt<textarea value={userPrompt} onChange={event => setUserPrompt(event.target.value)} rows={7} required /></label>
          <button className="primary" disabled={!model || running !== null}>{running === 'chat' ? 'Running…' : 'Run chat test'}</button>
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
