import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { api } from './api'
import { originsHelp, parseOrigins } from './OriginsEditor'

/** Site-wide browser origins (CORS) that may call the API with any key. */
export default function CorsSettingsPanel({ canWrite }: { canWrite: boolean }) {
  const [loaded, setLoaded] = useState(false)
  const [text, setText] = useState('')
  const [maxAge, setMaxAge] = useState(600)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    let live = true
    api.corsSettings().then(settings => {
      if (!live) return
      setText(settings.allowedOrigins.join('\n'))
      setMaxAge(settings.maxAgeSeconds)
      setLoaded(true)
    }, reason => { if (live) { setError(reason instanceof Error ? reason.message : String(reason)); setLoaded(true) } })
    return () => { live = false }
  }, [])

  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true); setError(null); setSaved(false)
    try {
      const settings = await api.updateCorsSettings({ allowedOrigins: parseOrigins(text), maxAgeSeconds: maxAge })
      setText(settings.allowedOrigins.join('\n'))
      setMaxAge(settings.maxAgeSeconds)
      setSaved(true)
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(false) }
  }

  return <section className="panel formPanel corsPanel" aria-label="Browser access (CORS)">
    <div className="panelTitle tuningTitle">
      <h2>Browser access (CORS)</h2>
      <span>Applies to every API key · propagates to all gateway replicas within about 20 seconds</span>
    </div>
    <form onSubmit={event => void save(event)}>
      <label>Allowed browser origins for all keys (one per line)
        <textarea aria-label="Site-wide allowed origins" rows={4} spellCheck={false} disabled={!canWrite || !loaded}
          placeholder={'https://app.example.com\nhttp://localhost:5173'} value={text}
          onChange={event => { setText(event.target.value); setSaved(false) }} />
      </label>
      <label>Preflight cache (seconds)
        <input aria-label="Preflight cache seconds" type="number" min={0} max={86400} disabled={!canWrite || !loaded}
          value={maxAge} onChange={event => { setMaxAge(Number(event.target.value)); setSaved(false) }} />
      </label>
      <p className="muted">{originsHelp} Each API key can add its own origins below; a key that lists origins is only usable from a browser on one of them or on a site-wide origin.</p>
      {error && <div className="error" role="alert">{error}</div>}
      {saved && <div className="notice" role="status">Browser access settings saved.</div>}
      <div><button className="primary" disabled={!canWrite || busy || !loaded}>Save browser access</button></div>
    </form>
  </section>
}
