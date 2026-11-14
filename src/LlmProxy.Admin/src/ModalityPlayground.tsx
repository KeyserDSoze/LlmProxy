import { FormEvent, useEffect, useMemo, useState } from 'react'
import { testModality, type ModalityTestResponse } from './api'
import type { Model } from './types'

export type PlaygroundModality = 'image' | 'speech' | 'transcription' | 'embedding'

type Spec = { title: string; endpoint: string; surface: string; noun: string }

const specs: Record<PlaygroundModality, Spec> = {
  image: { title: 'Image generation test', endpoint: 'POST /v1/images/generations', surface: 'ImageGeneration', noun: 'image' },
  speech: { title: 'Text to speech test', endpoint: 'POST /v1/audio/speech', surface: 'SpeechSynthesis', noun: 'speech' },
  transcription: { title: 'Speech to text test', endpoint: 'POST /v1/audio/transcriptions', surface: 'AudioTranscription', noun: 'transcription' },
  embedding: { title: 'Embeddings test', endpoint: 'POST /v1/embeddings', surface: 'Embedding', noun: 'embedding' }
}

type Shown = { summary: ModalityTestResponse; imageUrl?: string; audioUrl?: string; lines: string[] }

/** One test form per kind of model. `models` are the ones that can take requests right now. */
export default function ModalityPlayground({ modality, models }: { modality: PlaygroundModality; models: Model[] }) {
  const spec = specs[modality]
  const [model, setModel] = useState('')
  const [prompt, setPrompt] = useState('A small red lighthouse on a rocky coast at sunrise')
  const [size, setSize] = useState('512x512')
  const [speechText, setSpeechText] = useState('Hello from LlmProxy. This is a speech test.')
  const [voice, setVoice] = useState('af_heart')
  const [format, setFormat] = useState('wav')
  const [file, setFile] = useState<File | null>(null)
  const [language, setLanguage] = useState('')
  const [inputs, setInputs] = useState('LlmProxy routes requests to local models.\nA second sentence to embed.')
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [shown, setShown] = useState<Shown | null>(null)

  useEffect(() => {
    if (!models.some(item => item.publicName === model)) setModel(models[0]?.publicName ?? '')
  }, [models, model])
  // Object URLs for media are released when the result is replaced or the tab is left.
  useEffect(() => () => {
    if (shown?.imageUrl) URL.revokeObjectURL(shown.imageUrl)
    if (shown?.audioUrl) URL.revokeObjectURL(shown.audioUrl)
  }, [shown])
  useEffect(() => { setShown(null); setError(null) }, [modality])

  const inputLines = useMemo(() => inputs.split('\n').map(line => line.trim()).filter(Boolean), [inputs])

  async function run(event: FormEvent) {
    event.preventDefault()
    setRunning(true); setError(null); setShown(null)
    try {
      if (modality === 'image') {
        const summary = await testModality('images', { model, prompt, n: 1, size, response_format: 'b64_json' })
        const first = (JSON.parse(summary.text ?? '{}') as { data?: { b64_json?: string; url?: string }[] }).data?.[0]
        const imageUrl = first?.b64_json ? `data:image/png;base64,${first.b64_json}` : first?.url
        setShown({ summary, imageUrl, lines: [] })
      } else if (modality === 'speech') {
        const summary = await testModality('speech', { model, input: speechText, voice, response_format: format })
        setShown({ summary, audioUrl: summary.blob ? URL.createObjectURL(summary.blob) : undefined,
          lines: summary.blob ? [`${summary.contentType || 'audio'} · ${(summary.blob.size / 1024).toFixed(1)} KiB`] : [summary.text ?? ''] })
      } else if (modality === 'transcription') {
        if (!file) throw new Error('Choose an audio file first.')
        const form = new FormData()
        form.append('model', model)
        form.append('file', file, file.name)
        if (language.trim()) form.append('language', language.trim())
        const summary = await testModality('transcriptions', form)
        const parsed = JSON.parse(summary.text ?? '{}') as { text?: string }
        setShown({ summary, lines: [parsed.text ?? summary.text ?? ''] })
      } else {
        const summary = await testModality('embeddings', { model, input: inputLines.length === 1 ? inputLines[0] : inputLines })
        const parsed = JSON.parse(summary.text ?? '{}') as { data?: { embedding?: number[] | string }[]; usage?: { prompt_tokens?: number } }
        const vectors = parsed.data ?? []
        const first = vectors[0]?.embedding
        const dimensions = Array.isArray(first) ? first.length : null
        setShown({ summary, lines: [
          `${vectors.length} vector${vectors.length === 1 ? '' : 's'}${dimensions ? ` · ${dimensions} dimensions` : ''}${parsed.usage?.prompt_tokens != null ? ` · ${parsed.usage.prompt_tokens} prompt tokens` : ''}`,
          Array.isArray(first) ? `first vector: [${first.slice(0, 8).map(value => value.toFixed(4)).join(', ')}, …]` : ''
        ].filter(Boolean) })
      }
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally { setRunning(false) }
  }

  const empty = models.length === 0
  return <div className="gridTwo">
    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>{spec.title}</h2><span>{spec.endpoint} · routed</span></div>
      <form onSubmit={run}>
        <label>Active {spec.noun} model<select aria-label={`Active ${spec.noun} model`} value={model} onChange={event => setModel(event.target.value)} required>
          {empty && <option value="">No active {spec.noun} models: start one in its own workspace</option>}
          {models.map(item => <option key={item.id} value={item.publicName}>{item.publicName}</option>)}
        </select></label>
        {modality === 'image' && <>
          <label>Prompt<textarea aria-label="Image prompt" value={prompt} onChange={event => setPrompt(event.target.value)} rows={4} required /></label>
          <label>Size<select aria-label="Image size" value={size} onChange={event => setSize(event.target.value)}>
            {['256x256', '512x512', '1024x1024'].map(value => <option key={value}>{value}</option>)}</select></label>
        </>}
        {modality === 'speech' && <>
          <label>Text<textarea aria-label="Text to speak" value={speechText} onChange={event => setSpeechText(event.target.value)} rows={4} maxLength={4096} required /></label>
          <label>Voice<input aria-label="Voice" value={voice} onChange={event => setVoice(event.target.value)} required /></label>
          <label>Format<select aria-label="Audio format" value={format} onChange={event => setFormat(event.target.value)}>
            {['wav', 'mp3'].map(value => <option key={value}>{value}</option>)}</select></label>
        </>}
        {modality === 'transcription' && <>
          <label>Audio file<input aria-label="Audio file" type="file" accept="audio/*,.wav,.mp3,.m4a,.ogg,.flac,.webm" onChange={event => setFile(event.target.files?.[0] ?? null)} required /></label>
          <label>Language (optional)<input aria-label="Language" value={language} placeholder="en" onChange={event => setLanguage(event.target.value)} /></label>
        </>}
        {modality === 'embedding' && <label>Texts (one per line)
          <textarea aria-label="Texts to embed" value={inputs} onChange={event => setInputs(event.target.value)} rows={6} required /></label>}
        <div className="actions"><button className="primary" disabled={!model || running || (modality === 'embedding' && inputLines.length === 0)}>
          {running ? 'Running…' : `Run ${spec.noun} test`}</button></div>
      </form>
    </section>
    <section className="panel">
      <div className="panelTitle"><h2>Result</h2></div>
      {error && <div className="error" role="alert">{error}</div>}
      {!error && !shown && <p className="muted">{running ? 'Waiting for the model…' : 'Run a test to see the answer here.'}</p>}
      {shown && <div className="stack" role="status">
        <p><strong>HTTP {shown.summary.status}</strong> · {shown.summary.latencyMilliseconds} ms{shown.summary.requestId ? <> · <span className="mono">{shown.summary.requestId}</span></> : null}</p>
        {shown.imageUrl && <img alt="Generated image" src={shown.imageUrl} style={{ maxWidth: '100%', borderRadius: 8 }} />}
        {shown.audioUrl && <audio controls src={shown.audioUrl} aria-label="Generated speech" />}
        {shown.lines.map((line, index) => <p key={index} className="mono" style={{ whiteSpace: 'pre-wrap' }}>{line}</p>)}
      </div>}
    </section>
  </div>
}
