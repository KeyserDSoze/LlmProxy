import type { ApiCredential, AuditEvent, CreatedApiCredential, Deployment, DeploymentPerformanceSnapshot, MetricsSummary, Model, Node, NodeConnectionTest, NodeHardwareMetricsSnapshot, NodeRuntimeMetricsSnapshot, Overview, RequestMetric, RoutingSettings, RoutingTuningSettings } from './types'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: init?.body ? { 'Content-Type': 'application/json', ...(init.headers ?? {}) } : init?.headers,
    ...init
  })

  if (response.status === 401 || response.status === 403) {
    throw new Error('AUTH_REQUIRED')
  }

  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `${response.status} ${response.statusText}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export const api = {
  overview: () => request<Overview>('/api/admin/overview'),
  routing: () => request<RoutingSettings>('/api/admin/routing'),
  routingTuning: () => request<RoutingTuningSettings>('/api/admin/routing/tuning'),
  routingPerformance: () => request<DeploymentPerformanceSnapshot[]>('/api/admin/routing/performance'),
  routingRuntime: () => request<NodeRuntimeMetricsSnapshot[]>('/api/admin/routing/runtime'),
  hardware: () => request<NodeHardwareMetricsSnapshot[]>('/api/admin/hardware'),
  updateRouting: (strategy: RoutingSettings['strategy']) =>
    request<RoutingSettings>('/api/admin/routing', { method: 'PUT', body: JSON.stringify({ strategy }) }),
  updateRoutingTuning: (settings: Omit<RoutingTuningSettings, 'updatedAtUtc'>) =>
    request<RoutingTuningSettings>('/api/admin/routing/tuning', { method: 'PUT', body: JSON.stringify(settings) }),
  nodes: () => request<Node[]>('/api/admin/nodes'),
  models: () => request<Model[]>('/api/admin/models'),
  deployments: () => request<Deployment[]>('/api/admin/deployments'),
  apiCredentials: () => request<ApiCredential[]>('/api/admin/api-credentials'),
  metrics: (take = 100) => request<RequestMetric[]>(`/api/admin/metrics?take=${take}`),
  metricsSummary: (hours = 24) => request<MetricsSummary>(`/api/admin/metrics/summary?hours=${hours}`),
  audit: (take = 100) => request<AuditEvent[]>(`/api/admin/audit?take=${take}`),
  createNode: (body: { name: string; baseAddress: string; weight: number; maxConcurrency: number }) =>
    request<Node>('/api/admin/nodes', { method: 'POST', body: JSON.stringify(body) }),
  updateNode: (id: string, body: { name: string; baseAddress: string; weight: number; maxConcurrency: number }) =>
    request<Node>(`/api/admin/nodes/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  updateNodeHardwareMetrics: (id: string, baseAddress: string | null) =>
    request<{ id: string; hardwareMetricsBaseAddress: string | null }>(`/api/admin/nodes/${id}/hardware-metrics`, { method: 'PUT', body: JSON.stringify({ baseAddress }) }),
  testNodeConnection: (id: string) => request<NodeConnectionTest>(`/api/admin/nodes/${id}/test-connection`, { method: 'POST' }),
  drainNode: (id: string) => request<void>(`/api/admin/nodes/${id}/drain`, { method: 'POST' }),
  enableNode: (id: string) => request<void>(`/api/admin/nodes/${id}/enable`, { method: 'POST' }),
  disableNode: (id: string) => request<void>(`/api/admin/nodes/${id}/disable`, { method: 'POST' }),
  createModel: (body: { publicName: string; providerModelName: string; supportsStreaming: boolean; supportsTools: boolean }) =>
    request<Model>('/api/admin/models', { method: 'POST', body: JSON.stringify(body) }),
  createDeployment: (body: { nodeId: string; modelId: string; weight: number; maxConcurrency?: number }) =>
    request<Deployment>('/api/admin/deployments', { method: 'POST', body: JSON.stringify(body) }),
  updateDeployment: (id: string, body: { weight: number; maxConcurrency?: number | null; enabled: boolean }) =>
    request<Deployment>(`/api/admin/deployments/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  createApiCredential: (body: { name: string; expiresAtUtc?: string | null }) =>
    request<CreatedApiCredential>('/api/admin/api-credentials', { method: 'POST', body: JSON.stringify(body) }),
  revokeApiCredential: (id: string) => request<void>(`/api/admin/api-credentials/${id}/revoke`, { method: 'POST' })
}
