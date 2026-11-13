import type { ModelInstallJob, ModelStartJob, PairedNodeStatus, ProductUpdateOverview } from './types'

/**
 * Long-running work (Agent update, model download, model start, gateway update) is started on one page and
 * finishes minutes later. These pure functions turn the raw state the API already exposes into one list the shell
 * can show on every page, and keep a finished item around until the operator has seen it.
 */
export type OperationStatus = 'running' | 'attention' | 'failed' | 'done'
export type OperationKind = 'agent-update' | 'model-install' | 'model-start' | 'product-update'

export type BackgroundOperation = {
  id: string
  kind: OperationKind
  title: string
  detail: string
  status: OperationStatus
  /** 0..100 when the source reports a real percentage; never invented. */
  progress?: number | null
  /** Admin URL where the operator can act on it. */
  href: string
  firstSeenAtMs: number
  finishedAtMs?: number | null
}

/** Result of one polling cycle: what is in flight now, and the final state the sources still report. */
export type OperationSnapshot = {
  active: BackgroundOperation[]
  terminal: BackgroundOperation[]
}

export type AgentUpdateStatus = {
  state: 'running' | 'succeeded' | 'failed'
  target: string
  phase?: string
  detail?: string
}

/** The Agent reports its updater as `state:target:phase:detail`. */
export function parseAgentUpdateStatus(raw?: string | null): AgentUpdateStatus | null {
  if (!raw) return null
  const [state, target, phase, detail] = raw.split(':')
  if (state !== 'running' && state !== 'succeeded' && state !== 'failed') return null
  return { state, target: target ?? '', phase: phase || undefined, detail: detail || undefined }
}

const stageText: Record<string, string> = {
  draining: 'Draining routing; waiting for running requests to finish',
  updating: 'Installing the new Agent',
  verifying: 'Verifying the Agent and the models before restoring routing',
  releasing: 'Restoring routing',
  needs_attention: 'Needs attention: the Agent could not be verified. Routing stays off until you act'
}

export function agentUpdateOperation(pair: PairedNodeStatus, nodeName: string, nowMs: number): { active?: BackgroundOperation; terminal?: BackgroundOperation } {
  const status = parseAgentUpdateStatus(pair.agentUpdateStatus)
  const id = `agent-update:${pair.nodeId}`
  const href = '/admin/infrastructure'
  const target = pair.agentUpgradeTargetVersion ?? status?.target ?? ''
  const base = { id, kind: 'agent-update' as const, title: `Agent update · ${nodeName}${target ? ' → v' + target : ''}`, href, firstSeenAtMs: nowMs }

  if (pair.agentUpgradeTargetVersion) {
    const stage = pair.agentUpgradeMaintenanceStage ?? 'preparing'
    const download = status?.state === 'running' && status.phase === 'downloading' && /^\d{1,3}$/.test(status.detail ?? '')
      ? Math.min(100, Number(status.detail)) : null
    return {
      active: {
        ...base,
        status: stage === 'needs_attention' ? 'attention' : 'running',
        detail: download !== null ? `Downloading the Agent release (${download}%)` : stageText[stage] ?? `Preparing (${stage})`,
        progress: download
      }
    }
  }

  if (status?.state === 'succeeded') return { terminal: { ...base, status: 'done', detail: `Agent is on v${status.target}; routing restored` } }
  if (status?.state === 'failed') {
    return { terminal: { ...base, status: 'failed', detail: `Update failed${status.detail ? ': ' + status.detail.replaceAll('_', ' ') : ''}. The previous version is still running` } }
  }
  if (status?.state === 'running') {
    return { active: { ...base, status: 'running', detail: `Updating${status.phase ? ' · ' + status.phase : ''}`, progress: null } }
  }
  return {}
}

export function installJobOperation(job: ModelInstallJob, nodeId: string, nodeName: string, nowMs: number): { active?: BackgroundOperation; terminal?: BackgroundOperation } {
  const name = job.request.publicName ?? job.request.providerModelName
  const base = {
    id: `model-install:${nodeId}:${job.id}`, kind: 'model-install' as const,
    title: `Download · ${name}`, href: `/admin/models?node=${encodeURIComponent(nodeId)}&panel=activity`, firstSeenAtMs: nowMs
  }
  if (job.status === 'queued' || job.status === 'running') {
    const percent = typeof job.percent === 'number' ? Math.max(0, Math.min(100, job.percent)) : null
    return { active: { ...base, status: 'running', detail: `${nodeName} · ${job.detail || job.stage}`, progress: percent } }
  }
  if (job.status === 'completed') return { terminal: { ...base, status: 'done', detail: `${nodeName} · downloaded. Start it from the Installed tab` } }
  if (job.status === 'failed' || job.status === 'interrupted') {
    return { terminal: { ...base, status: 'failed', detail: `${nodeName} · ${job.error ?? job.detail ?? 'The download did not finish'}` } }
  }
  return {}
}

