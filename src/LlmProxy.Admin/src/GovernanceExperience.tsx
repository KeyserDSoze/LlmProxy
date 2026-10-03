import { FormEvent, useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Modal, Tabs } from './UiPrimitives'
import type { CreatedApiCredential, GovernanceCredential, Model, PlatformUser, RateLimitPolicy, UsageGroup, UsageGroupRateLimitPolicy, UsageReport, UserRateLimitPolicy, UserUsageSummary } from './types'

const emptyUsage: UsageReport = { windowDays: 30, sinceUtc: '', windowGranularity: 'utc_day', rawRetentionDays: 90, rollupRetentionDays: 730, rawRequestCount: 0, rolledUpRequestCount: 0, historicalRollupsUsed: false, requestCount: 0, errorCount: 0, inputTokens: 0, outputTokens: 0, totalTokens: 0, rateLimitedRequests: 0, capacityExhaustedRequests: 0, groups: [], credentials: [], models: [] }
type ModalKind = 'group' | 'credential' | 'user' | 'usage-group' | null

export default function GovernanceExperience() {
  const [tab, setTab] = useState<'usage' | 'groups' | 'quotas'>('usage')
  const [modal, setModal] = useState<ModalKind>(null)
  const [days, setDays] = useState(30)
  const [groups, setGroups] = useState<UsageGroup[]>([])
  const [credentials, setCredentials] = useState<GovernanceCredential[]>([])
  const [policies, setPolicies] = useState<RateLimitPolicy[]>([])
  const [users, setUsers] = useState<PlatformUser[]>([])
  const [userPolicies, setUserPolicies] = useState<UserRateLimitPolicy[]>([])
  const [groupPolicies, setGroupPolicies] = useState<UsageGroupRateLimitPolicy[]>([])
  const [userUsage, setUserUsage] = useState<UserUsageSummary[]>([])
  const [models, setModels] = useState<Model[]>([])
  const [usage, setUsage] = useState<UsageReport>(emptyUsage)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const [groupName, setGroupName] = useState('')
  const [groupDescription, setGroupDescription] = useState('')
  const [credentialId, setCredentialId] = useState('')
  const [userKey, setUserKey] = useState('')
  const [usageGroupId, setUsageGroupId] = useState('')
  const [logicalModel, setLogicalModel] = useState('')
  const [requests, setRequests] = useState(60)
  const [seconds, setSeconds] = useState(60)
  const [tokens, setTokens] = useState(100000)
  const [maxTokens, setMaxTokens] = useState(4096)
  const [rotated, setRotated] = useState<CreatedApiCredential | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      const [nextGroups, nextCredentials, nextPolicies, nextUsers, nextUserPolicies, nextGroupPolicies, nextUsage, nextUserUsage, nextModels] = await Promise.all([
        api.usageGroups(), api.governanceCredentials(), api.rateLimits(), api.platformUsers(), api.userRateLimits(), api.groupRateLimits(), api.usageSummary(days), api.usageUsers(days), api.models()
      ])
      setGroups(nextGroups); setCredentials(nextCredentials); setPolicies(nextPolicies); setUsers(nextUsers); setUserPolicies(nextUserPolicies); setGroupPolicies(nextGroupPolicies); setUsage(nextUsage); setUserUsage(nextUserUsage); setModels(nextModels)
      setCredentialId(current => current || nextCredentials[0]?.id || '')
      setUserKey(current => current || (nextUsers[0] ? nextUsers[0].tenantId + '|' + nextUsers[0].objectId : ''))
      setUsageGroupId(current => current || nextGroups[0]?.id || '')
      setError(null)
    } catch (reason) {
      const value = reason instanceof Error ? reason.message : String(reason)
      if (value === 'AUTH_REQUIRED') window.location.assign('/auth/login')
      else if (value === 'FORBIDDEN') setError('Access denied. Your Entra account does not have the required LlmProxy administrative role.')
      else setError(value)
    } finally { setLoading(false) }
  }, [days])

  useEffect(() => { void refresh() }, [refresh])
  const groupNames = useMemo(() => new Map(groups.map(group => [group.id, group.name])), [groups])

  function resetQuota() { setLogicalModel(''); setRequests(60); setSeconds(60); setTokens(100000); setMaxTokens(4096) }
  async function createGroup(event: FormEvent) { event.preventDefault(); await api.createUsageGroup({ name: groupName, description: groupDescription || null }); setGroupName(''); setGroupDescription(''); setModal(null); setMessage('Usage group created.'); await refresh() }
  async function assignCredential(id: string, groupId: string) { if (groupId) await api.assignCredentialUsageGroup(id, groupId); else await api.clearCredentialUsageGroup(id); setMessage('Credential group updated.'); await refresh() }
  async function toggleCredential(item: GovernanceCredential) { if (item.kind === 'personal') return; await api.updateCredentialCallerGovernance(item.id, !item.enforceCallerGovernance); setMessage(item.enforceCallerGovernance ? 'Organization credential exempted from caller governance.' : 'Organization credential opted into caller governance.'); await refresh() }
  async function rotate(item: GovernanceCredential) { try { setRotated(await api.rotateApiCredential(item.id)); setMessage('Credential rotated. The previous secret is invalid.'); await refresh() } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } }

  async function createCredentialPolicy(event: FormEvent) {
    event.preventDefault(); if (!credentialId) return
    await api.createRateLimit({ apiCredentialId: credentialId, logicalModel: logicalModel || null, requestsPerWindow: requests, windowSeconds: seconds, enabled: true, outputTokensPerWindow: tokens, maxOutputTokensPerRequest: maxTokens })
    setModal(null); resetQuota(); setMessage('Credential quota created and applied live.'); await refresh()
  }
  async function createUserPolicy(event: FormEvent) {
    event.preventDefault(); const separator = userKey.indexOf('|'); if (separator < 1) return
    await api.createUserRateLimit({ ownerTenantId: userKey.slice(0, separator), ownerObjectId: userKey.slice(separator + 1), logicalModel: logicalModel || null, requestsPerWindow: requests, windowSeconds: seconds, enabled: true, outputTokensPerWindow: tokens, maxOutputTokensPerRequest: maxTokens })
    setModal(null); resetQuota(); setMessage('User quota created and applied live.'); await refresh()
  }
  async function createGroupPolicy(event: FormEvent) {
    event.preventDefault(); if (!usageGroupId) return
    await api.createGroupRateLimit({ usageGroupId, logicalModel: logicalModel || null, requestsPerWindow: requests, windowSeconds: seconds, enabled: true, outputTokensPerWindow: tokens, maxOutputTokensPerRequest: maxTokens })
    setModal(null); resetQuota(); setMessage('Group quota created and applied live.'); await refresh()
  }

  async function toggleRate(item: RateLimitPolicy) { await api.updateRateLimit(item.id, { logicalModel: item.logicalModel ?? null, requestsPerWindow: item.requestsPerWindow, windowSeconds: item.windowSeconds, enabled: !item.enabled, outputTokensPerWindow: item.outputTokensPerWindow, maxOutputTokensPerRequest: item.maxOutputTokensPerRequest }); await refresh() }
  async function toggleUserRate(item: UserRateLimitPolicy) { await api.updateUserRateLimit(item.id, { logicalModel: item.logicalModel ?? null, requestsPerWindow: item.requestsPerWindow, windowSeconds: item.windowSeconds, enabled: !item.enabled, outputTokensPerWindow: item.outputTokensPerWindow, maxOutputTokensPerRequest: item.maxOutputTokensPerRequest }); await refresh() }
  async function toggleGroupRate(item: UsageGroupRateLimitPolicy) { await api.updateGroupRateLimit(item.id, { logicalModel: item.logicalModel ?? null, requestsPerWindow: item.requestsPerWindow, windowSeconds: item.windowSeconds, enabled: !item.enabled, outputTokensPerWindow: item.outputTokensPerWindow, maxOutputTokensPerRequest: item.maxOutputTokensPerRequest }); await refresh() }

  if (loading) return <div className="loading">Loading usage governance…</div>

  return <div className="stack compactPage">
    {error && <div className="error">{error}</div>}{message && <div className="notice">{message}</div>}
    <section className="panel pageToolbar"><div><h2>Usage & Governance</h2><p className="muted">Usage reporting, group assignment and quota policy are separated into focused tabs. Creation flows open only when requested.</p></div><div className="actions"><select aria-label="Usage window" value={days} onChange={event => setDays(Number(event.target.value))}><option value={7}>Last 7 UTC days</option><option value={30}>Last 30 UTC days</option><option value={90}>Last 90 UTC days</option><option value={180}>Last 180 UTC days</option><option value={365}>Last 365 UTC days</option><option value={730}>Last 730 UTC days</option></select><button className="secondary" onClick={() => void refresh()}>Refresh</button></div></section>
    <Tabs value={tab} onChange={setTab} items={[{ value: 'usage', label: 'Usage' }, { value: 'groups', label: 'Groups & credentials', count: groups.length }, { value: 'quotas', label: 'Quotas', count: policies.length + userPolicies.length + groupPolicies.length }]} />

    {tab === 'usage' && <>
      <section className="cards cardsFive"><Metric label="Requests" value={usage.requestCount} /><Metric label="Total tokens" value={usage.totalTokens} /><Metric label="Output tokens" value={usage.outputTokens} /><Metric label="Rate limited" value={usage.rateLimitedRequests} /><Metric label="Capacity exhausted" value={usage.capacityExhaustedRequests} /></section>
      {usage.historicalRollupsUsed && <div className="notice" data-testid="historical-rollup-notice">Historical rollups included: {formatNumber(usage.rolledUpRequestCount)} rolled-up requests + {formatNumber(usage.rawRequestCount)} raw requests.</div>}
      <div className="gridTwo"><section className="panel"><div className="panelTitle"><h2>Usage by group</h2><span>{usage.windowDays}-day UTC window</span></div><table><thead><tr><th>Group</th><th>Requests</th><th>Tokens</th><th>Errors</th><th>Avg TTFT</th></tr></thead><tbody>{usage.groups.map(row => <tr key={row.usageGroupId ?? 'ungrouped'}><td><strong>{row.name}</strong></td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.totalTokens)}</td><td>{formatNumber(row.errorCount)}</td><td>{formatLatency(row.averageTtftMilliseconds)}</td></tr>)}</tbody></table></section><section className="panel"><div className="panelTitle"><h2>Usage by model</h2><span>Logical model</span></div><table><thead><tr><th>Model</th><th>Requests</th><th>Tokens</th><th>Errors</th></tr></thead><tbody>{usage.models.map(row => <tr key={row.logicalModel}><td><strong>{row.logicalModel}</strong></td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.totalTokens)}</td><td>{formatNumber(row.errorCount)}</td></tr>)}</tbody></table></section></div>
      <section className="panel"><div className="panelTitle"><h2>Usage by user</h2><span>Personal credentials resolved through stable Entra identity</span></div><table><thead><tr><th>User</th><th>Group</th><th>Requests</th><th>Tokens</th><th>Errors</th><th>Rate limited</th></tr></thead><tbody>{userUsage.map(row => <tr key={row.tenantId + '|' + row.objectId}><td><strong>{row.displayName ?? row.principalName ?? row.objectId}</strong></td><td>{row.usageGroupId ? groupNames.get(row.usageGroupId) ?? row.usageGroupId : 'Ungrouped'}</td><td>{formatNumber(row.requestCount)}</td><td>{formatNumber(row.totalTokens)}</td><td>{formatNumber(row.errorCount)}</td><td>{formatNumber(row.rateLimitedRequests)}</td></tr>)}</tbody></table></section>
    </>}

    {tab === 'groups' && <>
      <section className="panel"><div className="panelTitle"><div><h2>User / usage groups</h2><span>Grouping is governance and accounting; it does not own credentials.</span></div><button className="primary" onClick={() => setModal('group')}>Create usage group</button></div><table><thead><tr><th>Name</th><th>Description</th><th>Users</th><th>Credentials</th></tr></thead><tbody>{groups.map(group => <tr key={group.id}><td><strong>{group.name}</strong></td><td>{group.description ?? '—'}</td><td>{group.userCount}</td><td>{group.credentialCount}</td></tr>)}</tbody></table></section>
      <section className="panel"><div className="panelTitle"><h2>Credential governance</h2><span>Personal and organization scope stay visible.</span></div><div className="tableScroll"><table><thead><tr><th>Credential</th><th>Scope</th><th>Prefix</th><th>Usage group</th><th>Caller governance</th><th>Action</th></tr></thead><tbody>{credentials.map(item => <tr key={item.id}><td><strong>{item.name}</strong>{item.ownerPrincipalName && <div className="muted">{item.ownerPrincipalName}</div>}</td><td><span className={'scopeBadge ' + (item.kind === 'personal' ? 'scope-personal' : 'scope-org')}>{item.kind === 'personal' ? 'PERSONAL' : 'ORG'}</span></td><td className="mono">{item.keyPrefix}</td><td><select aria-label={'Usage group for ' + item.name} value={item.usageGroupId ?? ''} onChange={event => void assignCredential(item.id, event.target.value)}><option value="">Ungrouped</option>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></td><td>{item.kind === 'personal' ? 'Always governed' : <label className="inlineToggle"><input type="checkbox" checked={Boolean(item.enforceCallerGovernance)} onChange={() => void toggleCredential(item)} /> Treat as governed client</label>}</td><td>{item.enabled && <button onClick={() => void rotate(item)}>Rotate</button>}</td></tr>)}</tbody></table></div>{rotated && <div className="secretBox" data-testid="rotated-credential-secret"><strong>Copy the rotated key</strong><code>{rotated.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(rotated.secret)}>Copy</button></div>}</section>
    </>}

    {tab === 'quotas' && <>
      <section className="panel"><div className="panelTitle"><div><h2>Credential quotas</h2><span>Request and output-token budgets.</span></div><button className="primary" onClick={() => setModal('credential')}>Add credential limit</button></div><QuotaTable rows={policies.map(item => ({ id: item.id, name: item.credentialName ?? item.apiCredentialId, model: item.logicalModel, requests: item.requestsPerWindow, seconds: item.windowSeconds, tokens: item.outputTokensPerWindow, enabled: item.enabled, toggle: () => toggleRate(item), remove: () => api.deleteRateLimit(item.id).then(refresh) }))} /></section>
      <section className="panel"><div className="panelTitle"><div><h2>User quotas</h2><span>Applied across all personal keys owned by the user.</span></div><button className="primary" onClick={() => setModal('user')}>Add user quota</button></div><QuotaTable rows={userPolicies.map(item => ({ id: item.id, name: item.principalName ?? item.ownerObjectId, model: item.logicalModel, requests: item.requestsPerWindow, seconds: item.windowSeconds, tokens: item.outputTokensPerWindow, enabled: item.enabled, toggle: () => toggleUserRate(item), remove: () => api.deleteUserRateLimit(item.id).then(refresh) }))} /></section>
      <section className="panel"><div className="panelTitle"><div><h2>Group quotas</h2><span>Shared by governed callers attributed to the group.</span></div><button className="primary" onClick={() => setModal('usage-group')}>Add group quota</button></div><QuotaTable rows={groupPolicies.map(item => ({ id: item.id, name: item.usageGroupName ?? groupNames.get(item.usageGroupId) ?? item.usageGroupId, model: item.logicalModel, requests: item.requestsPerWindow, seconds: item.windowSeconds, tokens: item.outputTokensPerWindow, enabled: item.enabled, toggle: () => toggleGroupRate(item), remove: () => api.deleteGroupRateLimit(item.id).then(refresh) }))} /></section>
    </>}

    <Modal open={modal === 'group'} title="Create usage group" onClose={() => setModal(null)}><form className="formPanel" onSubmit={createGroup}><label>Name<input value={groupName} onChange={event => setGroupName(event.target.value)} required placeholder="Development CRM" /></label><label>Description<input value={groupDescription} onChange={event => setGroupDescription(event.target.value)} placeholder="Copilot usage for the CRM team" /></label><div className="modalActions"><button type="button" className="secondary" onClick={() => setModal(null)}>Cancel</button><button className="primary">Create group</button></div></form></Modal>
    <Modal open={modal === 'credential'} title="Add credential rate limit" onClose={() => setModal(null)}><form className="formPanel" onSubmit={createCredentialPolicy}><label>Credential<select value={credentialId} onChange={event => setCredentialId(event.target.value)} required>{credentials.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label><QuotaFields models={models} logicalModel={logicalModel} setLogicalModel={setLogicalModel} requests={requests} setRequests={setRequests} seconds={seconds} setSeconds={setSeconds} tokens={tokens} setTokens={setTokens} maxTokens={maxTokens} setMaxTokens={setMaxTokens} /><button className="primary">Add rate limit</button></form></Modal>
    <Modal open={modal === 'user'} title="Add user quota" onClose={() => setModal(null)}><form className="formPanel" onSubmit={createUserPolicy}><label>User<select value={userKey} onChange={event => setUserKey(event.target.value)} required>{users.map(user => <option key={user.tenantId + '|' + user.objectId} value={user.tenantId + '|' + user.objectId}>{user.displayName ?? user.principalName ?? user.objectId}</option>)}</select></label><QuotaFields models={models} logicalModel={logicalModel} setLogicalModel={setLogicalModel} requests={requests} setRequests={setRequests} seconds={seconds} setSeconds={setSeconds} tokens={tokens} setTokens={setTokens} maxTokens={maxTokens} setMaxTokens={setMaxTokens} /><button className="primary">Add user quota</button></form></Modal>
    <Modal open={modal === 'usage-group'} title="Add group quota" onClose={() => setModal(null)}><form className="formPanel" onSubmit={createGroupPolicy}><label>Group<select value={usageGroupId} onChange={event => setUsageGroupId(event.target.value)} required>{groups.map(group => <option key={group.id} value={group.id}>{group.name}</option>)}</select></label><QuotaFields models={models} logicalModel={logicalModel} setLogicalModel={setLogicalModel} requests={requests} setRequests={setRequests} seconds={seconds} setSeconds={setSeconds} tokens={tokens} setTokens={setTokens} maxTokens={maxTokens} setMaxTokens={setMaxTokens} /><button className="primary">Add group quota</button></form></Modal>
  </div>
}

