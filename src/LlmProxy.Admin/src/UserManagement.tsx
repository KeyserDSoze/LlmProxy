import { useEffect, useState } from 'react'
import { api } from './api'
import type { IdentityUserSummary } from './types'

export default function UserManagement() {
  const [users, setUsers] = useState<IdentityUserSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.identityUsers()
      .then(setUsers)
      .catch(reason => setError(reason instanceof Error ? reason.message : String(reason)))
      .finally(() => setLoading(false))
  }, [])

  if (loading) return <div className="loading">Loading users…</div>
  if (error) return <div className="error">{error}</div>

  return <div className="stack">
    <section className="cards">
      <Metric label="Known users" value={users.length} />
      <Metric label="Personal API keys" value={users.reduce((total, user) => total + user.credentialCount, 0)} />
      <Metric label="Active personal keys" value={users.reduce((total, user) => total + user.activeCredentialCount, 0)} />
      <Metric label="Users with active keys" value={users.filter(user => user.activeCredentialCount > 0).length} />
    </section>

    <section className="panel">
      <div className="panelTitle">
        <h2>User inventory</h2>
        <span>Entra identities known through personal API-key ownership</span>
      </div>
      <table>
        <thead><tr><th>User</th><th>Tenant / object</th><th>Keys</th><th>Active keys</th><th>First key</th><th>Last use</th></tr></thead>
        <tbody>
          {users.map(user => <tr key={`${user.tenantId}|${user.objectId}`}>
            <td><strong>{user.principalName ?? 'Unknown principal'}</strong></td>
            <td><div className="mono">{short(user.tenantId)}</div><div className="muted mono">{short(user.objectId)}</div></td>
            <td>{user.credentialCount}</td>
            <td>{user.activeCredentialCount}</td>
            <td>{formatDate(user.firstCredentialCreatedAtUtc)}</td>
            <td>{formatDate(user.lastUsedAtUtc)}</td>
          </tr>)}
          {users.length === 0 && <tr><td colSpan={6} className="muted">No Entra users with personal API keys have been observed yet.</td></tr>}
        </tbody>
      </table>
    </section>

    <div className="notice">
      User inventory is based on identities that have created personal API keys. Per-user and per-key limits remain configurable under Usage & Governance.
    </div>
  </div>
}

function Metric({ label, value }: { label: string; value: number }) {
  return <div className="metric"><span>{label}</span><strong>{value}</strong></div>
}
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function short(value: string) { return value.length > 18 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value }
