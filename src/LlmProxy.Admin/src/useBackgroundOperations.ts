import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import {
  agentUpdateOperation, installJobOperation, mergeOperations, productUpdateOperations, sortOperations, startJobOperation
} from './backgroundOperations'
import type { BackgroundOperation, OperationSnapshot } from './backgroundOperations'
import type { Node, ProductUpdateOverview } from './types'

const OPERATIONS_KEY = 'llmproxy.activity.operations.v1'
const DISMISSED_KEY = 'llmproxy.activity.dismissed.v1'
const BUSY_POLL_MS = 4_000
const IDLE_POLL_MS = 15_000
const MAX_DISMISSED = 200

function readStored<T>(key: string, fallback: T): T {
  try {
    const raw = window.sessionStorage.getItem(key)
    return raw ? JSON.parse(raw) as T : fallback
  } catch { return fallback }
}

function store(key: string, value: unknown) {
  try { window.sessionStorage.setItem(key, JSON.stringify(value)) } catch { /* private mode or quota: the list just resets on reload */ }
}

const empty: OperationSnapshot = { active: [], terminal: [] }

/**
 * Follows long-running work from the shell, so it stays visible on every page. It polls the same endpoints the
 * pages use; a source that fails (for example an Agent that is offline) keeps its last known state instead of
 * pretending its operations ended.
 */
export function useBackgroundOperations({ nodes, productUpdates }: { nodes: Node[]; productUpdates: ProductUpdateOverview | null }) {
  const [operations, setOperations] = useState<Record<string, BackgroundOperation>>(() => readStored(OPERATIONS_KEY, {}))
  const tracked = useRef(operations)
  const dismissed = useRef(new Set<string>(readStored<string[]>(DISMISSED_KEY, [])))
  const sources = useRef<Record<string, OperationSnapshot>>({})
  const inFlight = useRef(false)
  const again = useRef(false)

  const recompute = useCallback(() => {
    const union: OperationSnapshot = { active: [], terminal: [] }
    for (const snapshot of Object.values(sources.current)) {
      union.active.push(...snapshot.active)
      union.terminal.push(...snapshot.terminal)
    }
    for (const operation of union.active) dismissed.current.delete(operation.id)
    const next = mergeOperations(tracked.current, union, dismissed.current, Date.now())
    tracked.current = next
    store(OPERATIONS_KEY, next)
    setOperations(next)
  }, [])

  useEffect(() => {
    sources.current.product = productUpdateOperations(productUpdates, Date.now())
    recompute()
  }, [productUpdates, recompute])

  const latestNodes = useRef(nodes)
  latestNodes.current = nodes
  const managed = nodes.filter(node => node.managementBaseAddress)
  const managedKey = managed.map(node => node.id + ':' + node.name).join('|')
  const busy = Object.values(operations).some(operation => operation.status === 'running' || operation.status === 'attention')

  useEffect(() => {
    async function poll() {
      if (inFlight.current) { again.current = true; return }
      inFlight.current = true
      try {
        const now = Date.now()
        // Read the node list at call time: a re-run queued while an older poll was in flight must see the nodes that arrived since.
        const nodes = latestNodes.current
        const managed = nodes.filter(node => node.managementBaseAddress)
        const names = new Map(nodes.map(node => [node.id, node.name]))
        const [pairs, installResults, startResults] = await Promise.all([
          Promise.allSettled([api.pairedAgentNodes()]).then(results => results[0]),
          Promise.allSettled(managed.map(node => api.installJobs(node.id))),
          Promise.allSettled(managed.map(node => api.modelStartJobs(node.id)))
        ])

        if (pairs.status === 'fulfilled') {
          const snapshot: OperationSnapshot = { active: [], terminal: [] }
          for (const pair of pairs.value) {
            const outcome = agentUpdateOperation(pair, names.get(pair.nodeId) ?? pair.nodeId.slice(0, 8), now)
            if (outcome.active) snapshot.active.push(outcome.active)
            if (outcome.terminal) snapshot.terminal.push(outcome.terminal)
          }
          sources.current.agents = snapshot
        }

        managed.forEach((node, index) => {
          const installs = installResults[index]
          const starts = startResults[index]
          if (installs.status !== 'fulfilled' || starts.status !== 'fulfilled') return
          const snapshot: OperationSnapshot = { active: [], terminal: [] }
          const labels = new Map(installs.value.flatMap(job => job.result?.installationId
            ? [[job.result.installationId, job.request.publicName ?? job.request.providerModelName] as const] : []))
          for (const job of installs.value) {
            const outcome = installJobOperation(job, node.id, node.name, now)
            if (outcome.active) snapshot.active.push(outcome.active)
            if (outcome.terminal) snapshot.terminal.push(outcome.terminal)
          }
          for (const job of starts.value) {
            const outcome = startJobOperation(job, node.id, node.name, labels.get(job.installationId) ?? job.installationId, now)
            if (outcome.active) snapshot.active.push(outcome.active)
            if (outcome.terminal) snapshot.terminal.push(outcome.terminal)
          }
          sources.current['jobs:' + node.id] = snapshot
        })
        recompute()
      } finally {
        inFlight.current = false
        if (again.current) { again.current = false; void poll() }
      }
    }

    void poll()
    const timer = window.setInterval(() => void poll(), busy ? BUSY_POLL_MS : IDLE_POLL_MS)
    return () => window.clearInterval(timer)
    // `managedKey` restarts the poll when the set of managed servers changes.
  }, [busy, managedKey, recompute])

  const dismiss = useCallback((id: string) => {
    dismissed.current.add(id)
    store(DISMISSED_KEY, [...dismissed.current].slice(-MAX_DISMISSED))
    recompute()
  }, [recompute])

  const clearFinished = useCallback(() => {
    for (const operation of Object.values(tracked.current)) {
      if (operation.status === 'done' || operation.status === 'failed') dismissed.current.add(operation.id)
    }
    store(DISMISSED_KEY, [...dismissed.current].slice(-MAX_DISMISSED))
    recompute()
  }, [recompute])

  return { operations: sortOperations(Object.values(operations)), dismiss, clearFinished }
}

export { empty as emptySnapshot }
