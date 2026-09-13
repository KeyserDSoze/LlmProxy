import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'
import type { CapacitySnapshot, Model, Node, NodeHardwareMetricsSnapshot } from './types'

type Props = {
  nodes: Node[]
  hardware: NodeHardwareMetricsSnapshot[]
  refresh: () => Promise<void>
}

const emptyCapacity: CapacitySnapshot = { nodes: [], deployments: [] }

export default function Hardware({ nodes, hardware, refresh }: Props) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? '')
  const [baseAddress, setBaseAddress] = useState('')
  const [saved, setSaved] = useState(false)
  const [capacity, setCapacity] = useState<CapacitySnapshot>(emptyCapacity)
  const [models, setModels] = useState<Model[]>([])
  const [deploymentId, setDeploymentId] = useState('')
  const [recommended, setRecommended] = useState(1)
  const [p95Ttft, setP95Ttft] = useState('')
  const [p95Duration, setP95Duration] = useState('')
  const [outputTokensPerSecond, setOutputTokensPerSecond] = useState('')
  const [benchmarkSource, setBenchmarkSource] = useState('')
  const [measuredAt, setMeasuredAt] = useState(nowForInput())
  const [capacityMessage, setCapacityMessage] = useState<string | null>(null)

  useEffect(() => {
    if (!nodeId && nodes[0]) setNodeId(nodes[0].id)
  }, [nodeId, nodes])

  useEffect(() => {
    const selected = nodes.find(node => node.id === nodeId)
    setBaseAddress(selected?.hardwareMetricsBaseAddress ?? '')
  }, [nodeId, nodes])

  useEffect(() => {
    void loadCapacity()
  }, [])

  useEffect(() => {
    const selected = capacity.deployments.find(item => item.id === deploymentId)
    if (!selected) return
    setRecommended(selected.recommendedMaxConcurrency ?? selected.effectiveMaxConcurrency)
    setP95Ttft(optionalInput(selected.benchmarkP95TtftMilliseconds))
    setP95Duration(optionalInput(selected.benchmarkP95DurationMilliseconds))
    setOutputTokensPerSecond(optionalInput(selected.sustainableOutputTokensPerSecond))
    setBenchmarkSource(selected.benchmarkSource ?? '')
    setMeasuredAt(selected.benchmarkMeasuredAtUtc ? dateForInput(selected.benchmarkMeasuredAtUtc) : nowForInput())
  }, [capacity.deployments, deploymentId])

  async function loadCapacity() {
    // The compatibility guard keeps isolated component tests from older fixtures working while
    // the real application always supplies the current API client with capacity().
    const capacityRequest = typeof api.capacity === 'function' ? api.capacity() : Promise.resolve(emptyCapacity)
    const [nextCapacity, nextModels] = await Promise.all([capacityRequest, api.models()])
    setCapacity(nextCapacity)
    setModels(nextModels)
    setDeploymentId(current => current || nextCapacity.deployments[0]?.id || '')
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!nodeId) return
    await api.updateNodeHardwareMetrics(nodeId, baseAddress.trim() || null)
    setSaved(true)
    await refresh()
  }

  async function clear() {
    if (!nodeId) return
    await api.updateNodeHardwareMetrics(nodeId, null)
    setBaseAddress('')
    setSaved(true)
    await refresh()
  }

  async function saveCapacityProfile(event: FormEvent) {
    event.preventDefault()
    if (!deploymentId) return
    await api.updateCapacityProfile(deploymentId, {
      recommendedMaxConcurrency: recommended,
      p95TtftMilliseconds: nullableNumber(p95Ttft),
      p95DurationMilliseconds: nullableNumber(p95Duration),
      sustainableOutputTokensPerSecond: nullableNumber(outputTokensPerSecond),
      benchmarkSource: benchmarkSource.trim(),
      measuredAtUtc: new Date(measuredAt).toISOString()
    })
    setCapacityMessage('Capacity recommendation saved. Active production limits were not changed.')
    await loadCapacity()
    await refresh()
  }

  async function applyCapacityProfile() {
    if (!deploymentId) return
    await api.applyCapacityProfile(deploymentId)
    setCapacityMessage('Recommended deployment capacity applied explicitly.')
    await loadCapacity()
    await refresh()
  }

  async function clearCapacityProfile() {
    if (!deploymentId) return
    await api.clearCapacityProfile(deploymentId)
    setCapacityMessage('Capacity recommendation cleared. Active production limits were not changed.')
    await loadCapacity()
    await refresh()
  }

  const available = hardware.filter(item => item.available)
  const modelNames = new Map(models.map(model => [model.id, model.publicName]))
  const nodeNames = new Map(nodes.map(node => [node.id, node.name]))
  const selectedDeployment = capacity.deployments.find(item => item.id === deploymentId)
  const selectedNode = selectedDeployment ? capacity.nodes.find(item => item.id === selectedDeployment.nodeId) : undefined
  const recommendationExceedsNode = Boolean(selectedNode && recommended > selectedNode.maxConcurrency)

  return <div className="stack">
    <section className="cards cardsFive">
      <Metric label="Configured nodes" value={nodes.filter(node => node.hardwareMetricsBaseAddress).length} />
      <Metric label="Telemetry available" value={available.length} />
      <Metric label="GPUs observed" value={available.reduce((total, item) => total + item.gpuCount, 0)} />
      <Metric label="Max GPU util" value={formatMax(available.map(item => item.maxGpuUtilizationPercent), '%')} />
      <Metric label="Max temperature" value={formatMax(available.map(item => item.maxTemperatureCelsius), ' °C')} />
    </section>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>DGX hardware telemetry</h2><span>NVIDIA/DCGM · observational only</span></div>
        <table><thead><tr><th>Node</th><th>Collector</th><th>GPU</th><th>GPU util</th><th>FB memory</th><th>Temperature</th><th>Power</th><th>Last sample</th></tr></thead><tbody>
          {nodes.map(node => {
            const item = hardware.find(snapshot => snapshot.nodeId === node.id)
            return <tr key={node.id}>
              <td><strong>{node.name}</strong><div className="muted mono">{node.hardwareMetricsBaseAddress ?? 'DCGM endpoint not configured'}</div></td>
              <td>{item ? (item.available ? 'Available' : 'Unavailable') : (node.hardwareMetricsBaseAddress ? 'Waiting' : 'Not configured')}{item?.error && <div className="muted">{item.error}</div>}</td>
              <td>{item?.available ? item.gpuCount : '—'}</td>
              <td>{item?.available ? `${formatOptional(item.averageGpuUtilizationPercent)} avg · ${formatOptional(item.maxGpuUtilizationPercent)} max` : '—'}</td>
              <td>{item?.available ? `${formatMiB(item.framebufferUsedMiB)} used · ${formatRatio(item.framebufferUsageRatio)}` : '—'}</td>
              <td>{item?.available ? formatTemperature(item.maxTemperatureCelsius) : '—'}</td>
              <td>{item?.available ? formatPower(item.totalPowerUsageWatts) : '—'}</td>
              <td>{formatDate(item?.collectedAtUtc ?? item?.lastAttemptAtUtc)}</td>
            </tr>
          })}
          {nodes.length === 0 && <tr><td colSpan={8} className="muted">No DGX nodes registered.</td></tr>}
        </tbody></table>
      </section>

      <section className="panel formPanel">
        <h2>DCGM exporter endpoint</h2>
        <p className="muted">Optional and independent from the vLLM service root. LlmProxy appends <span className="mono">/metrics</span>.</p>
        <form onSubmit={submit}>
          <label>DGX node<select aria-label="Hardware DGX node" value={nodeId} onChange={event => { setNodeId(event.target.value); setSaved(false) }} disabled={nodes.length === 0}>
            {nodes.map(node => <option key={node.id} value={node.id}>{node.name}</option>)}
          </select></label>
          <label>Hardware metrics service root<input aria-label="Hardware metrics service root" value={baseAddress} onChange={event => { setBaseAddress(event.target.value); setSaved(false) }} placeholder="http://10.0.0.21:9400" /></label>
          <div className="actions"><button className="primary" disabled={!nodeId}>Save endpoint</button><button type="button" className="secondary" disabled={!nodeId} onClick={() => void clear()}>Clear endpoint</button></div>
          {saved && <div className="notice">Hardware telemetry configuration updated.</div>}
        </form>
        <div className="secretBox hardwareHint">
          <strong>Routing isolation</strong>
          <p>DCGM availability, GPU utilization, temperature and power do not currently change node health or the routing score. vLLM queue/KV pressure remains the runtime scheduling signal until benchmark data says otherwise.</p>
        </div>
      </section>
    </div>

    <section className="panel">
      <div className="panelTitle"><h2>Physical DGX capacity</h2><span>Atomic node-wide concurrency · all deployments share the same physical ceiling</span></div>
      <table><thead><tr><th>Node</th><th>Active</th><th>Physical limit</th><th>Remaining</th><th>Behavior at saturation</th></tr></thead><tbody>
        {capacity.nodes.map(item => <tr key={item.id}>
          <td><strong>{item.name}</strong></td><td>{item.activeRequests}</td><td>{item.maxConcurrency}</td><td>{item.remaining}</td><td>HTTP 429 · Retry-After: 1</td>
        </tr>)}
        {capacity.nodes.length === 0 && <tr><td colSpan={5} className="muted">No node capacity data available.</td></tr>}
      </tbody></table>
      <p className="muted">A deployment limit can be lower than the physical DGX limit. It can never bypass the aggregate node ceiling: concurrent deployments consume the same node-wide lease pool.</p>
    </section>

    <div className="gridTwo">
      <section className="panel">
        <div className="panelTitle"><h2>Benchmark capacity profiles</h2><span>Measured recommendation ≠ active production limit</span></div>
        <table><thead><tr><th>Model / node</th><th>Active</th><th>Deployment limit</th><th>Recommended</th><th>P95 TTFT</th><th>Throughput</th><th>Evidence</th></tr></thead><tbody>
          {capacity.deployments.map(item => <tr key={item.id}>
            <td><strong>{modelNames.get(item.modelId) ?? short(item.modelId)}</strong><div className="muted">{nodeNames.get(item.nodeId) ?? short(item.nodeId)}</div></td>
            <td>{item.activeRequests}</td>
            <td>{item.effectiveMaxConcurrency}</td>
            <td>{item.recommendedMaxConcurrency ?? '—'}</td>
            <td>{formatMilliseconds(item.benchmarkP95TtftMilliseconds)}</td>
            <td>{formatTokensPerSecond(item.sustainableOutputTokensPerSecond)}</td>
            <td><span className="mono">{item.benchmarkSource ?? 'not measured'}</span><div className="muted">{formatDate(item.benchmarkMeasuredAtUtc)}</div></td>
          </tr>)}
          {capacity.deployments.length === 0 && <tr><td colSpan={7} className="muted">No deployments available for capacity profiling.</td></tr>}
        </tbody></table>
      </section>

      <section className="panel formPanel">
        <h2>Capacity recommendation</h2>
        <p className="muted">Store benchmark evidence first. Applying it to production is a separate, explicit and audited action.</p>
        <form onSubmit={saveCapacityProfile}>
          <label>Deployment<select aria-label="Capacity deployment" value={deploymentId} onChange={event => { setDeploymentId(event.target.value); setCapacityMessage(null) }} disabled={capacity.deployments.length === 0}>
            {capacity.deployments.map(item => <option key={item.id} value={item.id}>{modelNames.get(item.modelId) ?? short(item.modelId)} → {nodeNames.get(item.nodeId) ?? short(item.nodeId)}</option>)}
          </select></label>
          <label>Recommended max concurrency<input aria-label="Recommended max concurrency" type="number" min="1" value={recommended} onChange={event => { setRecommended(Number(event.target.value)); setCapacityMessage(null) }} required /></label>
          <label>P95 TTFT (ms)<input aria-label="Capacity P95 TTFT" type="number" min="0" step="0.1" value={p95Ttft} onChange={event => setP95Ttft(event.target.value)} /></label>
          <label>P95 duration (ms)<input aria-label="Capacity P95 duration" type="number" min="0" step="0.1" value={p95Duration} onChange={event => setP95Duration(event.target.value)} /></label>
          <label>Sustainable output tokens/sec<input aria-label="Capacity output tokens per second" type="number" min="0" step="0.1" value={outputTokensPerSecond} onChange={event => setOutputTokensPerSecond(event.target.value)} /></label>
          <label>Benchmark source<input aria-label="Benchmark source" value={benchmarkSource} onChange={event => setBenchmarkSource(event.target.value)} required placeholder="benchmark-results/llmproxy-benchmark-...json" /></label>
          <label>Measured at<input aria-label="Benchmark measured at" type="datetime-local" value={measuredAt} onChange={event => setMeasuredAt(event.target.value)} required /></label>
          {selectedDeployment && <div className="secretBox">
            <strong>Current limits</strong>
            <p>Deployment active limit: {selectedDeployment.effectiveMaxConcurrency}</p>
            <p>Physical node limit: {selectedNode?.maxConcurrency ?? '—'}</p>
            {recommendationExceedsNode && <p className="error">Recommendation exceeds the current physical node limit. Save is allowed as evidence, but Apply will be rejected until the node limit is explicitly increased.</p>}
          </div>}
          <div className="actions">
            <button className="primary" disabled={!deploymentId}>Save recommendation</button>
            <button type="button" className="secondary" disabled={!deploymentId || !selectedDeployment?.recommendedMaxConcurrency || recommendationExceedsNode} onClick={() => void applyCapacityProfile()}>Apply recommended</button>
            <button type="button" className="secondary" disabled={!deploymentId || !selectedDeployment?.recommendedMaxConcurrency} onClick={() => void clearCapacityProfile()}>Clear recommendation</button>
          </div>
          {capacityMessage && <div className="notice">{capacityMessage}</div>}
        </form>
      </section>
    </div>
  </div>
}