export function startJobOperation(job: ModelStartJob, nodeId: string, nodeName: string, label: string, nowMs: number): { active?: BackgroundOperation; terminal?: BackgroundOperation } {
  const base = {
    id: `model-start:${nodeId}:${job.id}`, kind: 'model-start' as const,
    title: `Start · ${label}`, href: `/admin/models?node=${encodeURIComponent(nodeId)}`, firstSeenAtMs: nowMs
  }
  if (job.status === 'queued' || job.status === 'starting') {
    return { active: { ...base, status: 'running', detail: `${nodeName} · ${job.detail || 'Starting the runtime'}`, progress: null } }
  }
  if (job.status === 'completed') return { terminal: { ...base, status: 'done', detail: `${nodeName} · running. Verify it and activate routing` } }
  if (job.status === 'failed' || job.status === 'interrupted') {
    return { terminal: { ...base, status: 'failed', detail: `${nodeName} · ${job.error?.split('\n')[0] ?? 'The start did not complete'}` } }
  }
  return {}
}

export function productUpdateOperations(overview: ProductUpdateOverview | null, nowMs: number): OperationSnapshot {
  const snapshot: OperationSnapshot = { active: [], terminal: [] }
  const job = overview?.agent?.activeJob
  const base = (version: string, id: string) => ({
    id: `product-update:${id}`, kind: 'product-update' as const, title: `Gateway update → v${version}`, href: '/admin/releases', firstSeenAtMs: nowMs
  })
  if (job) {
    snapshot.active.push({
      ...base(job.version, job.id), status: 'running', progress: null,
      detail: (job.status === 'Pending' ? 'Scheduled' : 'Applying') + (job.currentStep ? ` v${job.currentStep}` : '') + '. The gateway restarts briefly; this page reconnects by itself'
    })
  }
  for (const recent of overview?.agent?.recentJobs ?? []) {
    if (recent.id === job?.id) continue
    if (recent.status === 'Succeeded') snapshot.terminal.push({ ...base(recent.version, recent.id), status: 'done', detail: 'Gateway updated' })
    else if (recent.status === 'Failed') snapshot.terminal.push({ ...base(recent.version, recent.id), status: 'failed', detail: recent.error ?? 'The update failed' })
  }
  return snapshot
}

/**
 * Folds one polling cycle into what the operator sees. An operation appears only once it was seen in flight (old
 * finished jobs that the sources still list are ignored), keeps its first-seen time, and when it leaves the active
 * set it becomes `done` or `failed` and stays until dismissed.
 */
export function mergeOperations(
  tracked: Record<string, BackgroundOperation>, snapshot: OperationSnapshot, dismissed: ReadonlySet<string>, nowMs: number
): Record<string, BackgroundOperation> {
  const next: Record<string, BackgroundOperation> = {}
  const activeIds = new Set(snapshot.active.map(operation => operation.id))
  const terminalById = new Map(snapshot.terminal.map(operation => [operation.id, operation]))

  // An operation that is in flight is always shown, even if an earlier run with the same id was dismissed
  // (a second Agent update on the same server reuses its id).
  for (const operation of snapshot.active) {
    next[operation.id] = { ...operation, firstSeenAtMs: tracked[operation.id]?.firstSeenAtMs ?? operation.firstSeenAtMs, finishedAtMs: null }
  }

  for (const previous of Object.values(tracked)) {
    if (activeIds.has(previous.id) || dismissed.has(previous.id)) continue
    if (previous.status === 'done' || previous.status === 'failed') { next[previous.id] = previous; continue }
    const final = terminalById.get(previous.id)
    // Gone from the active set without a reported outcome: it ended, and its page tells how.
    next[previous.id] = final
      ? { ...final, firstSeenAtMs: previous.firstSeenAtMs, finishedAtMs: nowMs }
      : { ...previous, status: 'done', progress: previous.progress != null ? 100 : null, detail: 'Finished', finishedAtMs: nowMs }
  }

  return next
}

export function sortOperations(operations: BackgroundOperation[]): BackgroundOperation[] {
  const rank: Record<OperationStatus, number> = { attention: 0, failed: 1, running: 2, done: 3 }
  return [...operations].sort((a, b) => rank[a.status] - rank[b.status] || b.firstSeenAtMs - a.firstSeenAtMs)
}

export function summarizeOperations(operations: BackgroundOperation[]) {
  const count = (status: OperationStatus) => operations.filter(operation => operation.status === status).length
  const running = count('running'); const attention = count('attention'); const failed = count('failed'); const done = count('done')
  const tone: 'attention' | 'failed' | 'running' | 'done' = attention ? 'attention' : failed ? 'failed' : running ? 'running' : 'done'
  return { running, attention, failed, done, tone, total: operations.length }
}
