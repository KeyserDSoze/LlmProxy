import { useEffect, useMemo, useState } from 'react'
import { api } from './api'
import PageDocumentation from './PageDocumentation'
import type { ProductUpdateOverview } from './types'

type ProductRelease = {
  version: string
  releasedOn: string
  title: string
  sections: Record<string, string[]>
}

type ProductReleaseInfo = {
  product: string
  version: string
  channel: string
  releasedOn: string
  buildRevision?: string | null
  builtAtUtc?: string | null
  releases: ProductRelease[]
}

export default function ReleaseNotesPage({ embedded = false, canWrite = false }: { embedded?: boolean; canWrite?: boolean }) {
  const [product, setProduct] = useState<ProductReleaseInfo | null>(null)
  const [updates, setUpdates] = useState<ProductUpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [updateError, setUpdateError] = useState<string | null>(null)
  const [busyVersion, setBusyVersion] = useState<string | null>(null)
  const [scheduleTimes, setScheduleTimes] = useState<Record<string, string>>({})

  async function refreshUpdates() {
    try {
      setUpdates(await api.productUpdates())
      setUpdateError(null)
    } catch (reason) {
      setUpdateError(reason instanceof Error ? reason.message : String(reason))
    }
  }

  useEffect(() => {
    fetch('/api/admin/product', { credentials: 'same-origin' })
      .then(async response => {
        if (response.status === 401) throw new Error('AUTH_REQUIRED')
        if (response.status === 403) throw new Error('FORBIDDEN')
        if (!response.ok) throw new Error(await response.text() || `${response.status} ${response.statusText}`)
        return response.json() as Promise<ProductReleaseInfo>
      })
      .then(setProduct)
      .catch(reason => setError(reason instanceof Error ? reason.message : String(reason)))
    void refreshUpdates()
  }, [])

  const availableUpdates = useMemo(
    () => updates?.releases.filter(release => release.isNewer) ?? [],
    [updates]
  )

  async function updateNow(version: string) {
    if (!window.confirm(`Update LlmProxy to v${version} now? The gateway will restart during deployment.`)) return
    setBusyVersion(version)
    try {
      await api.scheduleProductUpdate({ version, force: true })
      await refreshUpdates()
    } catch (reason) {
      setUpdateError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusyVersion(null)
    }
  }

  async function schedule(version: string) {
    const raw = scheduleTimes[version]
    if (!raw) {
      setUpdateError('Choose a date and time before scheduling the update.')
      return
    }
    const date = new Date(raw)
    if (Number.isNaN(date.getTime())) {
      setUpdateError('The scheduled date/time is invalid.')
      return
    }

    setBusyVersion(version)
    try {
      await api.scheduleProductUpdate({ version, scheduledForUtc: date.toISOString(), force: false })
      await refreshUpdates()
    } catch (reason) {
      setUpdateError(reason instanceof Error ? reason.message : String(reason))
    } finally {
      setBusyVersion(null)
    }
  }

  async function cancelActiveUpdate() {
    const id = updates?.agent?.activeJob?.id
    if (!id) return
    try {
      await api.cancelProductUpdate(id)
      await refreshUpdates()
    } catch (reason) {
      setUpdateError(reason instanceof Error ? reason.message : String(reason))
    }
  }

  const cls = embedded ? 'stack' : 'releasePage'
  if (error === 'AUTH_REQUIRED') return <div className={cls}><div className="notice">Authentication is required. <a href="/auth/login">Sign in with Entra ID</a>.</div></div>
  if (error === 'FORBIDDEN') return <div className={cls}><div className="error">Access denied. Your Entra account does not have an administrative LlmProxy role.</div></div>
  if (error) return <div className={cls}><div className="error">{error}</div></div>
  if (!product) return <div className={cls}><div className="loading">Loading release notes…</div></div>

  const activeJob = updates?.agent?.activeJob

  return <div className={cls}>
    {!embedded && <><div className="releaseHeader">
      <div><span className="releaseEyebrow">{product.product} · {product.channel}</span><h1>Release notes</h1><p>Versions, immutable update plans and administrator-controlled deployment.</p></div>
      <a className="secondary releaseBack" href="/admin/">Back to Admin</a>
    </div><PageDocumentation page="releases" /></>}
    {embedded && <div className="muted">{product.product} · {product.channel}</div>}

    <section className="cards cardsFive">
      <div className="metric"><span>Current version</span><strong>{product.version}</strong></div>
      <div className="metric"><span>Channel</span><strong>{product.channel}</strong></div>
      <div className="metric"><span>Release date</span><strong>{formatDate(product.releasedOn)}</strong></div>
      <div className="metric"><span>Build</span><strong className="releaseBuild">{shortRevision(product.buildRevision)}</strong></div>
      <div className="metric"><span>Updates available</span><strong>{availableUpdates.length}</strong></div>
    </section>

    <section className="panel updatePanel">
      <div className="panelTitle"><div><h2>Host updates</h2><div className="muted">Updates run on the host agent so the operation survives the gateway container restart.</div></div><span>{updates?.agentAvailable ? 'Agent online' : 'Agent unavailable'}</span></div>
      {updateError && <div className="error updateMessage">{friendlyError(updateError)}</div>}
      {!updates && !updateError && <div className="loading updateMessage">Checking published releases and host update agent…</div>}
      {updates && !(updates?.agentAvailable ?? false) && <div className="notice updateMessage">The host update agent is not reachable. Release information remains visible, but Update now / Schedule are disabled until the host is upgraded to a release that installs the update agent.</div>}
      {activeJob && <div className="updateActive">
        <div><strong>{activeJob.status}: v{activeJob.version}</strong><div className="muted">Scheduled {formatDateTime(activeJob.scheduledForUtc)}{activeJob.startedAtUtc ? ` · started ${formatDateTime(activeJob.startedAtUtc)}` : ''}{activeJob.currentStep ? ` · applying v${activeJob.currentStep}` : ''}</div>{activeJob.upgradePath?.length ? <div className="muted">Upgrade path: {activeJob.upgradePath.map(version => `v${version}`).join(' → ')}</div> : null}{activeJob.error && <div className="errorText">{activeJob.error}</div>}</div>
        {canWrite && activeJob.status === 'Pending' && <button className="secondary" onClick={() => void cancelActiveUpdate()}>Cancel scheduled update</button>}
      </div>}

      <div className="updateReleaseList">
        {availableUpdates.map(release => <div className="updateRelease" key={release.version}>
          <div className="updateReleaseInfo">
            <div className="updateVersionLine"><strong>v{release.version}</strong><span className={`updateMode updateMode-${release.updateMode}`}>{release.updateMode === 'custom' ? 'Custom update plan' : 'Standard update'}</span>{release.requiresHostRestart && <span className="updateMode">Host restart required</span>}</div>
            <div>{release.title}</div>
            <div className="muted">Published {formatDateTime(release.publishedAtUtc)} · {release.updateTitle}</div>
            <p>{release.updateDescription}</p>
            <div className="muted">Upgrade path: {upgradePathTo(updates?.releases ?? [], release.version).map(version => `v${version}`).join(' → ')}</div>
            <code className="updateCommand">{release.operatorCommand}</code>
          </div>
          <div className="updateControls">
            {canWrite && <>
              <button className="primary" disabled={!(updates?.agentAvailable ?? false) || busyVersion === release.version} onClick={() => void updateNow(release.version)}>Update now</button>
              <div className="scheduleRow">
                <input aria-label={`Schedule v${release.version}`} type="datetime-local" value={scheduleTimes[release.version] ?? ''} onChange={event => setScheduleTimes(current => ({ ...current, [release.version]: event.target.value }))} />
                <button className="secondary" disabled={!(updates?.agentAvailable ?? false) || busyVersion === release.version} onClick={() => void schedule(release.version)}>Schedule</button>
              </div>
            </>}
            {!canWrite && <span className="muted">Administrator write access is required to launch an update.</span>}
          </div>
        </div>)}
        {updates && availableUpdates.length === 0 && <div className="updateEmpty">This installation is on the latest published stable release.</div>}
      </div>
    </section>

    {updates?.agent?.recentJobs?.length ? <section className="panel">
      <div className="panelTitle"><h2>Recent update jobs</h2><span>Host-agent history</span></div>
      <table><thead><tr><th>Version</th><th>Status</th><th>Scheduled</th><th>Completed</th><th>Result</th></tr></thead><tbody>
        {updates.agent.recentJobs.slice(0, 10).map(job => <tr key={job.id}><td><strong>v{job.version}</strong></td><td>{job.status}</td><td>{formatDateTime(job.scheduledForUtc)}</td><td>{formatDateTime(job.completedAtUtc)}</td><td>{job.error ?? (job.exitCode === 0 ? 'Completed' : '—')}</td></tr>)}
      </tbody></table>
    </section> : null}

    <div className="stack">
      {product.releases.map(release => <section className="panel releaseCard" key={release.version}>
        <div className="panelTitle"><div><h2>v{release.version} · {release.title}</h2><div className="muted">Released {formatDate(release.releasedOn)}</div></div><span>{release.version === product.version ? 'Current' : 'Previous'}</span></div>
        <div className="releaseSections">{Object.entries(release.sections).map(([section, items]) => <div className="releaseSection" key={section}><h3>{section}</h3><ul>{items.map(item => <li key={item}>{item}</li>)}</ul></div>)}</div>
      </section>)}
    </div>
  </div>
}

function formatDate(value: string) {
  const date = value.includes('T') ? new Date(value) : new Date(`${value}T00:00:00Z`)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString()
}
function formatDateTime(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}
function shortRevision(value?: string | null) {
  if (!value) return 'local / unknown'
  return value.length > 12 ? value.slice(0, 12) : value
}
function friendlyError(value: string) {
  if (value === 'AUTH_REQUIRED') return 'Authentication is required.'
  if (value === 'FORBIDDEN') return 'Administrator write access is required.'
  return value
}

function upgradePathTo(releases: ProductUpdateOverview['releases'], target: string) {
  const targetParts = semverParts(target)
  if (!targetParts) return [target]
  return releases
    .filter(release => release.isNewer)
    .filter(release => {
      const parts = semverParts(release.version)
      return parts !== null && compareSemver(parts, targetParts) <= 0
    })
    .sort((left, right) => compareSemver(semverParts(left.version)!, semverParts(right.version)!))
    .map(release => release.version)
}
function semverParts(value: string): [number, number, number] | null {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(value)
  return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null
}
function compareSemver(left: [number, number, number], right: [number, number, number]) {
  return left[0] - right[0] || left[1] - right[1] || left[2] - right[2]
}
