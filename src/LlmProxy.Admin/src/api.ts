import type { ModelManagementOverview } from './types'
import type { AdminSession, AdminTestResult, ApiCredential, AuditEvent, CapacityProfileInput, CapacitySnapshot, ContentLogCleanupResult, ContentLogDetail, ContentLogSettings, ContentLogSummary, CreatedApiCredential, Deployment, DeploymentPerformanceSnapshot, GovernanceCredential, IdentityUserSummary, MetricsSummary, Model, Node, NodeConnectionTest, NodeHardwareMetricsSnapshot, NodeMaintenanceResponse, NodeMaintenanceStatus, NodeRuntimeMetricsSnapshot, Overview, PlatformUser, PlatformUserAccessSettings, RateLimitPolicy, RequestMetric, RevealedApiCredential, RoutingSettings, RoutingTuningSettings, SystemOneStatus, UsageGroup, UsageGroupRateLimitPolicy, UsageReport, UserRateLimitPolicy, UserUsageSummary } from './types'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: init?.body ? { 'Content-Type': 'application/json', ...(init.headers ?? {}) } : init?.headers,
    ...init
  })

  if (response.status === 401) {
    throw new Error('AUTH_REQUIRED')
  }

  if (response.status === 403) {
    throw new Error('FORBIDDEN')
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
  adminSession: () => request<AdminSession>('/api/admin/session'),
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
  identityUsers: () => request<IdentityUserSummary[]>('/api/admin/identity/users'),
  platformUsers: () => request<PlatformUser[]>('/api/admin/users'),
  platformUserAccessSettings: () => request<PlatformUserAccessSettings>('/api/admin/users/settings'),
  updatePlatformUserAccessSettings: (provisioningMode: 'automatic' | 'manual') =>
    request<PlatformUserAccessSettings>('/api/admin/users/settings', { method: 'PUT', body: JSON.stringify({ provisioningMode }) }),
  createPlatformUser: (body: { objectId: string; tenantId?: string | null; principalName?: string | null; displayName?: string | null; enabled?: boolean; usageGroupId?: string | null }) =>
    request<PlatformUser>('/api/admin/users', { method: 'POST', body: JSON.stringify(body) }),
  assignPlatformUserUsageGroup: (id: string, usageGroupId: string | null) =>
    request<void>('/api/admin/users/' + id + '/usage-group', { method: 'PUT', body: JSON.stringify({ usageGroupId }) }),
  disablePlatformUser: (id: string) => request<void>('/api/admin/users/' + id + '/disable', { method: 'POST' }),
  enablePlatformUser: (id: string) => request<void>('/api/admin/users/' + id + '/enable', { method: 'POST' }),
  userRateLimits: () => request<UserRateLimitPolicy[]>('/api/admin/user-rate-limits'),
  createUserRateLimit: (body: { ownerTenantId: string; ownerObjectId: string; logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled?: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<UserRateLimitPolicy>('/api/admin/user-rate-limits', { method: 'POST', body: JSON.stringify(body) }),
  updateUserRateLimit: (id: string, body: { logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<UserRateLimitPolicy>(`/api/admin/user-rate-limits/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  clearUserOutputTokenBudget: (id: string) => request<void>(`/api/admin/user-rate-limits/${id}/output-token-budget`, { method: 'DELETE' }),
  deleteUserRateLimit: (id: string) => request<void>(`/api/admin/user-rate-limits/${id}`, { method: 'DELETE' }),
  groupRateLimits: () => request<UsageGroupRateLimitPolicy[]>('/api/admin/group-rate-limits'),
  createGroupRateLimit: (body: { usageGroupId: string; logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled?: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<UsageGroupRateLimitPolicy>('/api/admin/group-rate-limits', { method: 'POST', body: JSON.stringify(body) }),
  updateGroupRateLimit: (id: string, body: { logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<UsageGroupRateLimitPolicy>(`/api/admin/group-rate-limits/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  clearGroupOutputTokenBudget: (id: string) => request<void>(`/api/admin/group-rate-limits/${id}/output-token-budget`, { method: 'DELETE' }),
  deleteGroupRateLimit: (id: string) => request<void>(`/api/admin/group-rate-limits/${id}`, { method: 'DELETE' }),
  createRateLimit: (body: { apiCredentialId: string; logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled?: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<RateLimitPolicy>('/api/admin/rate-limits', { method: 'POST', body: JSON.stringify(body) }),
  updateRateLimit: (id: string, body: { logicalModel?: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean; outputTokensPerWindow?: number | null; maxOutputTokensPerRequest?: number | null }) =>
    request<RateLimitPolicy>(`/api/admin/rate-limits/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  setOutputTokenBudget: (id: string, outputTokensPerWindow: number, maxOutputTokensPerRequest: number) =>
    request<RateLimitPolicy>(`/api/admin/rate-limits/${id}/output-token-budget`, { method: 'PUT', body: JSON.stringify({ outputTokensPerWindow, maxOutputTokensPerRequest }) }),
  clearOutputTokenBudget: (id: string) => request<void>(`/api/admin/rate-limits/${id}/output-token-budget`, { method: 'DELETE' }),
  deleteRateLimit: (id: string) => request<void>(`/api/admin/rate-limits/${id}`, { method: 'DELETE' }),
  usageSummary: (days = 30) => request<UsageReport>(`/api/admin/usage/summary?days=${days}`),
  usageUsers: (days = 30) => request<UserUsageSummary[]>(`/api/admin/usage/users?days=${days}`),
  updateCredentialCallerGovernance: (id: string, enabled: boolean) => request<{ id: string; name: string; kind: string; enforceCallerGovernance: boolean }>(`/api/admin/api-credentials/${id}/caller-governance`, { method: 'PUT', body: JSON.stringify({ enabled }) }),
  metrics: (take = 100) => request<RequestMetric[]>(`/api/admin/metrics?take=${take}`),
  metricsSummary: (hours = 24) => request<MetricsSummary>(`/api/admin/metrics/summary?hours=${hours}`),
  audit: (take = 100) => request<AuditEvent[]>('/api/admin/audit?take=' + take),
  contentLogs: (take = 100) => request<ContentLogSummary[]>('/api/admin/content-logs?take=' + take),
  contentLog: (id: number) => request<ContentLogDetail>('/api/admin/content-logs/' + id),
  contentLogSettings: () => request<ContentLogSettings>('/api/admin/content-logs/settings'),
  updateContentLogSettings: (retentionDays: number) => request<ContentLogSettings>('/api/admin/content-logs/settings', { method: 'PUT', body: JSON.stringify({ retentionDays }) }),
  runContentLogRetention: () => request<ContentLogCleanupResult>('/api/admin/content-logs/retention/run', { method: 'POST' }),
  systemOneStatus: () => request<SystemOneStatus>('/api/admin/testing/systemone'),
  testSystemOne: (payload: unknown) => request<AdminTestResult>('/api/admin/testing/systemone', { method: 'POST', body: JSON.stringify({ payload }) }),
  testChat: (body: { model: string; userPrompt: string; systemPrompt?: string | null; maxTokens?: number; temperature?: number }) =>
    request<AdminTestResult>('/api/admin/testing/chat', { method: 'POST', body: JSON.stringify(body) }),
  createNode: (body: { name: string; baseAddress: string; weight: number; maxConcurrency: number; upstreamBearerToken?: string | null }) =>
    request<Node>('/api/admin/nodes', { method: 'POST', body: JSON.stringify(body) }),
  setNodeUpstreamCredential: (id: string, bearerToken: string) =>
    request<{ id: string; hasUpstreamCredential: boolean }>(`/api/admin/nodes/${id}/upstream-credential`, { method: 'PUT', body: JSON.stringify({ bearerToken }) }),
  clearNodeUpstreamCredential: (id: string) =>
    request<void>(`/api/admin/nodes/${id}/upstream-credential`, { method: 'DELETE' }),
  updateNode: (id: string, body: { name: string; baseAddress: string; weight: number; maxConcurrency: number }) =>
    request<Node>(`/api/admin/nodes/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  updateNodeHardwareMetrics: (id: string, baseAddress: string | null) =>
    request<{ id: string; hardwareMetricsBaseAddress: string | null }>(`/api/admin/nodes/${id}/hardware-metrics`, { method: 'PUT', body: JSON.stringify({ baseAddress }) }),
  testNodeConnection: (id: string) => request<NodeConnectionTest>(`/api/admin/nodes/${id}/test-connection`, { method: 'POST' }),
  nodeMaintenance: (id: string) => request<NodeMaintenanceStatus>(`/api/admin/nodes/${id}/maintenance`),
  beginNodeMaintenance: (id: string) => request<NodeMaintenanceResponse>(`/api/admin/nodes/${id}/maintenance/drain`, { method: 'POST' }),
  resumeNodeMaintenance: (id: string) => request<NodeMaintenanceResponse>(`/api/admin/nodes/${id}/maintenance/resume`, { method: 'POST' }),
  drainNode: async (id: string) => {
    await request<NodeMaintenanceResponse>(`/api/admin/nodes/${id}/maintenance/drain`, { method: 'POST' })
  },
  enableNode: async (id: string) => {
    const maintenance = await request<NodeMaintenanceStatus>(`/api/admin/nodes/${id}/maintenance`)
    if (maintenance.nodeStatus === 'Draining') {
      await request<NodeMaintenanceResponse>(`/api/admin/nodes/${id}/maintenance/resume`, { method: 'POST' })
      return
    }
    await request<void>(`/api/admin/nodes/${id}/enable`, { method: 'POST' })
  },
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
  rotateApiCredential: (id: string) => request<CreatedApiCredential>('/api/admin/api-credentials/' + id + '/rotate', { method: 'POST' }),
  revealApiCredential: (id: string) => request<RevealedApiCredential>('/api/admin/api-credentials/' + id + '/secret'),
  revokeApiCredential: (id: string) => request<void>(`/api/admin/api-credentials/${id}/revoke`, { method: 'POST' }),
  modelManagementOverview: (nodeId: string) =>
    request<ModelManagementOverview>(`/api/admin/model-management/nodes/${nodeId}/overview`),
  configureNodeManagement: (nodeId: string, body: { managementBaseAddress?: string | null; bearerToken?: string | null; clearBearerToken?: boolean }) =>
    request<{ id: string; managementBaseAddress?: string | null; hasManagementCredential: boolean }>(`/api/admin/model-management/nodes/${nodeId}/configuration`, { method: 'PUT', body: JSON.stringify(body) }),
  installManagedModel: (nodeId: string, catalogId: string, body: { publicName?: string | null; port?: number | null; force?: boolean; extraArguments?: string[] }) =>
    request<unknown>(`/api/admin/model-management/nodes/${nodeId}/models/${encodeURIComponent(catalogId)}/install`, { method: 'POST', body: JSON.stringify(body) }),
  startManagedDeployment: (deploymentId: string) =>
    request<unknown>(`/api/admin/model-management/deployments/${deploymentId}/start`, { method: 'POST' }),
  stopManagedDeployment: (deploymentId: string) =>
    request<unknown>(`/api/admin/model-management/deployments/${deploymentId}/stop`, { method: 'POST' }),
  removeManagedDeployment: (deploymentId: string) =>
    request<void>(`/api/admin/model-management/deployments/${deploymentId}`, { method: 'DELETE' })
}