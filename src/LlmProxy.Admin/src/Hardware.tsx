import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'
import type { Node, NodeHardwareMetricsSnapshot } from './types'

type Props = {
  nodes: Node[]
  hardware: NodeHardwareMetricsSnapshot[]
  refresh: () => Promise<void>
}

export default function Hardware({ nodes, hardware, refresh }: Props) {
  const [nodeId, setNodeId] = useState(nodes[0]?.id ?? '')
  const [baseAddress, setBaseAddress] = useState('')
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    if (!nodeId && nodes[0]) setNodeId(nodes[0].id)
  }, [nodeId, nodes])

  useEffect(() => {
    const selected = nodes.find(node => node.id === nodeId)
    setBaseAddress(selected?.hardwareMetricsBaseAddress ?? '')
  }, [nodeId, nodes])

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

  const available = hardware.filter(item => item.available)

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
function formatMax(values: Array<number | null | undefined>, suffix: string) {
  const numbers = values.filter((value): value is number => value !== null && value !== undefined)
  return numbers.length === 0 ? '—' : `${Math.max(...numbers).toFixed(0)}${suffix}`
}