function Metric({ label, value }: { label: string; value: number }) { return <div className="metric"><span>{label}</span><strong>{formatNumber(value)}</strong></div> }
function QuotaFields({ models, logicalModel, setLogicalModel, requests, setRequests, seconds, setSeconds, tokens, setTokens, maxTokens, setMaxTokens }: { models: Model[]; logicalModel: string; setLogicalModel: (value: string) => void; requests: number; setRequests: (value: number) => void; seconds: number; setSeconds: (value: number) => void; tokens: number; setTokens: (value: number) => void; maxTokens: number; setMaxTokens: (value: number) => void }) { return <><label>Logical model<select value={logicalModel} onChange={event => setLogicalModel(event.target.value)}><option value="">All models</option>{models.map(model => <option key={model.id} value={model.publicName}>{model.publicName}</option>)}</select></label><div className="formGridTwo"><label>Requests per window<input type="number" min="1" value={requests} onChange={event => setRequests(Number(event.target.value))} /></label><label>Window seconds<input type="number" min="1" value={seconds} onChange={event => setSeconds(Number(event.target.value))} /></label><label>Output tokens per window<input type="number" min="1" value={tokens} onChange={event => setTokens(Number(event.target.value))} /></label><label>Max output tokens per request<input type="number" min="1" max={tokens} value={maxTokens} onChange={event => setMaxTokens(Number(event.target.value))} /></label></div></> }
function QuotaTable({ rows }: { rows: Array<{ id: string; name: string; model?: string | null; requests: number; seconds: number; tokens?: number | null; enabled: boolean; toggle: () => Promise<void>; remove: () => Promise<unknown> }> }) { return <div className="tableScroll"><table><thead><tr><th>Scope</th><th>Model</th><th>Request limit</th><th>Output tokens</th><th>Status</th><th>Actions</th></tr></thead><tbody>{rows.map(row => <tr key={row.id}><td><strong>{row.name}</strong></td><td>{row.model ?? 'All models'}</td><td>{formatNumber(row.requests)} / {row.seconds}s</td><td>{row.tokens ? formatNumber(row.tokens) : 'Not set'}</td><td>{row.enabled ? 'Enabled' : 'Disabled'}</td><td className="actions"><button onClick={() => void row.toggle()}>{row.enabled ? 'Disable' : 'Enable'}</button><button onClick={() => void row.remove()}>Delete</button></td></tr>)}</tbody></table></div> }
function formatNumber(value: number) { return new Intl.NumberFormat().format(value) }
function formatLatency(value?: number | null) { return value === null || value === undefined ? '—' : Math.round(value) + ' ms' }
