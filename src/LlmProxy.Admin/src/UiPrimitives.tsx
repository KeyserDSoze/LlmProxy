import { useEffect, useRef } from 'react'
import type { KeyboardEvent, ReactNode } from 'react'
import { useT } from './uiPreferences'

export function Tabs<T extends string>({ value, onChange, items }: {
  value: T; onChange: (value: T) => void;
  items: Array<{ value: T; label: string; count?: number }>
}) {
  const handleKeys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowRight', 'ArrowLeft', 'Home', 'End'].includes(event.key)) return
    const index = items.findIndex(item => item.value === value)
    if (index < 0 || items.length === 0) return
    event.preventDefault()
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
      : (index + (event.key === 'ArrowRight' ? 1 : -1) + items.length) % items.length
    onChange(items[next].value)
    const buttons = event.currentTarget.querySelectorAll<HTMLButtonElement>('button[role="tab"]')
    buttons[next]?.focus()
  }
  return <div className="tabs" role="tablist" onKeyDown={handleKeys}>
    {items.map(item => <button key={item.value} type="button" role="tab" tabIndex={value === item.value ? 0 : -1}
      aria-selected={value === item.value} className={value === item.value ? 'tabButton active' : 'tabButton'}
      onClick={() => onChange(item.value)}>
      <span>{item.label}</span>{item.count !== undefined && <span className="tabCount">{item.count}</span>}
    </button>)}
  </div>
}

export function Modal({ open, title, description, onClose, children, className }: {
  open: boolean; title: string; description?: string; onClose: () => void;
  children: ReactNode; className?: string
}) {
  const t = useT()
  const panel = useRef<HTMLElement>(null)
  const onCloseRef = useRef(onClose)
  onCloseRef.current = onClose
  useEffect(() => {
    if (!open) return
    const previousFocus = document.activeElement instanceof HTMLElement
      ? document.activeElement : null
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const focusable = panel.current?.querySelector<HTMLElement>(
      'button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href]'
    )
    ;(focusable ?? panel.current)?.focus()
    const keydown = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.stopPropagation()
        event.preventDefault()
        onCloseRef.current()
      }
      if (event.key !== 'Tab' || !panel.current) return
      const focusItems = Array.from(panel.current.querySelectorAll<HTMLElement>(
        'button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),a[href]'
      )).filter(element => element.getClientRects().length > 0)
      if (focusItems.length === 0) { event.preventDefault(); panel.current.focus(); return }
      const first = focusItems[0], last = focusItems[focusItems.length - 1]
      if (event.shiftKey && (document.activeElement === first || !panel.current.contains(document.activeElement))) {
        event.preventDefault(); last.focus()
      } else if (!event.shiftKey && (document.activeElement === last || !panel.current.contains(document.activeElement))) {
        event.preventDefault(); first.focus()
      }
    }
    document.addEventListener('keydown', keydown, true)
    return () => {
      document.removeEventListener('keydown', keydown, true)
      document.body.style.overflow = previousOverflow
      previousFocus?.focus()
    }
  }, [open])
  if (!open) return null
  const panelClassName = className ? `modalPanel ${className}` : 'modalPanel'
  return <div className="modalBackdrop" role="presentation"
    onMouseDown={event => { if (event.currentTarget === event.target) onClose() }}>
    <section ref={panel} tabIndex={-1} className={panelClassName} role="dialog" aria-modal="true" aria-label={title}>
      <div className="modalHeader">
        <div><h2>{title}</h2>{description && <p>{description}</p>}</div>
        <button type="button" className="iconButton" aria-label={t('nav.close') + ' · ' + title} onClick={onClose}>×</button>
      </div>
      <div className="modalBody">{children}</div>
    </section>
  </div>
}
