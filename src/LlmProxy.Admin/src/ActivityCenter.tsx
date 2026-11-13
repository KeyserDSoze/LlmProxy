import { useEffect, useRef, useState } from 'react'
import { summarizeOperations } from './backgroundOperations'
import type { BackgroundOperation, OperationStatus } from './backgroundOperations'
import { useT } from './uiPreferences'
import type { TranslationKey } from './uiPreferences'

const statusTone: Record<OperationStatus, string> = { running: 'info', attention: 'warning', failed: 'danger', done: 'success' }
const statusLabel: Record<OperationStatus, TranslationKey> = {
  running: 'activity.status.running', attention: 'activity.status.attention', failed: 'activity.status.failed', done: 'activity.status.done'
}

/**
 * Floating dock shown on every page while something runs in the background (Agent update, model download or start,
 * gateway update). The operator can leave the page that started it, watch progress here, and come back to the right
 * page with one click when it has finished.
 */
export default function ActivityCenter({ operations, onOpen, onDismiss, onClearFinished }: {
  operations: BackgroundOperation[]
  onOpen: (href: string) => void
  onDismiss: (id: string) => void
  onClearFinished: () => void
}) {
  const t = useT()
  const [open, setOpen] = useState(false)
  const dock = useRef<HTMLDivElement>(null)
  const summary = summarizeOperations(operations)
  const finished = summary.done + summary.failed

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  useEffect(() => { if (operations.length === 0) setOpen(false) }, [operations.length])

  if (operations.length === 0) return null

  const label = summary.tone === 'attention' ? t('activity.attention')
    : summary.tone === 'failed' ? t('activity.failed')
    : summary.tone === 'running' ? `${summary.running} ${t('activity.running')}`
    : t('activity.done')

  return <div className="activityDock" data-tone={summary.tone} ref={dock}>
    {open && <section id="activity-panel" className="activityPanel" aria-label={t('activity.title')}>
      <header className="activityPanelHeader">
        <strong>{t('activity.title')}</strong>
        <span className="activityPanelActions">
          {finished > 0 && <button type="button" className="secondary compact" onClick={onClearFinished}>{t('activity.clear')}</button>}
          <button type="button" className="secondary compact" aria-label={t('activity.close')} onClick={() => setOpen(false)}>×</button>
        </span>
      </header>
      <ul className="activityList">
        {operations.map(operation => <li key={operation.id} className={`activityItem activityItem--${operation.status}`}>
          <div className="activityItemHead">
            <span className={'pill pill-' + statusTone[operation.status]}><i aria-hidden="true" />{t(statusLabel[operation.status])}</span>
            <strong>{operation.title}</strong>
          </div>
          <p className="activityItemDetail">{operation.detail}</p>
          {operation.status === 'running' && (operation.progress != null
            ? <progress aria-label={t('activity.progress')} max={100} value={operation.progress} />
            : <progress aria-label={t('activity.progress')} />)}
          <div className="activityItemActions">
            <button type="button" className="secondary compact" onClick={() => { onOpen(operation.href); setOpen(false) }}>{t('activity.open')}</button>
            {(operation.status === 'done' || operation.status === 'failed') &&
              <button type="button" className="secondary compact" onClick={() => onDismiss(operation.id)}>{t('activity.dismiss')}</button>}
          </div>
        </li>)}
      </ul>
    </section>}
    <button type="button" className="activityButton" aria-expanded={open} aria-controls="activity-panel" onClick={() => setOpen(value => !value)}>
      {summary.tone === 'running' && <span className="miniSpinner" aria-hidden="true" />}
      <span>{label}</span>
    </button>
    <div className="srOnly" role="status" aria-live="polite">{label}</div>
  </div>
}
