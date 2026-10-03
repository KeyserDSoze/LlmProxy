import { FormEvent, useCallback, useEffect, useState } from 'react'
import PageDocumentation from './PageDocumentation'

type Identity = {
  tenantId: string
  objectId: string
  principalName?: string | null
  displayName?: string | null
  roles: string[]
}

type PersonalCredential = {
  id: string
  name: string
  keyPrefix: string
  enabled: boolean
  createdAtUtc: string
  expiresAtUtc?: string | null
  lastUsedAtUtc?: string | null
  usageGroupId?: string | null
}

type CreatedCredential = PersonalCredential & { secret: string }

type CredentialUsage = {
  apiCredentialId: string
  name: string
  keyPrefix?: string | null
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
}

type PersonalRateLimit = {
  id: string
  scope: 'user' | 'group'
  scopeName?: string | null
  logicalModel?: string | null
  requestsPerWindow: number
  windowSeconds: number
  outputTokensPerWindow?: number | null
  maxOutputTokensPerRequest?: number | null
  enabled: boolean
  updatedAtUtc: string
}

type PersonalRequest = {
  requestId: string
  startedAtUtc: string
  logicalModel: string
  surface: string
  statusCode: number
  durationMilliseconds: number
  isStreaming: boolean
  timeToFirstByteMilliseconds?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  errorCode?: string | null
  apiCredentialId?: string | null
}

type PersonalUsage = {
  windowDays: number
  sinceUtc: string
  historicalRollupsUsed: boolean
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
  credentials: CredentialUsage[]
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: init?.body ? { 'Content-Type': 'application/json', ...(init.headers ?? {}) } : init?.headers,
    ...init
  })
  if (response.status === 401) throw new Error('AUTH_REQUIRED')
  if (response.status === 403) throw new Error('FORBIDDEN')
  if (!response.ok) throw new Error((await response.text()) || `${response.status} ${response.statusText}`)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}

function formatNumber(value: number) {
  return new Intl.NumberFormat().format(value)
}

