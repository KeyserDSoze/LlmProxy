import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Modal } from './UiPrimitives'

/** One origin per line (commas and spaces also separate entries). */
export function parseOrigins(text: string): string[] {
  return text.split(/[\s,]+/).map(item => item.trim()).filter(Boolean)
}

export const originsHelp = 'Web apps that may call the API directly from a browser (CORS), as scheme://host[:port] without a path, ' +
  'for example https://app.example.com or http://localhost:5173. Use * to allow any origin. ' +
  'Requests from servers or curl send no Origin header and are not affected; this does not replace keeping the key secret.'

export function OriginsSummary({ origins, empty }: { origins?: string[] | null; empty: string }) {
  if (!origins || origins.length === 0) return <span className="muted">{empty}</span>
  return <span className="originChips">{origins.map(origin => <code key={origin} className="originChip">{origin}</code>)}</span>
}

export function OriginsModal({ open, title, description, initial, busy, error, onSave, onClose }: {
  open: boolean
  title: string
  description?: string
  initial: string[]
  busy: boolean
  error: string | null
  onSave: (origins: string[]) => void | Promise<void>
  onClose: () => void
}) {
  const [text, setText] = useState('')
  useEffect(() => { if (open) setText(initial.join('\n')) }, [open, initial.join('|')])

  function submit(event: FormEvent) {
    event.preventDefault()
    void onSave(parseOrigins(text))
  }

  return <Modal open={open} title={title} description={description} onClose={onClose}>
    <form className="formPanel" onSubmit={submit}>
      <label>Allowed browser origins (one per line)
        <textarea aria-label="Allowed browser origins" rows={5} value={text} spellCheck={false}
          placeholder={'https://app.example.com\nhttp://localhost:5173'}
          onChange={event => setText(event.target.value)} />
      </label>
      <p className="muted">{originsHelp}</p>
      {error && <div className="error" role="alert">{error}</div>}
      <div className="modalActions">
        <button type="button" className="secondary" onClick={onClose}>Cancel</button>
        <button className="primary" disabled={busy}>Save origins</button>
      </div>
    </form>
  </Modal>
}
