import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import { kindInfo, parseModelKind } from './modelKinds'
import { Modal } from './UiPrimitives'
import { useT } from './uiPreferences'
import type { EngineEntry, EngineJob, EnginesOverview, EngineUpdateCheck, Node } from './types'

const activeStates = new Set(['queued', 'running'])

function formatSize(bytes?: number | null) {
  if (!bytes) return '—'
  return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GiB` : `${Math.round(bytes / 1024 ** 2)} MiB`
}

function formatDate(value?: string | null) { return value ? new Date(value).toLocaleDateString() : '—' }

/** The dock links here with `?node=`, so the page opens on the server the operation belongs to. */
function initialNode(nodes: Node[]) {
  const wanted = new URLSearchParams(window.location.search).get('node')
  return nodes.find(node => node.id === wanted)?.id ?? nodes[0]?.id ?? ''
}

function rememberNode(id: string) {
  const url = new URL(window.location.href)
  url.searchParams.set('node', id)
  window.history.replaceState(window.history.state, '', url)
}

function browserZone() { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC' } catch { return 'UTC' } }

const resultText: Record<string, string> = {
  updated: 'Updated', up_to_date: 'Already up to date', skipped: 'Skipped', failed: 'Failed', waiting: 'Waiting to retry'
}

/**
 * Serving engines per server: which are installed and at which version, what uses them, install / update / uninstall,
 * and an optional automatic update at a chosen time. An engine is a Docker image; model files are never touched.
 */
export default function EnginesExperience({ nodes, canWrite }: { nodes: Node[]; canWrite: boolean }) {
  const t = useT()
  const [nodeId, setNodeId] = useState(() => initialNode(nodes))
  const [overview, setOverview] = useState<EnginesOverview | null>(null)
  const [checks, setChecks] = useState<Record<string, EngineUpdateCheck>>({})
  const [loading, setLoading] = useState(false)
  const [checking, setChecking] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [policyFor, setPolicyFor] = useState<EngineEntry | null>(null)
  const [enabled, setEnabled] = useState(false)
  const [time, setTime] = useState('03:00')
  const [zone, setZone] = useState('UTC')
  const [restartAfterPolicy, setRestartAfterPolicy] = useState(true)
  const [updateFor, setUpdateFor] = useState<EngineEntry | null>(null)
  const [restartAfterUpdate, setRestartAfterUpdate] = useState(true)
  const finished = useRef(new Set<string>())

  useEffect(() => { if (!nodeId && nodes[0]) setNodeId(nodes[0].id) }, [nodeId, nodes])

  const load = useCallback(async (quiet = false) => {
    if (!nodeId) return
    if (!quiet) setLoading(true)
    try { setOverview(await api.engines(nodeId)); if (!quiet) setError(null) }
    catch (failure) { if (!quiet) setError(failure instanceof Error ? failure.message : String(failure)) }
    finally { if (!quiet) setLoading(false) }
  }, [nodeId])

  useEffect(() => { setOverview(null); setChecks({}); setMessage(null); void load() }, [load])

  const jobs = overview?.jobs ?? []
  const running = useMemo(() => jobs.filter(job => activeStates.has(job.status)), [jobs])
  // Poll while something is downloading or building; stop when it ends.
  useEffect(() => {
    if (running.length === 0) return
    const timer = window.setInterval(() => void load(true), 3000)
    return () => window.clearInterval(timer)
  }, [running.length, load])
  // Say how a job ended once, and refresh the version it produced.
  useEffect(() => {
    for (const job of jobs) {
      if (activeStates.has(job.status) || finished.current.has(job.id)) continue
      finished.current.add(job.id)
      if (Date.now() - new Date(job.completedAtUtc ?? job.createdAtUtc).getTime() < 60_000)
        job.status === 'completed' ? setMessage(job.result?.message ?? job.detail) : setError(`${job.engineId}: ${job.error ?? job.detail}`)
    }
  }, [jobs])

  const jobOf = (engineId: string): EngineJob | undefined => jobs.find(job => job.engineId === engineId && activeStates.has(job.status))
  const guarded = async (key: string, work: () => Promise<void>) => {
    setBusy(key); setError(null); setMessage(null)
    try { await work() } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)) }
    finally { setBusy(null) }
  }

  const start = (entry: EngineEntry, action: 'install' | 'update', restartModels = false) => guarded(entry.engine.id, async () => {
    await api.startEngineJob(nodeId, entry.engine.id, action, restartModels)
    setMessage(action === 'install' ? `Installing ${entry.engine.label}…`
      : restartModels ? `Updating ${entry.engine.label}; the running models restart afterwards. Follow it in the activity panel.` : `Updating ${entry.engine.label}…`)
    await load(true)
  })

  // Models that are running keep the old version until they restart, so ask whether to restart them.
  const update = (entry: EngineEntry) => {
    if (entry.engine.runningContainers === 0) return start(entry, 'update')
    setRestartAfterUpdate(true)
    setUpdateFor(entry)
    return Promise.resolve()
  }

  const remove = (entry: EngineEntry) => {
    const used = entry.engine.installations
    const warning = used > 0
      ? `${used} installed model${used === 1 ? '' : 's'} use ${entry.engine.label}; ${used === 1 ? 'it' : 'they'} will set the engine up again the next time ${used === 1 ? 'it starts' : 'they start'}. Model files are kept.\n\nUninstall ${entry.engine.label}?`
      : `Uninstall ${entry.engine.label}? Downloaded model files are kept.`
    if (!window.confirm(warning)) return Promise.resolve()
    return guarded(entry.engine.id, async () => {
      const result = await api.removeEngine(nodeId, entry.engine.id)
      setMessage(result.message)
      setChecks(current => { const next = { ...current }; delete next[entry.engine.id]; return next })
      await load(true)
    })
  }

  const check = () => {
    setChecking(true); setError(null)
    return api.checkEngineUpdates(nodeId)
      .then(list => setChecks(Object.fromEntries(list.map(item => [item.id, item]))))
      .catch(failure => setError(failure instanceof Error ? failure.message : String(failure)))
      .finally(() => setChecking(false))
  }

  const cancel = (job: EngineJob) => guarded(job.engineId, async () => { await api.cancelEngineJob(nodeId, job.id); await load(true) })

  function openPolicy(entry: EngineEntry) {
    setPolicyFor(entry)
    setEnabled(entry.policy.autoUpdate)
    setTime(entry.policy.updateAtLocal)
    setZone(entry.policy.autoUpdate ? entry.policy.timeZone : browserZone())
    setRestartAfterPolicy(entry.policy.restartModels ?? true)
  }

  async function savePolicy(event: FormEvent) {
    event.preventDefault()
    if (!policyFor) return
    await guarded(policyFor.engine.id, async () => {
      await api.saveEnginePolicy(nodeId, policyFor.engine.id, { autoUpdate: enabled, updateAtLocal: time, timeZone: zone.trim(), restartModels: restartAfterPolicy })
      setMessage(enabled ? `${policyFor.engine.label} will update every day at ${time} (${zone}).` : `Automatic updates for ${policyFor.engine.label} are off.`)
      setPolicyFor(null)
      await load(true)
    })
  }

  const entries = overview?.engines ?? []
  const installed = entries.filter(entry => entry.engine.installed).length

  if (nodes.length === 0) return <section className="panel"><div className="emptyState"><strong>No servers yet</strong><p>Add a server under Fleet & access to manage its engines.</p></div></section>

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div>
        <h2>Engines</h2>
        <p className="muted">The programs that run your models on each server (llama.cpp, vLLM and the others): installed or not, which version, and when they update. Updating an engine never touches downloaded model files. A running model keeps the version it started with until it restarts: you can have the running models restarted after an update, one at a time.</p>
      </div>
      <div className="actions">
        <label>Server<select aria-label="Engines server" value={nodeId} onChange={event => { setNodeId(event.target.value); rememberNode(event.target.value) }}>
          {nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}</select></label>
        <button className="secondary" disabled={loading} onClick={() => void load()}>Refresh</button>
        <button className="secondary" disabled={!overview?.agentAvailable || checking || installed === 0} onClick={() => void check()}>
          {checking ? 'Checking…' : 'Check for updates'}</button>
      </div>
    </section>

    {error && <div className="error" role="alert">{error}</div>}
    {message && <div className="notice" role="status">{message}</div>}
    {overview && !overview.agentConfigured && <div className="notice">This server has no management Agent, so its engines cannot be managed from here. Pair the Agent under Fleet & access.</div>}
    {overview?.agentConfigured && !overview.agentAvailable && <div className="error" role="alert">{overview.agentError ?? 'The Agent did not answer.'}</div>}

    {overview?.agentAvailable && <section className="panel">
      <div className="panelTitle"><h2>Engines on this server</h2><span>{installed} of {entries.length} installed</span></div>
      <div className="tableScroll"><table>
        <thead><tr><th>Engine</th><th>Status</th><th>Version</th><th>Used by</th><th>Updates</th><th>Automatic update</th>{canWrite && <th>Actions</th>}</tr></thead>
        <tbody>{entries.map(entry => {
          const { engine, policy } = entry
          const job = jobOf(engine.id)
          const verdict = checks[engine.id]
          const working = busy === engine.id
          return <tr key={engine.id} aria-label={engine.label}>
            <td><strong>{engine.label}</strong><div className="muted">{engine.description}</div>
              <div className="chipList">{engine.serves.map(kind => <span className="chip" key={kind}>{t(kindInfo(parseModelKind(kind)).label)}</span>)}</div></td>
            <td><span className={'pill ' + (job ? 'pill-info' : engine.installed ? 'pill-success' : 'pill-neutral')}><i aria-hidden="true" />
              {job ? (job.action === 'install' ? 'Installing' : 'Updating') : engine.installed ? 'Installed' : 'Not installed'}</span>
              <div className="muted">{engine.source === 'built' ? 'Built by the Agent' : 'Downloaded image'}</div></td>
            <td>{job ? <span className="muted" title={job.detail}>{job.detail}</span> : engine.installed
              ? <><strong>{engine.version ?? '—'}</strong>
                <div className="muted mono">{engine.imageId ? engine.imageId.replace('sha256:', '').slice(0, 12) : ''}{engine.revision ? ` · ${engine.revision}` : ''}</div>
                <div className="muted">{formatDate(engine.createdAtUtc)} · {formatSize(engine.sizeBytes)}</div></>
              : <span className="muted">—</span>}</td>
            <td>{engine.installed ? <>
              <div>{engine.runningContainers} running</div>
              <div className="muted">{engine.installations} installed model{engine.installations === 1 ? '' : 's'}</div></> : <span className="muted">—</span>}</td>
            <td>{!engine.installed ? <span className="muted">—</span> : !verdict ? <span className="muted">Not checked</span>
              : <span className={'pill ' + (verdict.status === 'update_available' ? 'pill-warning' : verdict.status === 'up_to_date' ? 'pill-success' : 'pill-neutral')} title={verdict.detail ?? undefined}>
                <i aria-hidden="true" />{verdict.status === 'update_available' ? 'Update available' : verdict.status === 'up_to_date' ? 'Up to date'
                  : verdict.status === 'not_applicable' ? 'Rebuild to refresh' : 'Cannot tell'}</span>}</td>
            <td>{policy.autoUpdate ? <><strong>Daily at {policy.updateAtLocal}</strong><div className="muted">{policy.timeZone}{policy.restartModels === false ? ' · models are not restarted' : ' · restarts running models'}</div></> : <span className="muted">Off</span>}
              {policy.lastResult && <div className="muted" title={policy.lastMessage ?? undefined}>Last: {resultText[policy.lastResult] ?? policy.lastResult}{policy.lastRunAtUtc ? ` · ${new Date(policy.lastRunAtUtc).toLocaleString()}` : ''}</div>}</td>
            {canWrite && <td><div className="actions">
              {job ? <button className="secondary" disabled={working} onClick={() => void cancel(job)}>Cancel</button> : <>
                {!engine.installed && <button className="primary" disabled={working || busy !== null} onClick={() => void start(entry, 'install')}>Install</button>}
                {engine.installed && <button disabled={working || busy !== null || running.length > 0} onClick={() => void update(entry)}>Update</button>}
                {engine.installed && <button className="secondary" disabled={working || busy !== null || engine.runningContainers > 0}
                  title={engine.runningContainers > 0 ? 'Stop the models that use it first' : undefined} onClick={() => void remove(entry)}>Uninstall</button>}
                <button className="secondary" disabled={busy !== null} onClick={() => openPolicy(entry)}>Auto-update…</button>
              </>}
            </div></td>}
          </tr>
        })}</tbody>
      </table></div>
    </section>}

    <Modal open={updateFor !== null} title={`Update ${updateFor?.engine.label ?? ''}`}
      description="The new image is downloaded first. Running models keep using the old one until they restart." onClose={() => setUpdateFor(null)}>
      <form className="formPanel" onSubmit={event => {
        event.preventDefault()
        const entry = updateFor
        setUpdateFor(null)
        if (entry) void start(entry, 'update', restartAfterUpdate)
      }}>
        <p>{updateFor?.engine.runningContainers} running model{updateFor?.engine.runningContainers === 1 ? '' : 's'} use{updateFor?.engine.runningContainers === 1 ? 's' : ''} {updateFor?.engine.label}.</p>
        <label className="inlineCheck"><input type="checkbox" checked={restartAfterUpdate} onChange={event => setRestartAfterUpdate(event.target.checked)} /> Restart them afterwards so they use the new version</label>
        <p className="muted">Each model is taken out of routing, given time to finish its requests, stopped and started again, one at a time. It is unavailable while it reloads, which can take several minutes. If one does not come back, its routing stays off and the others are left running. Without the box, nothing restarts.</p>
        <div className="actions"><button className="primary" disabled={busy !== null}>Update</button>
          <button type="button" className="secondary" onClick={() => setUpdateFor(null)}>Cancel</button></div>
      </form>
    </Modal>

    <Modal open={policyFor !== null} title={`Automatic update · ${policyFor?.engine.label ?? ''}`}
      description="Every day at the chosen time the server checks this engine and updates it if a newer version exists." onClose={() => setPolicyFor(null)}>
      <form className="formPanel" onSubmit={event => void savePolicy(event)}>
        <label className="inlineCheck"><input type="checkbox" checked={enabled} onChange={event => setEnabled(event.target.checked)} /> Update automatically</label>
        <label>Time<input aria-label="Update time" type="time" value={time} disabled={!enabled} onChange={event => setTime(event.target.value)} required /></label>
        <label>Time zone<input aria-label="Time zone" value={zone} disabled={!enabled} placeholder="Europe/Rome" onChange={event => setZone(event.target.value)} required /></label>
        <label className="inlineCheck"><input type="checkbox" checked={restartAfterPolicy} disabled={!enabled} onChange={event => setRestartAfterPolicy(event.target.checked)} /> Restart the running models after an update, so they use the new version</label>
        <p className="muted">Only installed engines are updated; one that is not installed is skipped. With the box above ticked, running models are restarted one at a time (taken out of routing first, requests in flight allowed to finish) and are unavailable while they reload, which can take several minutes; without it they keep the version they started with until they next start. If the server is unreachable at that time, it is retried for the next 12 hours.</p>
        <div className="actions"><button className="primary" disabled={busy !== null}>Save</button>
          <button type="button" className="secondary" onClick={() => setPolicyFor(null)}>Cancel</button></div>
      </form>
    </Modal>
  </div>
}
