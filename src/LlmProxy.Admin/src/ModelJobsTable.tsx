import { Fragment, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useT } from './uiPreferences'

export type JobStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled' | 'interrupted'
export type JobAction = { key: string; label: string; primary?: boolean; disabled?: boolean; onClick: () => void }

/** One line of the Jobs table: a download, a model start, or an administrative operation. */
export type JobRow = {
  id: string
  type: 'download' | 'start' | 'operation'
  title: string
  subtitle?: string
  status: JobStatus
  detail: string
  percent?: number | null
  size?: string | null
  startedAtUtc: string
  actions: JobAction[]
  /** Shown under the row when the operator opens it (diagnosis, hints). */
  expanded?: ReactNode
}

type Filter = 'attention' | 'active' | 'done' | 'all'

const tone: Record<JobStatus, string> = {
  queued: 'info', running: 'info', completed: 'success', failed: 'danger', cancelled: 'neutral', interrupted: 'warning'
}

export const failed = (row: JobRow) => row.status === 'failed' || row.status === 'interrupted'
/** Finished downloads that still wait for the operator (register, start, activate) stay in the default view. */
export const needsAttention = (row: JobRow) => failed(row) || (row.status === 'completed' && row.actions.some(action => action.primary))
export const isActive = (row: JobRow) => row.status === 'queued' || row.status === 'running'

export function filterJobs(rows: JobRow[], filter: Filter): JobRow[] {
  switch (filter) {
    case 'active': return rows.filter(isActive)
    case 'attention': return rows.filter(row => isActive(row) || needsAttention(row))
    case 'done': return rows.filter(row => (row.status === 'completed' || row.status === 'cancelled') && !needsAttention(row))
    default: return rows
  }
}

export function paginate<T>(rows: T[], page: number, size: number): { items: T[]; pages: number; page: number; from: number; to: number } {
  const pages = Math.max(1, Math.ceil(rows.length / size))
  const current = Math.min(Math.max(1, page), pages)
  const start = (current - 1) * size
  return { items: rows.slice(start, start + size), pages, page: current, from: rows.length === 0 ? 0 : start + 1, to: Math.min(rows.length, start + size) }
}

const PAGE_SIZES = [10, 25, 50]

/**
 * Compact, paginated table of everything running or finished in the Models workspace. Newest first; by default it
 * shows what is running or needs attention so finished history does not bury it.
 */
export default function ModelJobsTable({ rows }: { rows: JobRow[] }) {
  const t = useT()
  const [filter, setFilter] = useState<Filter>('attention')
  const [size, setSize] = useState(10)
  const [page, setPage] = useState(1)
  const [toggled, setToggled] = useState<Set<string>>(new Set())

  const sorted = useMemo(() => [...rows].sort((a, b) => b.startedAtUtc.localeCompare(a.startedAtUtc)), [rows])
  const visible = useMemo(() => filterJobs(sorted, filter), [sorted, filter])
  const view = paginate(visible, page, size)
  useEffect(() => { if (page !== view.page) setPage(view.page) }, [page, view.page])

  const counts = {
    attention: filterJobs(sorted, 'attention').length, active: filterJobs(sorted, 'active').length,
    done: filterJobs(sorted, 'done').length, all: sorted.length
  }
  const filters: Array<[Filter, string]> = [
    ['attention', t('jobs.filterAttention')], ['active', t('jobs.filterActive')], ['done', t('jobs.filterDone')], ['all', t('jobs.filterAll')]
  ]
  const toggle = (id: string) => setToggled(current => { const next = new Set(current); if (next.has(id)) next.delete(id); else next.add(id); return next })
  // A failure explains itself without a click; the operator can fold it away. Everything else opens on request.
  const isOpen = (row: JobRow) => failed(row) !== toggled.has(row.id)
  const typeLabel = { download: t('jobs.typeDownload'), start: t('jobs.typeStart'), operation: t('jobs.typeOperation') }

  return <div className="jobsTable">
    <div className="jobsToolbar">
      <div className="jobsFilters" role="group" aria-label={t('jobs.filter')}>
        {filters.map(([value, label]) => <button key={value} type="button" className={filter === value ? 'primary compact' : 'secondary compact'}
          aria-pressed={filter === value} onClick={() => { setFilter(value); setPage(1) }}>{label} <span className="tabCount">{counts[value]}</span></button>)}
      </div>
      <label className="jobsPageSize">{t('jobs.perPage')}
        <select aria-label={t('jobs.perPage')} value={size} onChange={event => { setSize(Number(event.target.value)); setPage(1) }}>
          {PAGE_SIZES.map(option => <option key={option} value={option}>{option}</option>)}
        </select>
      </label>
    </div>

    {visible.length === 0 ? <p className="muted">{t('jobs.empty')}</p> : <div className="tableScroll">
      <table className="jobsGrid">
        <thead><tr>
          <th>{t('jobs.type')}</th><th>{t('jobs.model')}</th><th>{t('jobs.status')}</th>
          <th>{t('jobs.progress')}</th><th>{t('jobs.started')}</th><th>{t('jobs.actions')}</th>
        </tr></thead>
        <tbody>{view.items.map(row => <Fragment key={row.id}>
          <tr className={'jobRow jobRow--' + row.status}>
            <td data-label={t('jobs.type')}><span className="jobType">{typeLabel[row.type]}</span></td>
            <td data-label={t('jobs.model')}><strong>{row.title}</strong>{row.subtitle && <div className="muted mono">{row.subtitle}</div>}</td>
            <td data-label={t('jobs.status')}><span className={'pill pill-' + tone[row.status]}><i aria-hidden="true" />{t(('jobs.status.' + row.status) as 'jobs.status.running')}</span></td>
            <td data-label={t('jobs.progress')} className="jobProgress">
              {row.percent != null && isActive(row) && <progress max={100} value={Math.min(100, Math.max(0, row.percent))} aria-label={t('jobs.progress') + ' ' + row.title} />}
              <span className="jobDetail">{row.percent != null && isActive(row) ? row.percent.toFixed(0) + '% · ' : ''}{row.detail}</span>
              {row.size && <span className="muted"> · {row.size}</span>}
            </td>
            <td data-label={t('jobs.started')} className="jobStarted">{new Date(row.startedAtUtc).toLocaleString()}</td>
            <td data-label={t('jobs.actions')}><div className="actions jobActions">
              {row.actions.map(action => <button key={action.key} type="button" className={(action.primary ? 'primary' : 'secondary') + ' compact'}
                disabled={action.disabled} onClick={action.onClick}>{action.label}</button>)}
              {row.expanded && <button type="button" className="secondary compact" aria-expanded={isOpen(row)}
                onClick={() => toggle(row.id)}>{isOpen(row) ? t('jobs.hideDetails') : t('jobs.details')}</button>}
            </div></td>
          </tr>
          {row.expanded && isOpen(row) && <tr className="jobDetailsRow"><td colSpan={6}>{row.expanded}</td></tr>}
        </Fragment>)}</tbody>
      </table>
    </div>}

    {visible.length > 0 && <div className="jobsPager">
      <span className="muted">{t('jobs.range').replace('{from}', String(view.from)).replace('{to}', String(view.to)).replace('{total}', String(visible.length))}</span>
      <div className="actions">
        <button type="button" className="secondary compact" disabled={view.page <= 1} onClick={() => setPage(view.page - 1)}>{t('jobs.prev')}</button>
        <span>{view.page} / {view.pages}</span>
        <button type="button" className="secondary compact" disabled={view.page >= view.pages} onClick={() => setPage(view.page + 1)}>{t('jobs.next')}</button>
      </div>
    </div>}
  </div>
}
