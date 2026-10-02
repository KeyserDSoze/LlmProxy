import { FormEvent, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { AdminTestResult, Model, SystemOneStatus } from './types'

const defaultClassifierBody = JSON.stringify({
  state: { document: 'The customer reports a duplicate card charge' },
  questions: { billing: { type: 'noul', instructions: 'Is this request about billing?' } }
}, null, 2)

export default function Playground({ models }: { models: Model[] }) {
  const activeModels = useMemo(() => models.filter(model => model.enabled), [models])
  const [model, setModel] = useState('')
  const [systemPrompt, setSystemPrompt] = useState('You are a concise assistant.')
  const [userPrompt, setUserPrompt] = useState('Reply with exactly: LlmProxy model test OK')
  const [chatResult, setChatResult] = useState<AdminTestResult | null>(null)
  const [classifierStatus, setClassifierStatus] = useState<SystemOneStatus | null>(null)
  const [classifierBody, setClassifierBody] = useState(defaultClassifierBody)
  const [classifierResult, setClassifierResult] = useState<AdminTestResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [running, setRunning] = useState<'chat' | 'classifier' | null>(null)

  useEffect(() => {
    if (!model && activeModels[0]) setModel(activeModels[0].publicName)
  }, [activeModels, model])

  useEffect(() => {
    void api.systemOneStatus().then(setClassifierStatus).catch(err => setError(err instanceof Error ? err.message : String(err)))
  }, [])

  async function runChat(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setRunning('chat')
    try {
      setChatResult(await api.testChat({ model, systemPrompt, userPrompt, maxTokens: 256, temperature: 0.2 }))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setRunning(null)
    }
  }

  async function runClassifier(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setRunning('classifier')
    try {
      const payload = JSON.parse(classifierBody) as unknown
      setClassifierResult(await api.testSystemOne(payload))
      setClassifierStatus(await api.systemOneStatus())
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setRunning(null)
    }
  }

  return <div className="stack">
    {error && <div className="error">{error}</div>}
    <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>Model chat test</h2><span>Uses routing + capacity admission</span></div>
        <form onSubmit={runChat}>
          <label>Active logical model<select value={model} onChange={event => setModel(event.target.value)} required>
            {activeModels.length === 0 && <option value="">No active models</option>}
            {activeModels.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}
          </select></label>
          <label>System prompt<textarea value={systemPrompt} onChange={event => setSystemPrompt(event.target.value)} rows={4} /></label>
          <label>User prompt<textarea value={userPrompt} onChange={event => setUserPrompt(event.target.value)} rows={7} required /></label>
          <button className="primary" disabled={!model || running !== null}>{running === 'chat' ? 'Running…' : 'Run chat test'}</button>
        </form>
      </section>
      <ResultPanel title="Chat test result" result={chatResult} />
    </div>

    <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>System One classifier</h2><span>{classifierStatus?.enabled ? 'Enabled' : 'Disabled'}</span></div>
        <div className="statusGrid">
          <div><span>Public endpoint</span><strong>{classifierStatus?.publicEndpoint ?? '/v1/systemone'}</strong></div>
          <div><span>Upstream</span><strong>{classifierStatus?.upstreamEndpoint ?? 'Not configured'}</strong></div>
          <div><span>Bearer auth</span><strong>{classifierStatus?.apiKeyConfigured ? 'Configured' : 'Not configured'}</strong></div>
          <div><span>Timeout</span><strong>{classifierStatus ? String(classifierStatus.timeoutSeconds) + 's' : '—'}</strong></div>
        </div>
        {classifierStatus?.configurationError && <div className="error">{classifierStatus.configurationError}</div>}
        <form onSubmit={runClassifier}>
          <label>Classifier request body<textarea className="codeArea" value={classifierBody} onChange={event => setClassifierBody(event.target.value)} rows={16} required /></label>
          <button className="primary" disabled={!classifierStatus?.enabled || running !== null}>{running === 'classifier' ? 'Running…' : 'Run classifier test'}</button>
        </form>
      </section>
      <ResultPanel title="Classifier result" result={classifierResult} />
    </div>
  </div>
}

function ResultPanel({ title, result }: { title: string; result: AdminTestResult | null }) {
  return <section className="panel formPanel">
    <div className="panelTitle tuningTitle"><h2>{title}</h2><span>{result ? 'HTTP ' + result.statusCode : 'Not run yet'}</span></div>
    {!result ? <p className="muted">Run the diagnostic to see the exact request, selected runtime and raw response.</p> : <>
      <div className="statusGrid">
        <div><span>Success</span><strong>{result.success ? 'Yes' : 'No'}</strong></div>
        <div><span>Latency</span><strong>{result.latencyMilliseconds} ms</strong></div>
        <div><span>Request ID</span><strong className="mono">{result.requestId}</strong></div>
        {result.nodeName && <div><span>Node</span><strong>{result.nodeName}</strong></div>}
      </div>
      {result.requestBody && <><h3>Request</h3><pre className="payload">{pretty(result.requestBody)}</pre></>}
      {result.responseBody && <><h3>Response</h3><pre className="payload">{pretty(result.responseBody)}</pre></>}
    </>}
  </section>
}

function pretty(value: string) {
  try { return JSON.stringify(JSON.parse(value), null, 2) } catch { return value }
}
