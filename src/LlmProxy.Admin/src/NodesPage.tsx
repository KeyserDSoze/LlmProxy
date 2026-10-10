import { FormEvent, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Modal, Tabs } from './UiPrimitives'
import type { AgentDownloads, AgentPairingInvitation, AgentRecoverySecret, Node, NodeConnectionTest, PairedNodeStatus } from './types'

function shellQuote(value: string) { return "'" + value.replace(/'/g, "'\\''") + "'" }

export default function NodesPage({ nodes, canWrite, refresh, embedded = false }: { nodes: Node[]; canWrite: boolean; refresh: () => Promise<void>; embedded?: boolean }) {
  const [tab, setTab] = useState<'active' | 'disabled'>('active')
  const [invitation, setInvitation] = useState<AgentPairingInvitation | null>(null)
  const [agentDownloads, setAgentDownloads] = useState<AgentDownloads | null>(null)
  const [pairOpen, setPairOpen] = useState(false)
  const [recoveryNode, setRecoveryNode] = useState<Node | null>(null)
  const [recoverySecret, setRecoverySecret] = useState<AgentRecoverySecret | null>(null)
  const [recoveryVisible, setRecoveryVisible] = useState(false)
  const [pairMode, setPairMode] = useState<'outbound' | 'direct'>('outbound')
  const [directHost, setDirectHost] = useState('')
  const [pairedNodes, setPairedNodes] = useState<PairedNodeStatus[]>([])
  useEffect(() => {
    let alive = true
    const poll = () => { void api.pairedAgentNodes().then(rows => {
      if (!alive) return
      setPairedNodes(rows)
      if (rows.some(pair => !nodes.some(node => node.id === pair.nodeId))) void refresh()
    }).catch(() => {}) }
    poll()
    const timer = window.setInterval(poll, 10000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [nodes])
  const [addOpen, setAddOpen] = useState(false)
  const [credentialNode, setCredentialNode] = useState<Node | null>(null)
  const [editNode, setEditNode] = useState<Node | null>(null)
  const [editName, setEditName] = useState('')
  const [editBaseAddress, setEditBaseAddress] = useState('')
  const [editWeight, setEditWeight] = useState(1)
  const [editMaxConcurrency, setEditMaxConcurrency] = useState(4)
  const [consolidateNode, setConsolidateNode] = useState<Node | null>(null)
  const [consolidateTargetId, setConsolidateTargetId] = useState('')
  const [name, setName] = useState('')
  const [baseAddress, setBaseAddress] = useState('http://')
  const [weight, setWeight] = useState(1)
  const [maxConcurrency, setMaxConcurrency] = useState(4)
  const [upstreamBearerToken, setUpstreamBearerToken] = useState('')
  const [credentialSecret, setCredentialSecret] = useState('')
  const [connectionTests, setConnectionTests] = useState<Record<string, NodeConnectionTest>>({})
  const [testingNode, setTestingNode] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const pairingCommand = invitation
    ? `curl -fsSL ${window.location.origin}/downloads/agent/connect-node.sh | sudo LLMPROXY_GATEWAY_URL=${shellQuote(window.location.origin)} LLMPROXY_ENROLLMENT_TOKEN=${shellQuote(invitation.enrollmentToken)} LLMPROXY_CONNECTION_MODE=${pairMode}${pairMode === 'direct' && directHost.trim() ? ' LLMPROXY_ADVERTISE_HOST=' + shellQuote(directHost.trim()) : ''} bash`
    : ''

  const recoveringAgent = recoveryNode ? pairedNodes.find(pair => pair.nodeId === recoveryNode.id) : null
  const recoveryCommand = recoveryNode && recoverySecret
    ? `curl -fsSL ${window.location.origin}/downloads/agent/connect-node.sh | sudo LLMPROXY_GATEWAY_URL=${shellQuote(window.location.origin)} LLMPROXY_RECOVERY_NODE_ID=${shellQuote(recoveryNode.id)} LLMPROXY_RECOVERY_TOKEN=${shellQuote(recoverySecret.recoveryToken)} LLMPROXY_CONNECTION_MODE=${recoveringAgent?.mode ?? 'outbound'} bash`
    : ''

  function closeRecovery() {
    setRecoveryNode(null); setRecoverySecret(null); setRecoveryVisible(false)
  }

  async function revealRecovery(rotate = false) {
    if (!recoveryNode) return
    if (rotate && !window.confirm('Rotate this server recovery code? All older recovery commands for this server will stop working. The currently paired Agent remains connected.')) return
    setBusy('recovery:' + recoveryNode.id); setError(null)
    try {
      const result = rotate ? await api.rotateAgentRecovery(recoveryNode.id) : await api.revealAgentRecovery(recoveryNode.id)
      setRecoverySecret(result); setRecoveryVisible(true)
      setPairedNodes(current => current.map(item => item.nodeId === recoveryNode.id
        ? { ...item, hasRecoverySecret: true, recoverySecretCreatedAtUtc: result.recoverySecretCreatedAtUtc } : item))
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  async function updateAgent(nodeId: string) {
    setError(null); setBusy('agent-update:' + nodeId)
    try { await api.updatePairedAgent(nodeId); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  async function beginPair() {
    setError(null); setBusy('pair')
    try {
      const [invite, downloads] = await Promise.all([api.createAgentInvitation(), api.agentDownloads()])
      setInvitation(invite); setAgentDownloads(downloads); setPairOpen(true)
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) }
    finally { setBusy(null) }
  }

  const active = useMemo(() => nodes.filter(node => node.enabled), [nodes])
  const disabled = useMemo(() => nodes.filter(node => !node.enabled), [nodes])
  const visible = tab === 'active' ? active : disabled

  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy('add'); setError(null)
    try {
      await api.createNode({ name, baseAddress, weight, maxConcurrency, upstreamBearerToken: upstreamBearerToken || null })
      setName(''); setBaseAddress('http://'); setWeight(1); setMaxConcurrency(4); setUpstreamBearerToken(''); setAddOpen(false)
      await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function saveCredential(event: FormEvent) {
    event.preventDefault(); if (!credentialNode || !credentialSecret) return
    setBusy('credential'); setError(null)
    try { await api.setNodeUpstreamCredential(credentialNode.id, credentialSecret); setCredentialSecret(''); setCredentialNode(null); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  function openEdit(node: Node) {
    setEditNode(node); setEditName(node.name); setEditBaseAddress(node.baseAddress); setEditWeight(node.weight); setEditMaxConcurrency(node.maxConcurrency); setError(null)
  }

  async function saveEdit(event: FormEvent) {
    event.preventDefault(); if (!editNode) return
    setBusy('edit'); setError(null)
    try {
      await api.updateNode(editNode.id, { name: editName, baseAddress: editBaseAddress, weight: editWeight, maxConcurrency: editMaxConcurrency })
      setEditNode(null); await refresh()
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  function openConsolidate(node: Node) {
    setConsolidateNode(node); setConsolidateTargetId(nodes.find(item => item.id !== node.id)?.id ?? ''); setError(null)
  }

  async function consolidate(event: FormEvent) {
    event.preventDefault(); if (!consolidateNode || !consolidateTargetId) return
    setBusy('consolidate'); setError(null)
    try {
      const result = await api.consolidateNode(consolidateNode.id, consolidateTargetId)
      if (result.code === 'source_still_draining') {
        setError(`Source runtime is draining (${result.activeRequests ?? 0} active). Retry Consolidate when it reaches zero.`)
      } else {
        setConsolidateNode(null); await refresh()
      }
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function clearCredential() {
    if (!credentialNode) return
    setBusy('credential'); setError(null)
    try { await api.clearNodeUpstreamCredential(credentialNode.id); setCredentialSecret(''); setCredentialNode(null); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  async function testConnection(node: Node) {
    setTestingNode(node.id); setError(null)
    try { const result = await api.testNodeConnection(node.id); setConnectionTests(current => ({ ...current, [node.id]: result })); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setTestingNode(null) }
  }

  async function deleteNode(node: Node) {
    if (!window.confirm(`Delete ${node.name}? The node must remain disabled and have no active work.`)) return
    setBusy(`delete:${node.id}`); setError(null)
    try { await api.deleteNode(node.id); await refresh() }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)) } finally { setBusy(null) }
  }

  return <div className="stack compactPage">
    {!embedded && <section className="panel pageToolbar">
      <div><h2>Inference fleet</h2><p className="muted">Active nodes stay front and center. Add, credentials and destructive actions open only when needed.</p></div>
      {canWrite && <div className="actions"><button className="primary" disabled={busy === 'pair'} onClick={() => void beginPair()}>Pair Linux agent</button><button onClick={() => setAddOpen(true)}>Add inference node</button></div>}
    </section>}

    {embedded && canWrite && <section className="panel pageToolbar"><div><h2>Hardware fleet</h2><p className="muted">Register one row per physical machine. Use deployment runtime roots when models listen on different ports.</p></div><div className="actions"><button className="primary" disabled={busy === 'pair'} onClick={() => void beginPair()}>Pair Linux agent</button><button onClick={() => setAddOpen(true)}>Add hardware node</button></div></section>}

    {error && <div className="error">{error}</div>}
    <Tabs value={tab} onChange={setTab} items={[{ value: 'active', label: 'Active nodes', count: active.length }, { value: 'disabled', label: 'Disabled nodes', count: disabled.length }]} />

    <section className="panel">
      <div className="panelTitle"><h2>{tab === 'active' ? 'Active nodes' : 'Disabled nodes'}</h2><span>{visible.length} shown</span></div>
      <div className="tableScroll"><table><thead><tr><th>Name</th><th>Status</th><th>Service root</th><th>Health</th><th>Capacity</th><th>Actions</th></tr></thead><tbody>
        {visible.map(node => <tr key={node.id}>
          <td><strong>{node.name}</strong><div className="muted">weight {node.weight} · upstream auth {node.hasUpstreamCredential ? 'configured' : 'none'}</div></td>
          <td><Status value={pairedNodes.some(pair => pair.nodeId === node.id) && node.baseAddress === 'http://127.0.0.1:1' ? 'No runtime' : node.status} />{pairedNodes.filter(pair => pair.nodeId === node.id).map(pair => {
            const fresh = pair.lastHeartbeatAtUtc && Date.now() - Date.parse(pair.lastHeartbeatAtUtc) < 30000
            return <div key={pair.nodeId} className="muted">Agent {fresh ? 'connected' : 'offline'} · {pair.mode}{pair.agentUpdateStatus ? ' · update ' + pair.agentUpdateStatus.split(':').slice(0, 3).join(' · ') : ''}{pair.mode === 'outbound' ? (pair.tunnelConnected ? ' · tunnel ready' : ' · tunnel unavailable') : ''}
              {pair.agentUpdateStatus?.split(':')[0] === 'running' && pair.agentUpdateStatus?.split(':')[2] === 'downloading' &&
                /^[0-9]{1,3}$/.test(pair.agentUpdateStatus?.split(':')[3] ?? '') && <div className="stack">
                  <progress aria-label="Node Agent update archive download progress"
                    max="100" value={Number(pair.agentUpdateStatus?.split(':')[3])} style={{width: '100%'}} />
                  <span>{pair.agentUpdateStatus?.split(':')[3]}% of Agent release archive</span>
                </div>}</div>
          })}</td><td className="mono">{node.baseAddress === 'http://127.0.0.1:1' ? 'No inference model deployed' : node.baseAddress}</td>
          <td>{node.baseAddress === 'http://127.0.0.1:1' && pairedNodes.some(pair => pair.nodeId === node.id)
            ? <div className="muted">Install and start an inference model to enable health checks.</div>
            : <><div>{formatLatency(node.lastHealthLatencyMilliseconds)} · {healthStreak(node)}</div><div className="muted">{node.lastHealthError ?? `last healthy ${formatDate(node.lastHealthyAtUtc)}`}</div></>}</td>
          <td>{node.maxConcurrency}</td>
          <td className="actions">
            <button disabled={node.baseAddress === 'http://127.0.0.1:1'} title={node.baseAddress === 'http://127.0.0.1:1' ? 'Start a model before testing inference endpoints.' : undefined}
              onClick={() => void testConnection(node)}>{testingNode === node.id ? 'Testing…' : 'Test'}</button>
            {canWrite && pairedNodes.some(pair => pair.nodeId === node.id) &&
              <button disabled={busy === 'agent-update:' + node.id} onClick={() => void updateAgent(node.id)}>
                {pairedNodes.some(pair => pair.nodeId === node.id && pair.desiredAgentVersion) ? 'Update queued' : 'Update agent'}
              </button>}
            {canWrite && pairedNodes.some(pair => pair.nodeId === node.id) &&
              <button onClick={() => { setRecoveryNode(node); setRecoverySecret(null); setRecoveryVisible(false); setError(null) }}>Agent setup / recovery</button>}
            {canWrite && <button onClick={() => openEdit(node)}>Edit</button>}
            {canWrite && <button onClick={() => { setCredentialNode(node); setCredentialSecret('') }}>Credentials</button>}
            {canWrite && nodes.length > 1 && <button onClick={() => openConsolidate(node)}>Consolidate</button>}
            {canWrite && node.enabled && node.status !== 'Draining' && <button onClick={() => void api.drainNode(node.id).then(refresh)}>Drain</button>}
            {canWrite && node.enabled && node.status !== 'Draining' && <button onClick={() => void api.disableNode(node.id).then(refresh)}>Disable</button>}
            {canWrite && (!node.enabled || node.status === 'Draining') && <button onClick={() => void api.enableNode(node.id).then(refresh)}>Enable</button>}
            {canWrite && !node.enabled && <button className="danger" disabled={busy === `delete:${node.id}`} onClick={() => void deleteNode(node)}>{busy === `delete:${node.id}` ? 'Deleting…' : 'Delete'}</button>}
          </td>
        </tr>)}
        {visible.length === 0 && <tr><td colSpan={6} className="muted">No nodes in this view.</td></tr>}
      </tbody></table></div>
      {Object.values(connectionTests).map(result => <div className="secretBox" key={result.nodeId}>
        <strong>{result.success ? '✓' : '✕'} Connection test: {result.nodeName}</strong><p className="mono">Root: {result.serviceRoot}</p><p>Health: {probeSummary(result.health)}</p><p>OpenAI models: {probeSummary(result.openAi)}</p><p className="mono">Chat: {result.chatCompletionsUrl}</p><p className="mono">Responses: {result.responsesUrl}</p>
      </div>)}
    </section>

    <Modal open={pairOpen} title="Pair a Linux server" description="Install the Agent once and it registers automatically. Outbound mode needs no incoming WAN ports." onClose={() => { setPairOpen(false); setInvitation(null) }}>
      <div className="stack">
        <label>Connection mode<select aria-label="Agent connection mode" value={pairMode} onChange={event => setPairMode(event.target.value as 'outbound' | 'direct')}>
          <option value="outbound">Outbound WSS tunnel (NAT or another network)</option>
          <option value="direct">Direct management (private reachable network)</option>
        </select></label>
        {pairMode === 'direct' && <label>Private reachable IP/hostname (optional)<input aria-label="Direct agent hostname" value={directHost} onChange={event => setDirectHost(event.target.value)} placeholder="10.0.0.21" /></label>}
        <p className="muted">On the Linux server, run this command once as an administrator. The installer verifies the immutable release checksum, starts systemd and enrolls automatically.</p>
        {window.location.protocol !== 'https:' && window.location.hostname !== 'localhost' && window.location.hostname !== '127.0.0.1' &&
          <p className="error">Remote enrollment needs HTTPS. Access the Admin through its public HTTPS domain before copying the command.</p>}
        {agentDownloads && <section className="stack">
          <strong>Agent release {agentDownloads.version} — download from LLMProxy Admin</strong>
          <div className="actions">
            <a className="buttonLink" href={agentDownloads.bootstrap}>Download pairing installer</a>
            <a className="buttonLink" href={agentDownloads.x64}>Download Agent Linux x64</a>
            <a className="buttonLink" href={agentDownloads.arm64}>Download Agent Linux ARM64</a>
          </div>
          <p className="muted">Download publicly accessible on this LLMProxy domain. No login or pairing code is needed to obtain the installer or archives; the time-limited invitation is required only to register a node.</p>
          <p className="mono" style={{ overflowWrap: 'anywhere' }}>curl -fLsS {window.location.origin}{agentDownloads.x64} -o llmproxy-node-agent-linux-x64.tar.gz</p>
          <p className="mono" style={{ overflowWrap: 'anywhere' }}>curl -fLsS {window.location.origin}{agentDownloads.arm64} -o llmproxy-node-agent-linux-arm64.tar.gz</p>
        <p className="muted">Archive SHA-256:
            {' '}<a href={agentDownloads.x64Checksum}>x64</a>
            {' · '}<a href={agentDownloads.arm64Checksum}>ARM64</a>
            {' · '}<a href={agentDownloads.bootstrapChecksum}>installer</a>.
            Downloads are immutable release files. The pairing token is not embedded in the public archive.
          </p>
        </section>}
        <pre className="mono" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{pairingCommand}</pre>
        <button type="button" onClick={() => void navigator.clipboard.writeText(pairingCommand)}>Copy installation command</button>
        <p className="muted">Invitation expires {invitation ? new Date(invitation.expiresAtUtc).toLocaleString() : 'soon'} and is valid for one registration only.</p>
        <p className="muted">{pairMode === 'outbound' ? 'HTTPS/WSS egress only; reverse relay can carry inference SSE.' : 'The gateway must be able to reach the server on port 9900; use a private network or VPN.'}</p>
        <div className="modalActions"><button className="secondary" type="button" onClick={() => { setPairOpen(false); setInvitation(null) }}>Close</button></div>
      </div>
    </Modal>
    <Modal open={Boolean(recoveryNode)} title={`Agent management · ${recoveryNode?.name ?? ''}`}
      description="Persistent, server-specific recovery of the existing hardware identity. No duplicate node, model-cache deletion or new pairing invitation."
      onClose={closeRecovery}>
      <div className="stack">
        <div><strong>Registered server</strong><p className="mono">{recoveryNode?.id}</p></div>
        <p>Agent: {recoveringAgent?.agentVersion ?? 'unknown'} · {recoveringAgent?.mode ?? 'outbound'} ·
          {recoveringAgent?.tunnelConnected ? ' tunnel connected' : ' tunnel disconnected'} ·
          last heartbeat {formatDate(recoveringAgent?.lastHeartbeatAtUtc)}</p>
        <p className="muted">Recovery credential: {recoveringAgent?.hasRecoverySecret ? 'created' : 'not created yet'}.
          {recoveringAgent?.recoverySecretCreatedAtUtc ? ' Last rotated ' + formatDate(recoveringAgent.recoverySecretCreatedAtUtc) : ''}
          The credential is unique to this physical node and survives updates. Revealing or rotating it is audited.</p>
        <div className="actions">
          <button type="button" disabled={busy !== null} onClick={() => void revealRecovery()}>
            {recoveringAgent?.hasRecoverySecret ? 'Reveal recovery code' : 'Create recovery code'}
          </button>
          <button type="button" className="secondary" disabled={busy !== null} onClick={() => void revealRecovery(true)}>
            Rotate recovery code
          </button>
        </div>
        {recoverySecret && <>
          <label>Recovery code (admin only)
            <input readOnly type={recoveryVisible ? 'text' : 'password'} value={recoverySecret.recoveryToken} autoComplete="off" />
          </label>
          <div className="actions">
            <button type="button" className="secondary" onClick={() => setRecoveryVisible(current => !current)}>{recoveryVisible ? 'Hide code' : 'Show code'}</button>
            <button type="button" className="secondary" onClick={() => void navigator.clipboard.writeText(recoverySecret.recoveryToken)}>Copy code</button>
          </div>
          <strong>Repair or reinstall this Agent (existing node)</strong>
          <p className="muted">Run on the registered Linux machine. Downloads the latest checksummed public Agent, preserves its model data and restores this exact node ID. Re-running with the same recovery key does not create another node. Stop active inference deployments before a full reinstall.</p>
          <pre className="mono" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{recoveryCommand}</pre>
          <button type="button" onClick={() => void navigator.clipboard.writeText(recoveryCommand)}>Copy repair / reinstall command</button>
          <p className="muted">Keep this command private: it contains a persistent server recovery secret. It is not included in public downloads, and can be revoked by rotating the key here.</p>
        </>}
        <p className="muted">For normal updates while online, use “Update agent” on the hardware row. For offline or lost pairing, use the recovery command above; it does not rely on an expired 30-minute invitation.</p>
        <div className="modalActions"><button className="secondary" type="button" onClick={closeRecovery}>Close</button></div>
      </div>
    </Modal>
    <Modal open={addOpen} title="Add inference node" description="Register the inference service root and optional provider bearer. The bearer remains write-only." onClose={() => setAddOpen(false)}>
      <form className="formPanel" onSubmit={submit}>
        <label>Name<input value={name} onChange={event => setName(event.target.value)} required placeholder="inference-02" /></label>
        <label>Base address / service root<input value={baseAddress} onChange={event => setBaseAddress(event.target.value)} required placeholder="http://10.0.0.12:8000/vllm" /></label>
        <div className="formGridTwo"><label>Weight<input type="number" min="1" value={weight} onChange={event => setWeight(Number(event.target.value))} /></label><label>Max concurrency<input type="number" min="1" value={maxConcurrency} onChange={event => setMaxConcurrency(Number(event.target.value))} /></label></div>
        <label>Upstream bearer token (optional)<input type="password" autoComplete="new-password" value={upstreamBearerToken} onChange={event => setUpstreamBearerToken(event.target.value)} placeholder="provider token" /></label>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setAddOpen(false)}>Cancel</button><button className="primary" disabled={busy === 'add'}>{busy === 'add' ? 'Adding…' : 'Add node'}</button></div>
      </form>
    </Modal>

    <Modal open={Boolean(editNode)} title={`Hardware settings · ${editNode?.name ?? ''}`} description="Physical concurrency is the maximum simultaneous inference requests admitted across every deployment on this machine. It is not a user count." onClose={() => setEditNode(null)}>
      <form className="formPanel" onSubmit={saveEdit}>
        <label>Name<input value={editName} onChange={event => setEditName(event.target.value)} required /></label>
        <label>Default runtime service root<input value={editBaseAddress} onChange={event => setEditBaseAddress(event.target.value)} required /></label>
        <div className="formGridTwo"><label>Routing weight<input type="number" min="1" value={editWeight} onChange={event => setEditWeight(Number(event.target.value))} /></label><label>Physical max concurrent requests<input aria-label="Physical max concurrent requests" type="number" min="1" value={editMaxConcurrency} onChange={event => setEditMaxConcurrency(Number(event.target.value))} /></label></div>
        <div className="notice">Example: 30 authorized people belongs in Users & Access. A value of 10 here means this hardware accepts at most 10 simultaneous inference requests across all its models.</div>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setEditNode(null)}>Cancel</button><button className="primary" disabled={busy === 'edit'}>{busy === 'edit' ? 'Saving…' : 'Save hardware'}</button></div>
      </form>
    </Modal>

    <Modal open={Boolean(consolidateNode)} title={`Consolidate runtime · ${consolidateNode?.name ?? ''}`} description="Use this when a second row is really another runtime/port on the same physical machine. LlmProxy drains it, moves its deployments, preserves its runtime root and bearer, then removes the duplicate hardware row." onClose={() => setConsolidateNode(null)}>
      <form className="formPanel" onSubmit={consolidate}>
        <label>Physical hardware<select value={consolidateTargetId} onChange={event => setConsolidateTargetId(event.target.value)} required><option value="">Select target hardware</option>{nodes.filter(item => item.id !== consolidateNode?.id).map(item => <option key={item.id} value={item.id}>{item.name} · max {item.maxConcurrency} concurrent</option>)}</select></label>
        <div className="notice">After consolidation, the target hardware's physical limit is shared. A source deployment that previously inherited its node limit keeps that old value as its deployment-specific ceiling.</div>
        <div className="modalActions"><button type="button" className="secondary" onClick={() => setConsolidateNode(null)}>Cancel</button><button className="primary" disabled={!consolidateTargetId || busy === 'consolidate'}>{busy === 'consolidate' ? 'Draining…' : 'Consolidate safely'}</button></div>
      </form>
    </Modal>

    <Modal open={Boolean(credentialNode)} title={`Upstream authentication · ${credentialNode?.name ?? ''}`} description="Provider credentials are encrypted at rest and are never returned by the API." onClose={() => setCredentialNode(null)}>
      <form className="formPanel" onSubmit={saveCredential}>
        <label>New bearer token<input type="password" autoComplete="new-password" value={credentialSecret} onChange={event => setCredentialSecret(event.target.value)} placeholder="write-only secret" /></label>
        <div className="modalActions"><button type="button" className="danger" disabled={!credentialNode?.hasUpstreamCredential || busy === 'credential'} onClick={() => void clearCredential()}>Clear bearer</button><button className="primary" disabled={!credentialSecret || busy === 'credential'}>Set / rotate bearer</button></div>
      </form>
    </Modal>
  </div>
}

function Status({ value }: { value: string }) { return <span className={`status status-${value.toLowerCase().replace(/\s+/g, '-')}`}><i />{value}</span> }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function formatLatency(value?: number | null) { return value === null || value === undefined ? '—' : `${value} ms` }
function healthStreak(node: Node) { return node.consecutiveHealthFailures > 0 ? `${node.consecutiveHealthFailures} fail` : `${node.consecutiveHealthSuccesses} ok` }
function probeSummary(probe: NodeConnectionTest['health']) { return probe.success ? `✓ HTTP ${probe.statusCode} in ${probe.latencyMilliseconds} ms` : `✕ ${probe.error ?? `HTTP ${probe.statusCode}`} (${probe.latencyMilliseconds} ms)` }
