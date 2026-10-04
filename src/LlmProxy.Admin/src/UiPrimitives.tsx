import type { ReactNode } from 'react'

export function Tabs<T extends string>({ value, onChange, items }: { value: T; onChange: (value: T) => void; items: Array<{ value: T; label: string; count?: number }> }) {
  return <div className="tabs" role="tablist">
    {items.map(item => <button key={item.value} type="button" role="tab" aria-selected={value === item.value} className={value === item.value ? 'tabButton active' : 'tabButton'} onClick={() => onChange(item.value)}>
      <span>{item.label}</span>{item.count !== undefined && <span className="tabCount">{item.count}</span>}
    </button>)}
  </div>
}

export function Modal({ open, title, description, onClose, children, className }: { open: boolean; title: string; description?: string; onClose: () => void; children: ReactNode; className?: string }) {
  if (!open) return null
  const panelClassName = className ? `modalPanel ${className}` : 'modalPanel'
  return <div className="modalBackdrop" role="presentation" onMouseDown={event => { if (event.currentTarget === event.target) onClose() }}>
    <section className={panelClassName} role="dialog" aria-modal="true" aria-label={title}>
      <div className="modalHeader">
        <div><h2>{title}</h2>{description && <p>{description}</p>}</div>
        <button type="button" className="iconButton" aria-label={`Close ${title}`} onClick={onClose}>×</button>
      </div>
      <div className="modalBody">{children}</div>
    </section>
  </div>
}
