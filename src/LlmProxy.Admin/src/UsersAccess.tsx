import { FormEvent, useCallback, useEffect, useState } from 'react'
import { api } from './api'
import type { PlatformUser, PlatformUserAccessSettings, UsageGroup } from './types'

export default function UsersAccess() {
  const [settings, setSettings] = useState<PlatformUserAccessSettings | null>(null)
  const [users, setUsers] = useState<PlatformUser[]>([])
  const [groups, setGroups] = useState<UsageGroup[]>([])
  const [newUserGroupId, setNewUserGroupId] = useState('')
  const [mode, setMode] = useState<'automatic' | 'manual'>('manual')
  const [tenantId, setTenantId] = useState('')
  const [objectId, setObjectId] = useState('')
  const [principalName, setPrincipalName] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      const [nextSettings, nextUsers, nextGroups] = await Promise.all([
        api.platformUserAccessSettings(),
        api.platformUsers(),
        api.usageGroups()
      ])
      setSettings(nextSettings)
      setMode(nextSettings.provisioningMode)
      setTenantId(current => current || nextSettings.configuredTenantId || '')
      setUsers(nextUsers)
      setGroups(nextGroups)
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])

  async function saveMode() {
    try {
      const next = await api.updatePlatformUserAccessSettings(mode)
      setSettings(current => ({ ...(current ?? next), ...next }))
      setMessage(mode === 'automatic'
        ? 'Automatic provisioning enabled. Any authenticated Entra user who opens the user portal is registered as an active normal user.'
        : 'Manual provisioning enabled. Only users already registered and enabled by an administrator can enter the user portal.')
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function addUser(event: FormEvent) {
    event.preventDefault()
    try {
      await api.createPlatformUser({
        objectId,
        tenantId: tenantId || null,
        principalName: principalName || null,
        displayName: displayName || null,
        enabled: true,
        usageGroupId: newUserGroupId || null
      })
      setObjectId('')
      setPrincipalName('')
      setDisplayName('')
      setNewUserGroupId('')
      setMessage('User registered and enabled.')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function changeGroup(user: PlatformUser, usageGroupId: string) {
    try {
      await api.assignPlatformUserUsageGroup(user.id, usageGroupId || null)
      setMessage('User group updated. Personal API keys now inherit the same group for new requests; historical usage remains unchanged.')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function toggle(user: PlatformUser) {
    try {
      if (user.enabled) {
        await api.disablePlatformUser(user.id)
        setMessage('User disabled. Portal access is blocked and all currently active personal API keys were revoked.')
      } else {
        await api.enablePlatformUser(user.id)
        setMessage('User enabled. Portal access is restored; previously revoked API keys remain revoked and the user can create new ones.')
      }
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  return <div className="stack">
    {error && <div className="error">{error}</div>}
    {message && <div className="notice">{message}</div>}

    <div className="gridTwo">
      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>User provisioning policy</h2><span>Entra-authenticated user portal</span></div>
        <label>Provisioning mode
          <select value={mode} onChange={event => setMode(event.target.value as 'automatic' | 'manual')}>
            <option value="manual">Manual · admin census only</option>
            <option value="automatic">Automatic · register on first access</option>
          </select>
        </label>
        <p className="muted">
          In automatic mode, the first successful Entra sign-in to <code>/admin/me</code> creates an enabled normal user.
          In manual mode, an authenticated person is denied until an administrator registers the stable Entra tenant/object ID.
        </p>
        <button className="primary" onClick={() => void saveMode()}>Save provisioning mode</button>
        {settings && <p className="muted">Last updated {new Date(settings.updatedAtUtc).toLocaleString()}</p>}
      </section>

      <section className="panel formPanel">
        <div className="panelTitle tuningTitle"><h2>Register user manually</h2><span>Stable identity = tenant ID + object ID</span></div>
        <form onSubmit={addUser}>
          <label>Tenant ID<input value={tenantId} onChange={event => setTenantId(event.target.value)} placeholder="Microsoft Entra tenant ID" /></label>
          <label>Object ID<input value={objectId} onChange={event => setObjectId(event.target.value)} required placeholder="Entra user object ID (oid)" /></label>
          <label>Email / principal name<input value={principalName} onChange={event => setPrincipalName(event.target.value)} placeholder="user@example.com (display metadata)" /></label>
          <label>Display name<input value={displayName} onChange={event => setDisplayName(event.target.value)} placeholder="Example User" /></label>
          <label>Usage group<select value={newUserGroupId} onChange={event => setNewUserGroupId(event.target.value)}><option value="">Ungrouped</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label>
          <p className="muted">Email is not the security key because it can change. Access is matched against Entra <code>tid</code> + <code>oid</code>.</p>
          <button className="primary">Register user</button>
        </form>
      </section>
    </div>

    <section className="panel">
      <div className="panelTitle"><h2>Registered users</h2><span>{users.length} users</span></div>
      <table>
        <thead><tr><th>User</th><th>Stable identity</th><th>Group</th><th>State</th><th>Source</th><th>30d calls</th><th>Personal keys</th><th>Last seen</th><th>Action</th></tr></thead>
        <tbody>
          {users.map(user => <tr key={user.id}>
            <td><strong>{user.displayName ?? user.principalName ?? user.objectId}</strong><div className="muted">{user.principalName ?? '—'}</div></td>
            <td><div className="mono">{user.objectId}</div><div className="muted mono">{user.tenantId}</div></td>
            <td><select aria-label={`Usage group for ${user.displayName ?? user.principalName ?? user.objectId}`} value={user.usageGroupId ?? ''} onChange={event => void changeGroup(user, event.target.value)}><option value="">Ungrouped</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></td>
            <td>{user.enabled ? 'Enabled' : 'Disabled'}</td>
            <td>{friendlySource(user.provisioningSource)}</td>
            <td>{new Intl.NumberFormat().format(user.requestCount30d)}{user.errorCount30d > 0 && <div className="muted">{user.errorCount30d} errors</div>}</td>
            <td>{user.activeCredentialCount} active / {user.credentialCount}</td>
            <td>{formatDate(user.lastSeenAtUtc ?? user.lastCredentialUsedAtUtc)}</td>
            <td><button className={user.enabled ? 'secondary' : 'primary'} onClick={() => void toggle(user)}>{user.enabled ? 'Disable user' : 'Enable user'}</button></td>
          </tr>)}
          {users.length === 0 && <tr><td colSpan={9} className="muted">No registered end users yet.</td></tr>}
        </tbody>
      </table>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>GitHub Copilot identity note</h2><span>Shared provider key ≠ end-user identity</span></div>
      <p>When one organization-level GitHub Copilot custom model uses one shared LlmProxy API key, LlmProxy can reliably attribute calls to that credential, but not to the individual developer unless GitHub sends a documented per-user identity signal. Do not infer a person from IP, User-Agent or other unstable metadata.</p>
      <p>For deterministic per-user attribution, use one LlmProxy personal key per user/client configuration, or introduce a trusted signed identity token/header from a component that actually authenticates the user.</p>
    </section>
  </div>
}

function friendlySource(value: string) {
  return ({ automatic: 'Automatic', admin: 'Admin', migration: 'Existing user' } as Record<string, string>)[value] ?? value
}

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}
