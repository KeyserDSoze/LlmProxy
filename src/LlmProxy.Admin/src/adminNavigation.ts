import { useEffect, useState } from 'react'

export type AdminView =
  | 'dashboard' | 'infrastructure'
  | 'models' | 'catalog' | 'system-one' | 'routing' | 'credentials' | 'users'
  | 'metrics' | 'playground' | 'logs' | 'audit' | 'help' | 'governance' | 'releases'

const knownViews: readonly AdminView[] = [
  'dashboard', 'infrastructure',
  'models', 'catalog', 'system-one', 'routing', 'credentials', 'users',
  'metrics', 'playground', 'logs', 'audit', 'help', 'governance', 'releases'
]

export function viewFromAdminPath(pathname: string): AdminView {
  const path = pathname.replace(/\/+$/, '')
  const match = /^\/admin(?:\/([^/]+))?$/i.exec(path)
  if (!match) return 'dashboard'
  const segment = match[1] || 'dashboard'
  return knownViews.includes(segment as AdminView) ? segment as AdminView : 'dashboard'
}

export function urlForAdminView(view: AdminView): string {
  return '/admin/' + view
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


/**
 * Preserve the physical hardware identity in the same URL as the current
 * infrastructure section and tab. A missing or unknown node must never
 * silently select a DIFFERENT machine when the user refreshes a deep link.
 */
export function useRouteHardware(nodes: readonly { id: string }[]): [string, (id: string) => void] {
  const read = () => new URLSearchParams(window.location.search).get('node') ?? ''
  const [requestedId, setRequestedId] = useState<string>(read)
  const fallbackId = nodes[0]?.id ?? ''

  useEffect(() => {
    const restore = () => setRequestedId(read())
    window.addEventListener('popstate', restore)
    return () => window.removeEventListener('popstate', restore)
  }, [])

  // Add the default selection to the address bar once the fleet has loaded.
  // replaceState does not add a spurious browser-history entry.
  useEffect(() => {
    if (requestedId || !fallbackId) return
    const url = new URL(window.location.href)
    if (url.searchParams.has('node')) return
    url.searchParams.set('node', fallbackId)
    window.history.replaceState(null, '', url.pathname + url.search + url.hash)
    setRequestedId(fallbackId)
  }, [requestedId, fallbackId])

  const selectedId = requestedId
    ? (nodes.some(node => node.id === requestedId) ? requestedId : '')
    : fallbackId

  const selectHardware = (id: string) => {
    if (id === selectedId) return
    const url = new URL(window.location.href)
    if (id) url.searchParams.set('node', id)
    else url.searchParams.delete('node')
    window.history.pushState(null, '', url.pathname + url.search + url.hash)
    setRequestedId(id)
  }

  return [selectedId, selectHardware]
}
