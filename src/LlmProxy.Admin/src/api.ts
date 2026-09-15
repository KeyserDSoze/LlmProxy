import type { ApiCredential, AuditEvent, CapacityProfileInput, CapacitySnapshot, CreatedApiCredential, Deployment, DeploymentPerformanceSnapshot, GovernanceCredential, MetricsSummary, Model, Node, NodeConnectionTest, NodeHardwareMetricsSnapshot, NodeRuntimeMetricsSnapshot, Overview, RateLimitPolicy, RequestMetric, RoutingSettings, RoutingTuningSettings, UsageGroup, UsageReport } from './types'

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
  capacity: () => request<CapacitySnapshot>('/api/admin/capacity'),
  updateRouting: (strategy: RoutingSettings['strategy']) =>
    request<RoutingSettings>('/api/admin/routing', { method: 'PUT', body: JSON.stringify({ strategy }) }),
  updateRoutingTuning: (settings: Omit<RoutingTuningSettings, 'updatedAtUtc'>) =>
    request<RoutingTuningSettings>('/api/admin/routing/tuning', { method: 'PUT', body: JSON.stringify(settings) }),
  nodes: () => request<Node[]>('/api/admin/nodes'),
  models: () => request<Model[]>('/api/admin/models'),
  deployments: () => request<Deployment[]>('/api/admin/deployments'),
  apiCredentials: () => request<ApiCredential[]>('/api/admin/api-credentials'),
  governanceCredentials: () => request<GovernanceCredential[]>('/api/admin/governance/credentials'),
  usageGroups: () => request<UsageGroup[]>('/api/admin/usage-groups'),
  createUsageGroup: (body: { name: string; description?: string | null }) =>
    request<UsageGroup>('/api/admin/usage-groups', { method: 'POST', body: JSON.stringify(body) }),
  updateUsageGroup: (id: string, body: { name: string; description?: string | null }) =>
    request<UsageGroup>(`/api/admin/usage-groups/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  assignCredentialUsageGroup: (credentialId: string, usageGroupId: string) =>
    request<void>(`/api/admin/api-credentials/${credentialId}/usage-group`, { method: 'PUT', body: JSON.stringify({ usageGroupId }) }),
  clearCredentialUsageGroup: (credentialId: string) =>
    request<void>(`/api/admin/api-credentials/${credentialId}/usage-group`, { method: 'DELETE' }),
  rateLimits: () => request<RateLimitPolicy[]>('/api/admin/rate-limits'),
  createRateLimit: (body: { apiCredentialId: string; logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled?: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<RateLimitPolicy>('/api/admin/rate-limits', { method: 'POST', body: JSON.stringify(body) }),
  updateRateLimit: (id: string, body: { logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<RateLimitPolicy>(`/api/admin/rate-limits/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  setOutputTokenBudget: (id: string, outputTokensPerWindow: number, maxOutputTokensPerRequest: number) =>
    request<RateLimitPolicy>(`/api/admin/rate-limits/${id}/output-token-budget`, { method: 'PUT', body: JSON.stringify({ outputTokensPerWindow, maxOutputTokensPerRequest }) }),
  clearOutputTokenBudget: (id: string) => request<void>(`/api/admin/rate-limits/${id}/output-token-budget`, { method: 'DELETE' }),
  deleteRateLimit: (id: string) => request<void>(`/api/admin/rate-limits/${id}`, { method: 'DELETE' }),
  usageSummary: (days = 30) => request<UsageReport>(`/api/admin/usage/summary?days=${days}`),
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
  updateCapacityProfile: (id: string, body: CapacityProfileInput) =>
    request<Deployment>(`/api/admin/deployments/${id}/capacity-profile`, { method: 'PUT', body: JSON.stringify(body) }),
  clearCapacityProfile: (id: string) =>
    request<void>(`/api/admin/deployments/${id}/capacity-profile`, { method: 'DELETE' }),
  applyCapacityProfile: (id: string) =>
    request<Deployment>(`/api/admin/deployments/${id}/capacity-profile/apply`, { method: 'POST' }),
  createApiCredential: (body: { name: string; expiresAtUtc?: string | null }) =>
    request<CreatedApiCredential>('/api/admin/api-credentials', { method: 'POST', body: JSON.stringify(body) }),
  rotateApiCredential: (id: string) => request<CreatedApiCredential>(`/api/admin/api-credentials/${id}/rotate`, { method: 'POST' }),
  revokeApiCredential: (id: string) => request<void>(`/api/admin/api-credentials/${id}/revoke`, { method: 'POST' })
}
