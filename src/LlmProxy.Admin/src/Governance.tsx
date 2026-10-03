import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { CreatedApiCredential, GovernanceCredential, Model, PlatformUser, RateLimitPolicy, UsageGroup, UsageGroupRateLimitPolicy, UsageReport, UserRateLimitPolicy, UserUsageSummary } from './types'

const emptyUsage: UsageReport = {
  windowDays: 30,
  sinceUtc: '',
  windowGranularity: 'utc_day',
  rawRetentionDays: 90,
  rollupRetentionDays: 730,
  rawRequestCount: 0,
  rolledUpRequestCount: 0,
  historicalRollupsUsed: false,
  requestCount: 0,
  errorCount: 0,
  inputTokens: 0,
  outputTokens: 0,
  totalTokens: 0,
  rateLimitedRequests: 0,
  capacityExhaustedRequests: 0,
  groups: [],
  credentials: [],
  models: []
}

export default function Governance() {
  const [days, setDays] = useState(30)
  const [groups, setGroups] = useState<UsageGroup[]>([])
  const [credentials, setCredentials] = useState<GovernanceCredential[]>([])
  const [rateLimits, setRateLimits] = useState<RateLimitPolicy[]>([])
  const [users, setUsers] = useState<PlatformUser[]>([])
  const [userRateLimits, setUserRateLimits] = useState<UserRateLimitPolicy[]>([])
  const [groupRateLimits, setGroupRateLimits] = useState<UsageGroupRateLimitPolicy[]>([])
  const [userUsage, setUserUsage] = useState<UserUsageSummary[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [usage, setUsage] = useState<UsageReport>(emptyUsage)
  const [groupName, setGroupName] = useState('')
  const [groupDescription, setGroupDescription] = useState('')
  const [rateCredentialId, setRateCredentialId] = useState('')
  const [rateModel, setRateModel] = useState('')
  const [requestsPerWindow, setRequestsPerWindow] = useState(60)
  const [windowSeconds, setWindowSeconds] = useState(60)
  const [rateUserKey, setRateUserKey] = useState('')
  const [userRateModel, setUserRateModel] = useState('')
  const [userRequestsPerWindow, setUserRequestsPerWindow] = useState(300)
  const [userWindowSeconds, setUserWindowSeconds] = useState(60)
  const [userOutputTokensPerWindow, setUserOutputTokensPerWindow] = useState(100000)
  const [userMaxOutputTokensPerRequest, setUserMaxOutputTokensPerRequest] = useState(4096)
  const [groupPolicyGroupId, setGroupPolicyGroupId] = useState('')
  const [groupRateModel, setGroupRateModel] = useState('')
  const [groupRequestsPerWindow, setGroupRequestsPerWindow] = useState(1000)
  const [groupWindowSeconds, setGroupWindowSeconds] = useState(60)
  const [groupOutputTokensPerWindow, setGroupOutputTokensPerWindow] = useState(500000)
  const [groupMaxOutputTokensPerRequest, setGroupMaxOutputTokensPerRequest] = useState(4096)
  const [budgetPolicyId, setBudgetPolicyId] = useState('')
  const [outputTokensPerWindow, setOutputTokensPerWindow] = useState(100000)
  const [maxOutputTokensPerRequest, setMaxOutputTokensPerRequest] = useState(4096)
  const [rotatedCredential, setRotatedCredential] = useState<CreatedApiCredential | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const refresh = useCallback(async () => {
    try {
      setError(null)
      const [nextGroups, nextCredentials, nextRateLimits, nextUsers, nextUserRateLimits, nextGroupRateLimits, nextUsage, nextUserUsage, nextModels] = await Promise.all([
        api.usageGroups(),
        api.governanceCredentials(),
        api.rateLimits(),
        api.platformUsers(),
        api.userRateLimits(),
        api.groupRateLimits(),
        api.usageSummary(days),
        api.usageUsers(days),
        api.models()
      ])
      setGroups(nextGroups)
      setCredentials(nextCredentials)
      setRateLimits(nextRateLimits)
      setUsers(nextUsers)
      setUserRateLimits(nextUserRateLimits)
      setGroupRateLimits(nextGroupRateLimits)
      setUsage(nextUsage)
      setUserUsage(nextUserUsage)
      setModels(nextModels)
      setRateCredentialId(current => current || nextCredentials[0]?.id || '')
      setRateUserKey(current => current || (nextUsers[0] ? `${nextUsers[0].tenantId}|${nextUsers[0].objectId}` : ''))
      setGroupPolicyGroupId(current => current || nextGroups[0]?.id || '')
      setBudgetPolicyId(current => current && nextRateLimits.some(policy => policy.id === current) ? current : nextRateLimits[0]?.id || '')
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      if (message === 'AUTH_REQUIRED') window.location.assign('/auth/login')
      else if (message === 'FORBIDDEN') setError('Access denied. Your Entra account does not have the required LlmProxy administrative role.')
      else setError(message)
    } finally {
      setLoading(false)
    }
  }, [days])

  useEffect(() => { void refresh() }, [refresh])

  const groupNames = useMemo(() => new Map(groups.map(group => [group.id, group.name])), [groups])
  const budgetPolicy = useMemo(() => rateLimits.find(policy => policy.id === budgetPolicyId), [budgetPolicyId, rateLimits])

  useEffect(() => {
    if (!budgetPolicy) return
    setOutputTokensPerWindow(budgetPolicy.outputTokensPerWindow ?? 100000)
    setMaxOutputTokensPerRequest(budgetPolicy.maxOutputTokensPerRequest ?? 4096)
  }, [budgetPolicy])

  async function createGroup(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    await api.createUsageGroup({ name: groupName, description: groupDescription || null })
    setGroupName('')
    setGroupDescription('')
    setMessage('Usage group created.')
    await refresh()
  }

  async function changeCredentialGroup(credentialId: string, usageGroupId: string) {
    setMessage(null)
    if (usageGroupId) await api.assignCredentialUsageGroup(credentialId, usageGroupId)
    else await api.clearCredentialUsageGroup(credentialId)
    setMessage('Credential group updated. New requests use the new group; historical usage is unchanged.')
    await refresh()
  }

  async function toggleCredentialGovernance(credential: GovernanceCredential) {
    if (credential.kind === 'personal') return
    await api.updateCredentialCallerGovernance(credential.id, !credential.enforceCallerGovernance)
    setMessage(`Organization credential ${credential.enforceCallerGovernance ? 'exempted from' : 'opted into'} caller governance.`)
    await refresh()
  }

  async function rotateCredential(credential: GovernanceCredential) {
    setMessage(null)
    setRotatedCredential(null)
    try {
      const rotated = await api.rotateApiCredential(credential.id)
      setRotatedCredential(rotated)
      setMessage(`Credential ${credential.name} rotated. The previous secret is now invalid.`)
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function createRateLimit(event: FormEvent) {
    event.preventDefault()
    if (!rateCredentialId) return
    setMessage(null)
    const created = await api.createRateLimit({ apiCredentialId: rateCredentialId, logicalModel: rateModel || null, requestsPerWindow, windowSeconds, enabled: true })
    setBudgetPolicyId(created.id)
    setMessage('Rate-limit policy created and applied live.')
    await refresh()
  }

  async function toggleRateLimit(policy: RateLimitPolicy) {
    await api.updateRateLimit(policy.id, {
      logicalModel: policy.logicalModel ?? null,
      requestsPerWindow: policy.requestsPerWindow,
      windowSeconds: policy.windowSeconds,
      enabled: !policy.enabled,
      outputTokensPerWindow: policy.outputTokensPerWindow,
      maxOutputTokensPerRequest: policy.maxOutputTokensPerRequest
    })
    setMessage(`Rate-limit policy ${policy.enabled ? 'disabled' : 'enabled'} live.`)
    await refresh()
  }

  async function deleteRateLimit(policy: RateLimitPolicy) {
    await api.deleteRateLimit(policy.id)
    setMessage('Rate-limit policy removed.')
    await refresh()
  }

  async function createUserRateLimit(event: FormEvent) {
    event.preventDefault()
    if (!rateUserKey) return
    const separator = rateUserKey.indexOf('|')
    if (separator < 1) return
    const ownerTenantId = rateUserKey.slice(0, separator)
    const ownerObjectId = rateUserKey.slice(separator + 1)
    setMessage(null)
    await api.createUserRateLimit({
      ownerTenantId,
      ownerObjectId,
      logicalModel: userRateModel || null,
      requestsPerWindow: userRequestsPerWindow,
      windowSeconds: userWindowSeconds,
      enabled: true,
      outputTokensPerWindow: userOutputTokensPerWindow,
      maxOutputTokensPerRequest: userMaxOutputTokensPerRequest
    })
    setMessage('User rate-limit policy created and applied live across all personal keys.')
    await refresh()
  }

  async function toggleUserRateLimit(policy: UserRateLimitPolicy) {
    await api.updateUserRateLimit(policy.id, {
      logicalModel: policy.logicalModel ?? null,
      requestsPerWindow: policy.requestsPerWindow,
      windowSeconds: policy.windowSeconds,
      enabled: !policy.enabled,
      outputTokensPerWindow: policy.outputTokensPerWindow,
      maxOutputTokensPerRequest: policy.maxOutputTokensPerRequest
    })
    setMessage(`User rate-limit policy ${policy.enabled ? 'disabled' : 'enabled'} live.`)
    await refresh()
  }

  async function deleteUserRateLimit(policy: UserRateLimitPolicy) {
    await api.deleteUserRateLimit(policy.id)
    setMessage('User rate-limit policy removed.')
    await refresh()
  }

  async function createGroupRateLimit(event: FormEvent) {
    event.preventDefault()
    if (!groupPolicyGroupId) return
    await api.createGroupRateLimit({
      usageGroupId: groupPolicyGroupId,
      logicalModel: groupRateModel || null,
      requestsPerWindow: groupRequestsPerWindow,
      windowSeconds: groupWindowSeconds,
      enabled: true,
      outputTokensPerWindow: groupOutputTokensPerWindow,
      maxOutputTokensPerRequest: groupMaxOutputTokensPerRequest
    })
    setMessage('Group request and output-token quota created and applied live.')
    await refresh()
  }

  async function toggleGroupRateLimit(policy: UsageGroupRateLimitPolicy) {
    await api.updateGroupRateLimit(policy.id, {
      logicalModel: policy.logicalModel ?? null,
      requestsPerWindow: policy.requestsPerWindow,
      windowSeconds: policy.windowSeconds,
      enabled: !policy.enabled,
      outputTokensPerWindow: policy.outputTokensPerWindow,
      maxOutputTokensPerRequest: policy.maxOutputTokensPerRequest
    })
    setMessage(`Group quota ${policy.enabled ? 'disabled' : 'enabled'} live.`)
    await refresh()
  }

  async function deleteGroupRateLimit(policy: UsageGroupRateLimitPolicy) {
    await api.deleteGroupRateLimit(policy.id)
    setMessage('Group quota removed.')
    await refresh()
  }

  async function applyOutputTokenBudget(event: FormEvent) {
    event.preventDefault()
    if (!budgetPolicyId) return
    setMessage(null)
    await api.setOutputTokenBudget(budgetPolicyId, outputTokensPerWindow, maxOutputTokensPerRequest)
    setMessage('Output-token budget updated and applied live.')
    await refresh()
  }

  async function clearOutputTokenBudget() {
    if (!budgetPolicyId) return
    setMessage(null)
    await api.clearOutputTokenBudget(budgetPolicyId)
    setMessage('Output-token budget cleared. Request-rate policy remains active.')
    await refresh()
  }

  if (loading) return <div className="loading">Loading usage governance…</div>

  return <>
    {error && <div className="error">{error}</div>}
    {message && <div className="notice">{message}</div>}

    <section className="panel">
      <div className="panelTitle">
        <div><h2>Usage & Governance</h2><span>Caller identity, group attribution, rate limits, output-token budgets and consolidated usage.</span></div>
        <div className="actions">
          <select aria-label="Usage window" value={days} onChange={event => setDays(Number(event.target.value))}>
            <option value={7}>Last 7 UTC days</option><option value={30}>Last 30 UTC days</option><option value={90}>Last 90 UTC days</option><option value={180}>Last 180 UTC days</option><option value={365}>Last 365 UTC days</option><option value={730}>Last 730 UTC days</option>
          </select>
          <button className="secondary" onClick={() => void refresh()}>Refresh</button>
        </div>
      </div>
      <div className="cards cardsFive">
        <GovernanceMetric label="Requests" value={formatNumber(usage.requestCount)} />
        <GovernanceMetric label="Total tokens" value={formatNumber(usage.totalTokens)} />
        <GovernanceMetric label="Output tokens" value={formatNumber(usage.outputTokens)} />
        <GovernanceMetric label="Rate limited" value={formatNumber(usage.rateLimitedRequests)} />
        <GovernanceMetric label="Capacity exhausted" value={formatNumber(usage.capacityExhaustedRequests)} />
      </div>
      <div className="muted">UTC calendar-day reporting · raw request metrics {usage.rawRetentionDays}d · daily usage rollups {usage.rollupRetentionDays}d.</div>
      {usage.historicalRollupsUsed && <div className="notice" data-testid="historical-rollup-notice">Historical rollups included: {formatNumber(usage.rolledUpRequestCount)} rolled-up requests + {formatNumber(usage.rawRequestCount)} raw requests in this window.</div>}
    </section>

    <section className="panel">
      <div className="panelTitle"><h2>Usage by group</h2><span>Historical attribution uses the group snapshot captured at request time.</span></div>
      <table><thead><tr><th>Group</th><th>Requests</th><th>Input tokens</th><th>Output tokens</th><th>Errors</th><th>Rate limited</th><th>Avg TTFT</th></tr></thead>
        <tbody>{usage.groups.length === 0 ? <tr><td colSpan={7}>No usage in this window.</td></tr> : usage.groups.map(row => <tr key={row.usageGroupId ?? 'ungrouped'}>
          <td><strong>{row.name}</strong></td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.inputTokens)}</td><td>{formatNumber(row.outputTokens)}</td><td>{formatNumber(row.errorCount)}</td><td>{formatNumber(row.rateLimitedRequests)}</td><td>{formatLatency(row.averageTtftMilliseconds)}</td>
        </tr>)}</tbody>
      </table>
    </section>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>User / usage groups</h2><span>{groups.length} configured · one current group per user</span></div>
        <table><thead><tr><th>Name</th><th>Description</th><th>Users</th><th>Credentials</th></tr></thead><tbody>{groups.map(group => <tr key={group.id}><td><strong>{group.name}</strong></td><td>{group.description ?? '—'}</td><td>{group.userCount}</td><td>{group.credentialCount}</td></tr>)}</tbody></table>
      </section>
      <section className="panel formPanel"><h2>Create usage group</h2><form onSubmit={event => void createGroup(event)}>
        <label>Name<input value={groupName} onChange={event => setGroupName(event.target.value)} required placeholder="Development CRM" /></label>
        <label>Description<input value={groupDescription} onChange={event => setGroupDescription(event.target.value)} placeholder="Copilot usage for the CRM team" /></label>
        <button className="primary">Create group</button>
      </form></section>
    </div>

    <section className="panel">
      <div className="panelTitle"><h2>Organization & personal credentials</h2><span>Organization keys are caller-quota exempt by default; admins can opt them in. Personal keys are always governed.</span></div>
      <table><thead><tr><th>Credential</th><th>Type</th><th>Prefix</th><th>Status</th><th>Usage group</th><th>Caller governance</th><th>Actions</th></tr></thead><tbody>{credentials.map(credential => <tr key={credential.id}>
        <td><strong>{credential.name}</strong>{credential.ownerPrincipalName && <div className="muted">{credential.ownerPrincipalName}</div>}</td>
        <td>{credential.kind === 'personal' ? 'Personal' : 'Organization'}</td>
        <td className="mono">{credential.keyPrefix}</td><td>{credential.enabled ? 'Enabled' : 'Revoked'}</td>
        <td><select aria-label={`Usage group for ${credential.name}`} value={credential.usageGroupId ?? ''} onChange={event => void changeCredentialGroup(credential.id, event.target.value)}><option value="">Ungrouped</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></td>
        <td>{credential.kind === 'personal'
          ? <><strong>Always on</strong><div className="muted">User + group + key policies</div></>
          : <label><input type="checkbox" checked={Boolean(credential.enforceCallerGovernance)} onChange={() => void toggleCredentialGovernance(credential)} /> Treat like governed client</label>}</td>
        <td className="actions">{credential.enabled && <button aria-label={`Rotate ${credential.name}`} onClick={() => void rotateCredential(credential)}>Rotate</button>}</td>
      </tr>)}</tbody></table>
      {rotatedCredential && <div className="secretBox" data-testid="rotated-credential-secret"><strong>Copy the rotated key now</strong><p>The previous key is invalid and this secret will not be shown again.</p><code>{rotatedCredential.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(rotatedCredential.secret)}>Copy</button></div>}
    </section>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>Rate limits</h2><span>Caller governance; distinct from DGX capacity backpressure.</span></div>
        <table><thead><tr><th>Credential</th><th>Model</th><th>Request limit</th><th>Output-token budget</th><th>Status</th><th>Actions</th></tr></thead><tbody>{rateLimits.length === 0 ? <tr><td colSpan={6}>No rate limits configured.</td></tr> : rateLimits.map(policy => <tr key={policy.id}><td><strong>{policy.credentialName ?? policy.apiCredentialId}</strong><div className="muted mono">{policy.keyPrefix}</div></td><td>{policy.logicalModel ?? 'All models'}</td><td>{formatNumber(policy.requestsPerWindow)} / {policy.windowSeconds}s</td><td>{policy.outputTokensPerWindow && policy.maxOutputTokensPerRequest ? <><strong>{formatNumber(policy.outputTokensPerWindow)} tokens / {policy.windowSeconds}s</strong><div className="muted">max {formatNumber(policy.maxOutputTokensPerRequest)} / request</div></> : 'Not set'}</td><td>{policy.enabled ? 'Enabled' : 'Disabled'}</td><td className="actions"><button onClick={() => void toggleRateLimit(policy)}>{policy.enabled ? 'Disable' : 'Enable'}</button><button onClick={() => void deleteRateLimit(policy)}>Delete</button></td></tr>)}</tbody></table>
      </section>
      <section className="panel formPanel"><h2>Add rate limit</h2><form onSubmit={event => void createRateLimit(event)}>
        <label>Credential<select value={rateCredentialId} onChange={event => setRateCredentialId(event.target.value)} required><option value="">Select credential</option>{credentials.map(credential => <option key={credential.id} value={credential.id}>{credential.name}</option>)}</select></label>
        <label>Logical model<select value={rateModel} onChange={event => setRateModel(event.target.value)}><option value="">All models</option>{models.map(model => <option key={model.id} value={model.publicName}>{model.publicName}</option>)}</select></label>
        <label>Requests per window<input type="number" min="1" value={requestsPerWindow} onChange={event => setRequestsPerWindow(Number(event.target.value))} /></label>
        <label>Window seconds<input type="number" min="1" value={windowSeconds} onChange={event => setWindowSeconds(Number(event.target.value))} /></label>
        <button className="primary">Add rate limit</button>
      </form></section>
    </div>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>User quotas</h2><span>Applied across every personal API key owned by the registered Entra user.</span></div>
        <table><thead><tr><th>User</th><th>Model</th><th>Request limit</th><th>Output-token budget</th><th>Status</th><th>Actions</th></tr></thead><tbody>
          {userRateLimits.length === 0 ? <tr><td colSpan={6}>No user quotas configured.</td></tr> : userRateLimits.map(policy => <tr key={policy.id}>
            <td><strong>{policy.principalName ?? policy.ownerObjectId}</strong><div className="muted mono">{policy.ownerObjectId}</div></td>
            <td>{policy.logicalModel ?? 'All models'}</td>
            <td>{formatNumber(policy.requestsPerWindow)} / {policy.windowSeconds}s</td>
            <td>{policy.outputTokensPerWindow && policy.maxOutputTokensPerRequest ? <>{formatNumber(policy.outputTokensPerWindow)} / {policy.windowSeconds}s<div className="muted">max {formatNumber(policy.maxOutputTokensPerRequest)} / request</div></> : 'Not set'}</td>
            <td>{policy.enabled ? 'Enabled' : 'Disabled'}</td>
            <td className="actions"><button onClick={() => void toggleUserRateLimit(policy)}>{policy.enabled ? 'Disable' : 'Enable'}</button><button onClick={() => void deleteUserRateLimit(policy)}>Delete</button></td>
          </tr>)}
        </tbody></table>
      </section>
      <section className="panel formPanel"><h2>Add user quota</h2><form onSubmit={event => void createUserRateLimit(event)}>
        <label>User<select value={rateUserKey} onChange={event => setRateUserKey(event.target.value)} required><option value="">Select registered user</option>{users.map(user => <option key={`${user.tenantId}|${user.objectId}`} value={`${user.tenantId}|${user.objectId}`}>{user.displayName ?? user.principalName ?? user.objectId}</option>)}</select></label>
        <label>Logical model<select value={userRateModel} onChange={event => setUserRateModel(event.target.value)}><option value="">All models</option>{models.map(model => <option key={model.id} value={model.publicName}>{model.publicName}</option>)}</select></label>
        <label>Requests per window<input type="number" min="1" value={userRequestsPerWindow} onChange={event => setUserRequestsPerWindow(Number(event.target.value))} /></label>
        <label>Window seconds<input type="number" min="1" value={userWindowSeconds} onChange={event => setUserWindowSeconds(Number(event.target.value))} /></label>
        <label>Output tokens per window<input type="number" min="1" value={userOutputTokensPerWindow} onChange={event => setUserOutputTokensPerWindow(Number(event.target.value))} /></label>
        <label>Max output tokens per request<input type="number" min="1" max={userOutputTokensPerWindow} value={userMaxOutputTokensPerRequest} onChange={event => setUserMaxOutputTokensPerRequest(Number(event.target.value))} /></label>
        <button className="primary" disabled={users.length === 0}>Add user quota</button>
      </form>{users.length === 0 && <p className="muted">Register users in Users & Access before assigning personal quotas.</p>}</section>
    </div>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>Group quotas</h2><span>Shared across all governed personal/organization credentials currently attributed to the group.</span></div>
        <table><thead><tr><th>Group</th><th>Model</th><th>Request limit</th><th>Output-token budget</th><th>Status</th><th>Actions</th></tr></thead><tbody>
          {groupRateLimits.length === 0 ? <tr><td colSpan={6}>No group quotas configured.</td></tr> : groupRateLimits.map(policy => <tr key={policy.id}>
            <td><strong>{policy.usageGroupName ?? groupNames.get(policy.usageGroupId) ?? policy.usageGroupId}</strong></td>
            <td>{policy.logicalModel ?? 'All models'}</td>
            <td>{formatNumber(policy.requestsPerWindow)} / {policy.windowSeconds}s</td>
            <td>{policy.outputTokensPerWindow && policy.maxOutputTokensPerRequest ? <>{formatNumber(policy.outputTokensPerWindow)} / {policy.windowSeconds}s<div className="muted">max {formatNumber(policy.maxOutputTokensPerRequest)} / request</div></> : 'Not set'}</td>
            <td>{policy.enabled ? 'Enabled' : 'Disabled'}</td>
            <td className="actions"><button onClick={() => void toggleGroupRateLimit(policy)}>{policy.enabled ? 'Disable' : 'Enable'}</button><button onClick={() => void deleteGroupRateLimit(policy)}>Delete</button></td>
          </tr>)}
        </tbody></table>
      </section>
      <section className="panel formPanel"><h2>Add group quota</h2><form onSubmit={event => void createGroupRateLimit(event)}>
        <label>Group<select value={groupPolicyGroupId} onChange={event => setGroupPolicyGroupId(event.target.value)} required><option value="">Select group</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label>
        <label>Logical model<select value={groupRateModel} onChange={event => setGroupRateModel(event.target.value)}><option value="">All models</option>{models.map(model => <option key={model.id} value={model.publicName}>{model.publicName}</option>)}</select></label>
        <label>Requests per window<input type="number" min="1" value={groupRequestsPerWindow} onChange={event => setGroupRequestsPerWindow(Number(event.target.value))} /></label>
        <label>Window seconds<input type="number" min="1" value={groupWindowSeconds} onChange={event => setGroupWindowSeconds(Number(event.target.value))} /></label>
        <label>Output tokens per window<input type="number" min="1" value={groupOutputTokensPerWindow} onChange={event => setGroupOutputTokensPerWindow(Number(event.target.value))} /></label>
        <label>Max output tokens per request<input type="number" min="1" max={groupOutputTokensPerWindow} value={groupMaxOutputTokensPerRequest} onChange={event => setGroupMaxOutputTokensPerRequest(Number(event.target.value))} /></label>
        <button className="primary" disabled={groups.length === 0}>Add group quota</button>
      </form></section>
    </div>

    <section className="panel formPanel">
      <div className="panelTitle"><div><h2>Output-token budget</h2><span>Reserve before inference, then settle to actual output usage. Redis-enabled gateways enforce one shared budget.</span></div></div>
      {rateLimits.length === 0 ? <p className="muted">Create a rate-limit policy first; output-token budgets reuse the same credential/model scope and window.</p> : <form onSubmit={event => void applyOutputTokenBudget(event)}>
        <label>Budget policy<select aria-label="Budget policy" value={budgetPolicyId} onChange={event => setBudgetPolicyId(event.target.value)} required>{rateLimits.map(policy => <option key={policy.id} value={policy.id}>{policy.credentialName ?? policy.apiCredentialId} · {policy.logicalModel ?? 'All models'} · {policy.windowSeconds}s</option>)}</select></label>
        <label>Output tokens per window<input aria-label="Output tokens per window" type="number" min="1" value={outputTokensPerWindow} onChange={event => setOutputTokensPerWindow(Number(event.target.value))} /></label>
        <label>Max output tokens per request<input aria-label="Max output tokens per request" type="number" min="1" max={outputTokensPerWindow} value={maxOutputTokensPerRequest} onChange={event => setMaxOutputTokensPerRequest(Number(event.target.value))} /></label>
        <div className="actions"><button className="primary">Apply token budget</button><button type="button" className="secondary" disabled={!budgetPolicy?.outputTokensPerWindow} onClick={() => void clearOutputTokenBudget()}>Clear token budget</button></div>
      </form>}
    </section>

    <section className="panel"><div className="panelTitle"><h2>Usage by logical model</h2><span>{usage.windowDays}-day UTC window</span></div><table><thead><tr><th>Model</th><th>Requests</th><th>Total tokens</th><th>Output tokens</th><th>Errors</th><th>Rate limited</th></tr></thead><tbody>{usage.models.map(row => <tr key={row.logicalModel}><td><strong>{row.logicalModel}</strong></td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.totalTokens)}</td><td>{formatNumber(row.outputTokens)}</td><td>{formatNumber(row.errorCount)}</td><td>{formatNumber(row.rateLimitedRequests)}</td></tr>)}</tbody></table></section>

    <section className="panel"><div className="panelTitle"><h2>Usage by credential</h2><span>Gateway identity, not inferred end-user identity.</span></div><table><thead><tr><th>Credential</th><th>Group</th><th>Requests</th><th>Total tokens</th><th>Errors</th><th>Rate limited</th></tr></thead><tbody>{usage.credentials.map(row => <tr key={`${row.apiCredentialId}-${row.usageGroupId ?? 'none'}`}><td><strong>{row.name}</strong><div className="muted mono">{row.keyPrefix}</div></td><td>{row.usageGroupId ? groupNames.get(row.usageGroupId) ?? row.usageGroupId : 'Ungrouped'}</td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.totalTokens)}</td><td>{formatNumber(row.errorCount)}</td><td>{formatNumber(row.rateLimitedRequests)}</td></tr>)}</tbody></table></section>
  </>
}

function GovernanceMetric({ label, value }: { label: string; value: string }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function formatNumber(value: number) { return new Intl.NumberFormat().format(value) }
function formatLatency(value?: number | null) { return value === null || value === undefined ? '—' : `${Math.round(value)} ms` }