export default function UserPortal() {
  const [identity, setIdentity] = useState<Identity | null>(null)
  const [credentials, setCredentials] = useState<PersonalCredential[]>([])
  const [usage, setUsage] = useState<PersonalUsage | null>(null)
  const [rateLimits, setRateLimits] = useState<PersonalRateLimit[]>([])
  const [requests, setRequests] = useState<PersonalRequest[]>([])
  const [name, setName] = useState('')
  const [created, setCreated] = useState<CreatedCredential | null>(null)
  const [loading, setLoading] = useState(true)
  const [authRequired, setAuthRequired] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextIdentity, nextCredentials, nextUsage, nextRateLimits, nextRequests] = await Promise.all([
        request<Identity>('/api/me'),
        request<PersonalCredential[]>('/api/me/api-credentials'),
        request<PersonalUsage>('/api/me/usage?days=30'),
        request<PersonalRateLimit[]>('/api/me/rate-limits'),
        request<PersonalRequest[]>('/api/me/requests?take=50')
      ])
      setIdentity(nextIdentity)
      setCredentials(nextCredentials)
      setUsage(nextUsage)
      setRateLimits(nextRateLimits)
      setRequests(nextRequests)
      setAuthRequired(false)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      if (message === 'AUTH_REQUIRED') setAuthRequired(true)
      else if (message === 'FORBIDDEN') setError('Access denied. Your Entra identity is not enabled in LlmProxy. Ask an administrator to register or re-enable your user account.')
      else setError(message)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])

  async function createCredential(event: FormEvent) {
    event.preventDefault()
    try {
      setError(null)
      const next = await request<CreatedCredential>('/api/me/api-credentials', {
        method: 'POST',
        body: JSON.stringify({ name })
      })
      setCreated(next)
      setName('')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function revoke(id: string) {
    setCreated(null)
    await request<void>(`/api/me/api-credentials/${id}/revoke`, { method: 'POST' })
    await refresh()
  }

  async function rotate(id: string) {
    const next = await request<CreatedCredential>(`/api/me/api-credentials/${id}/rotate`, { method: 'POST' })
    setCreated(next)
    await refresh()
  }

  return <div className="shell">
    <aside className="sidebar">
      <div className="brand"><div className="brandMark">LP</div><div><strong>LlmProxy</strong><span>User portal</span></div></div>
      <nav><button className="active">My dashboard</button></nav>
      <div className="sidebarFooter"><span className="dot" /> Entra identity</div>
    </aside>
    <main>
      <header>
        <div><h1>My dashboard</h1><p>Your API keys, usage, limits and recent LlmProxy calls.</p></div>
        <div className="actions"><button className="secondary" onClick={() => void refresh()}>Refresh</button><a href="/auth/logout">Sign out</a></div>
      </header>

      <PageDocumentation page="me" />

      {authRequired && <div className="notice">Authentication is required. <a href="/auth/user-login">Sign in with Entra ID</a>.</div>}
      {error && <div className="error">{error}</div>}
      {loading ? <div className="loading">Loading your LlmProxy profile…</div> : !authRequired && <>
        {identity && <section className="panel">
          <div className="panelTitle"><h2>{identity.displayName ?? identity.principalName ?? 'Signed-in user'}</h2><span>{identity.principalName}</span></div>
          <p className="muted">Personal keys are bound to stable Entra tenant/object identity. Your access can be disabled centrally by an administrator.</p>
        </section>}

        {usage && <section className="cards cardsFive">
          <Metric label="30d requests" value={formatNumber(usage.requestCount)} />
          <Metric label="Input tokens" value={formatNumber(usage.inputTokens)} />
          <Metric label="Output tokens" value={formatNumber(usage.outputTokens)} />
          <Metric label="Errors" value={formatNumber(usage.errorCount)} />
          <Metric label="Rate limited" value={formatNumber(usage.rateLimitedRequests)} />
        </section>}

        <section className="panel">
          <div className="panelTitle"><h2>My limits</h2><span>User and group policies apply to every personal API key.</span></div>
          <table><thead><tr><th>Scope</th><th>Model</th><th>Request limit</th><th>Output-token budget</th><th>Status</th><th>Updated</th></tr></thead><tbody>
            {rateLimits.map(policy => <tr key={policy.id}>
              <td><strong>{policy.scope === 'group' ? 'Group' : 'User'}</strong><div className="muted">{policy.scopeName ?? '—'}</div></td>
              <td>{policy.logicalModel ?? 'All models'}</td>
              <td><strong>{formatNumber(policy.requestsPerWindow)}</strong> / {policy.windowSeconds}s</td>
              <td>{policy.outputTokensPerWindow && policy.maxOutputTokensPerRequest ? <>{formatNumber(policy.outputTokensPerWindow)} / {policy.windowSeconds}s<div className="muted">max {formatNumber(policy.maxOutputTokensPerRequest)} / request</div></> : 'Not set'}</td>
              <td>{policy.enabled ? 'Enabled' : 'Disabled'}</td><td>{formatDate(policy.updatedAtUtc)}</td>
            </tr>)}
            {rateLimits.length === 0 && <tr><td colSpan={6} className="muted">No user or group caller quota is configured.</td></tr>}
          </tbody></table>
        </section>

        <section className="panel">
          <div className="panelTitle"><h2>My recent calls</h2><span>Latest {requests.length} requests from your personal keys</span></div>
          <table><thead><tr><th>Time</th><th>Model</th><th>Surface</th><th>Status</th><th>TTFT</th><th>Duration</th><th>Tokens</th><th>Error</th></tr></thead><tbody>
            {requests.map(item => <tr key={item.requestId}>
              <td>{formatDate(item.startedAtUtc)}</td><td><strong>{item.logicalModel}</strong></td><td>{item.surface}{item.isStreaming ? ' · SSE' : ''}</td>
              <td>{item.statusCode}</td><td>{item.timeToFirstByteMilliseconds == null ? '—' : item.timeToFirstByteMilliseconds + ' ms'}</td>
              <td>{item.durationMilliseconds} ms</td><td>{item.totalTokens ?? '—'}</td><td className="mono">{item.errorCode ?? '—'}</td>
            </tr>)}
            {requests.length === 0 && <tr><td colSpan={8} className="muted">No calls recorded for your personal API keys yet.</td></tr>}
          </tbody></table>
        </section>

        <div className="gridTwo">
          <section className="panel">
            <div className="panelTitle"><h2>Personal credentials</h2><span>{credentials.length} keys</span></div>
            <table><thead><tr><th>Name</th><th>Prefix</th><th>State</th><th>Created</th><th>Last used</th><th>Action</th></tr></thead><tbody>
              {credentials.map(item => <tr key={item.id}>
                <td><strong>{item.name}</strong></td><td className="mono">{item.keyPrefix}…</td><td>{item.enabled ? 'Enabled' : 'Revoked'}</td>
                <td>{formatDate(item.createdAtUtc)}</td><td>{formatDate(item.lastUsedAtUtc)}</td>
                <td className="actions">{item.enabled && <><button onClick={() => void rotate(item.id)}>Rotate</button><button onClick={() => void revoke(item.id)}>Revoke</button></>}</td>
              </tr>)}
              {credentials.length === 0 && <tr><td colSpan={6} className="muted">No personal API keys yet.</td></tr>}
            </tbody></table>
          </section>

          <section className="panel formPanel">
            <h2>Create personal API key</h2>
            <form onSubmit={createCredential}>
              <label>Name<input value={name} onChange={event => setName(event.target.value)} required placeholder="Project Alpha / Development" /></label>
              <button className="primary">Generate API key</button>
            </form>
            {created && <div className="secretBox"><strong>Copy this key now</strong><p>It will not be shown again. Store it in your application's secret manager.</p><code>{created.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(created.secret)}>Copy</button></div>}
          </section>
        </div>

        {usage && <section className="panel">
          <div className="panelTitle"><h2>Usage by API key</h2><span>{usage.historicalRollupsUsed ? 'Includes historical rollups' : 'Recent telemetry'}</span></div>
          <table><thead><tr><th>Credential</th><th>Requests</th><th>Errors</th><th>Input tokens</th><th>Output tokens</th><th>Rate limited</th></tr></thead><tbody>
            {usage.credentials.map(item => <tr key={item.apiCredentialId}><td><strong>{item.name}</strong><div className="muted mono">{item.keyPrefix}…</div></td><td>{formatNumber(item.requestCount)}</td><td>{formatNumber(item.errorCount)}</td><td>{formatNumber(item.inputTokens)}</td><td>{formatNumber(item.outputTokens)}</td><td>{formatNumber(item.rateLimitedRequests)}</td></tr>)}
          </tbody></table>
          <p className="muted">Personal keys always participate in caller governance. User, group and optional credential/model policies compose together; the most restrictive output cap applies per request.</p>
        </section>}
      </>}
    </main>
  </div>
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return <div className="metric"><span>{label}</span><strong>{value}</strong></div>
}
