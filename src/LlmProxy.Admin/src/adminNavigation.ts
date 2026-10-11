import { useEffect, useState } from 'react'

export type AdminView =
  | 'dashboard' | 'infrastructure' | 'nodes' | 'hardware' | 'model-management'
  | 'models' | 'deployments' | 'routing' | 'credentials' | 'users'
  | 'metrics' | 'playground' | 'logs' | 'audit' | 'help' | 'governance' | 'releases'

const knownViews: readonly AdminView[] = [
  'dashboard', 'infrastructure', 'nodes', 'hardware', 'model-management',
  'models', 'deployments', 'routing', 'credentials', 'users',
  'metrics', 'playground', 'logs', 'audit', 'help', 'governance', 'releases'
]

export function viewFromAdminPath(pathname: string): AdminView {
  const path = pathname.replace(/\/+$/, '')
  const match = /^\/admin(?:\/([^/]+))?$/i.exec(path)
  if (!match) return 'dashboard'
  const segment = match[1] || 'dashboard'
  if (segment === 'model-management' || segment === 'hardware' || segment === 'nodes') return 'infrastructure'
  if (segment === 'deployments') return 'models'
  return knownViews.includes(segment as AdminView) ? segment as AdminView : 'dashboard'
}

export function urlForAdminView(view: AdminView): string {
  const normalized = view === 'nodes' || view === 'hardware' || view === 'model-management'
    ? 'infrastructure'
    : view === 'deployments' ? 'models' : view
  return '/admin/' + normalized
}

// A query parameter records the tab of the currently displayed workspace.
// Native history and popstate restore it, including after a hard refresh.
export function useRouteTab<T extends string>(key: string, permitted: readonly T[], fallback: T):
  [T, (value: T) => void] {
  const read = (): T => {
    const value = new URLSearchParams(window.location.search).get(key)
    return permitted.includes(value as T) ? value as T : fallback
  }
  const [tab, setTab] = useState<T>(read)
  useEffect(() => {
    const onPopState = () => setTab(read())
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [key, fallback, permitted.join('|')])
  const navigate = (value: T) => {
    if (value === tab) return
    const url = new URL(window.location.href)
    url.searchParams.set(key, value)
    // Avoid retaining irrelevant tabs when leaving the infrastructure section.
    if (key === 'section' && value !== 'inventory') url.searchParams.delete('tab')
    window.history.pushState(null, '', url.pathname + url.search + url.hash)
    setTab(value)
  }
  return [tab, navigate]
}
