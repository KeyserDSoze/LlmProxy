import { FormEvent, useState } from 'react'
import { api } from './api'
import { Modal } from './UiPrimitives'
import type { ApiCredential, CreatedApiCredential } from './types'

export default function CredentialsPage({ credentials, canWrite, refresh }: { credentials: ApiCredential[]; canWrite: boolean; refresh: () => Promise<void> }) {
  const [createOpen, setCreateOpen] = useState(false)
  const [name, setName] = useState('GitHub Copilot')
  const [created, setCreated] = useState<CreatedApiCredential | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy('create'); setError(null)
    try { const result = await api.createApiCredential({ name }); setCreated(result); setCreateOpen(false); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }
  async function reveal(item: ApiCredential) {
    setBusy(`reveal:${item.id}`); setError(null)
    try { const result = await api.revealApiCredential(item.id); setCreated({ id: result.id, name: result.name, keyPrefix: result.keyPrefix, secret: result.secret, enabled: true, createdAtUtc: '', secretAvailable: true }) }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }
  async function rotate(item: ApiCredential) {
    setBusy(`rotate:${item.id}`); setError(null)
    try { setCreated(await api.rotateApiCredential(item.id)); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  return <div className="stack compactPage">
    <section className="panel pageToolbar">
      <div><h2>Credential inventory</h2><p className="muted">Credential scope is explicit: organization keys are shared workloads, personal keys belong to one Entra identity, and Usage Groups add governance without owning the key.</p></div>
      {canWrite && <button className="primary" onClick={() => setCreateOpen(true)}>Create organization key</button>}
    </section>
    <section className="scopeLegend"><div><span className="scopeBadge scope-org">ORG</span><strong> Organization</strong><p>Created by administrators for shared integrations. Administrators can reveal recoverable secrets, rotate and revoke.</p></div><div><span className="scopeBadge scope-personal">PERSONAL</span><strong> Personal</strong><p>Owned by a stable Entra user identity. Users manage their own keys in <a href="/admin/me">My dashboard</a>.</p></div><div><span className="scopeBadge">GROUP</span><strong> Usage Group</strong><p>A governance/accounting assignment. It does not change who owns the API key.</p></div></section>
    {error && <div className="error">{error}</div>}
    <section className="panel"><div className="panelTitle"><h2>Credentials</h2><span>{credentials.length} visible to the administrator</span></div><div className="tableScroll"><table><thead><tr><th>Name</th><th>Scope</th><th>Prefix</th><th>State</th><th>Created</th><th>Last used</th><th>Secret</th><th>Actions</th></tr></thead><tbody>
      {credentials.map(item => { const personal = item.kind === 'personal'; return <tr key={item.id}><td><strong>{item.name}</strong>{item.ownerPrincipalName && <div className="muted">{item.ownerPrincipalName}</div>}</td><td><span className={`scopeBadge ${personal ? 'scope-personal' : 'scope-org'}`}>{personal ? 'PERSONAL' : 'ORG'}</span></td><td className="mono"><span className="muted">{personal ? 'personal · ' : 'org · '}</span>{item.keyPrefix}…</td><td>{item.enabled ? 'Enabled' : 'Revoked'}</td><td>{formatDate(item.createdAtUtc)}</td><td>{formatDate(item.lastUsedAtUtc)}</td><td>{item.secretAvailable ? 'Recoverable by admin' : 'Rotate to enable recovery'}</td><td className="actions">{canWrite && item.secretAvailable && <button disabled={busy === `reveal:${item.id}`} onClick={() => void reveal(item)}>Reveal / copy</button>}{canWrite && item.enabled && <button disabled={busy === `rotate:${item.id}`} onClick={() => void rotate(item)}>Rotate</button>}{canWrite && item.enabled && <button onClick={() => void api.revokeApiCredential(item.id).then(refresh)}>Revoke</button>}</td></tr> })}
      {credentials.length === 0 && <tr><td colSpan={8} className="muted">No credentials created yet.</td></tr>}
    </tbody></table></div></section>
    {created && <section className="secretBox"><strong>{created.name} · secret available to administrators</strong><p>Reveal/rotation is audited. Copy this value only to the intended client.</p><code>{created.secret}</code><button className="secondary" onClick={() => void navigator.clipboard.writeText(created.secret)}>Copy</button><button className="secondary" onClick={() => setCreated(null)}>Close</button></section>}
    <Modal open={createOpen} title="Create organization credential" description="Shared/workload key. Caller governance is off by default unless an administrator opts it in under Usage & Governance." onClose={() => setCreateOpen(false)}><form className="formPanel" onSubmit={submit}><label>Name<input value={name} onChange={event => setName(event.target.value)} required placeholder="GitHub Copilot Production" /></label><div className="modalActions"><button type="button" className="secondary" onClick={() => setCreateOpen(false)}>Cancel</button><button className="primary" disabled={busy === 'create'}>Generate organization API key</button></div></form></Modal>
  </div>
}
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
