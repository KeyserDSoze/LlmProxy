import { Fragment, useEffect, useRef, useState } from 'react'
import type { KeyboardEvent, MouseEvent, ReactNode } from 'react'
import { navigateAdmin } from './adminNavigation'
import { useT } from './uiPreferences'

/** In-app link: real href (open in new tab works) with client-side navigation on plain click. */
export function AdminLink({ href, className, children }: { href: string; className?: string; children: ReactNode }) {
  const onClick = (event: MouseEvent<HTMLAnchorElement>) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    navigateAdmin(href)
  }
  return <a href={href} className={className} onClick={onClick}>{children}</a>
}

/** Text with soft break opportunities after every "/" so URLs wrap between segments, not inside a word. */
export function BreakableUrl({ value }: { value: string }) {
  const parts = value.split('/')
  return <>{parts.map((part, index) => <Fragment key={index}>{part}{index < parts.length - 1 && <>/<wbr /></>}</Fragment>)}</>
}

/**
 * Copies text and reports whether it worked. The async Clipboard API exists only in secure contexts (HTTPS or
 * localhost) and is missing on a gateway opened over plain HTTP on the LAN, so fall back to a selection copy.
 */
export async function copyText(value: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value)
      return true
    }
  } catch { /* fall through to the selection copy */ }
  try {
    const area = document.createElement('textarea')
    area.value = value
    area.setAttribute('readonly', '')
    area.style.cssText = 'position:fixed;top:0;left:0;opacity:0;pointer-events:none'
    document.body.appendChild(area)
    area.select()
    const copied = document.execCommand('copy')
    area.remove()
    return copied
  } catch {
    return false
  }
}

/** Copy button that says whether the browser allowed it, instead of failing silently. */
export function CopyButton({ value, children, className, title }: { value: string; children: ReactNode; className?: string; title?: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])
  async function copy() {
    setState(await copyText(value) ? 'copied' : 'failed')
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setState('idle'), 3000)
  }
  return <>
    <button type="button" className={className} title={title} onClick={() => void copy()}>{children}</button>
    <span className={'copyStatus' + (state === 'failed' ? ' copyStatusFailed' : '')} role="status" aria-live="polite">
      {state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy is blocked here. Select the text and copy it manually.' : ''}
    </span>
  </>
}

/** Decorative "info" glyph as SVG, so it never becomes part of a button's visible text. */
export function InfoIcon() {
  return <svg className="infoIcon" viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
    <circle cx="8" cy="8" r="6.5" fill="none" stroke="currentColor" strokeWidth="1.4" />
    <path d="M8 7.2v4" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
    <circle cx="8" cy="4.9" r="0.95" fill="currentColor" />
  </svg>
}

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
        <button type="button" className="iconButton" aria-label={t('modal.close') + ' ' + title} onClick={onClose}>×</button>
      </div>
      <div className="modalBody">{children}</div>
    </section>
  </div>
}