function Metric({ label, value }: { label: string; value: string | number }) { return <div className="metric"><span>{label}</span><strong>{value}</strong></div> }
function formatDate(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function formatOptional(value?: number | null) { return value === null || value === undefined ? '—' : `${value.toFixed(1)}%` }
function formatRatio(value?: number | null) { return value === null || value === undefined ? '—' : `${(value * 100).toFixed(1)}%` }
function formatMiB(value?: number | null) {
  if (value === null || value === undefined) return '—'
  return value >= 1024 ? `${(value / 1024).toFixed(1)} GiB` : `${Math.round(value)} MiB`
}
function formatTemperature(value?: number | null) { return value === null || value === undefined ? '—' : `${value.toFixed(0)} °C` }
function formatPower(value?: number | null) { return value === null || value === undefined ? '—' : `${value.toFixed(0)} W` }
function formatMilliseconds(value?: number | null) { return value === null || value === undefined ? '—' : `${Math.round(value)} ms` }
function formatTokensPerSecond(value?: number | null) { return value === null || value === undefined ? '—' : `${value.toFixed(1)} tok/s` }
function formatMax(values: Array<number | null | undefined>, suffix: string) {
  const numbers = values.filter((value): value is number => value !== null && value !== undefined)
  return numbers.length === 0 ? '—' : `${Math.max(...numbers).toFixed(0)}${suffix}`
}
function nullableNumber(value: string) { return value.trim() === '' ? null : Number(value) }
function optionalInput(value?: number | null) { return value === null || value === undefined ? '' : String(value) }
function short(value: string) { return value.length > 12 ? `${value.slice(0, 8)}…` : value }
function nowForInput() { return dateForInput(new Date().toISOString()) }
function dateForInput(value: string) {
  const date = new Date(value)
  const offset = date.getTimezoneOffset() * 60000
  return new Date(date.getTime() - offset).toISOString().slice(0, 16)
}
