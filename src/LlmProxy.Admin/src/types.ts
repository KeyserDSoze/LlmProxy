export type Overview = {
  nodes: { total: number; healthy: number; degraded: number; unhealthy: number; draining: number }
  models: number
  deployments: number
  activeRequests: number
  requestsToday: number
}

export type RoutingSettings = {
  strategy: 'WeightedLeastLoaded' | 'RoundRobin' | 'WeightedRoundRobin'
  supportedStrategies: Array<'WeightedLeastLoaded' | 'RoundRobin' | 'WeightedRoundRobin'>
  updatedAtUtc?: string
}

export type RoutingTuningSettings = {
  warmupSamples: number
  ttftTargetMilliseconds: number
  ttftPenaltyWeight: number
  failurePenaltyWeight: number
  externalLoadPenaltyWeight: number
  queuePenaltyWeight: number
  kvCacheThreshold: number
  kvCachePenaltyWeight: number
  degradedNodePenalty: number
  unknownNodePenalty: number
  updatedAtUtc?: string
}

export type DeploymentPerformanceSnapshot = {
  deploymentId: string
  sampleCount: number
  ewmaTimeToFirstByteMilliseconds?: number | null
  ewmaDurationMilliseconds?: number | null
  infrastructureFailureScore: number
  lastObservedAtUtc?: string | null
}

export type NodeRuntimeMetricsSnapshot = {
  nodeId: string
  available: boolean
  modelName?: string | null
  runningRequests: number
  waitingRequests: number
  kvCacheUsageRatio?: number | null
  promptTokensTotal?: number | null
  generationTokensTotal?: number | null
  collectedAtUtc?: string | null
  lastAttemptAtUtc?: string | null
  error?: string | null
}

export type NodeHardwareMetricsSnapshot = {
  nodeId: string
  available: boolean
  gpuCount: number
  averageGpuUtilizationPercent?: number | null
  maxGpuUtilizationPercent?: number | null
  framebufferUsedMiB?: number | null
  framebufferFreeMiB?: number | null
  framebufferUsageRatio?: number | null
  maxTemperatureCelsius?: number | null
  totalPowerUsageWatts?: number | null
  collectedAtUtc?: string | null
  lastAttemptAtUtc?: string | null
  error?: string | null
}

export type EndpointProbe = {
  url: string
  success: boolean
  statusCode?: number | null
  latencyMilliseconds: number
  error?: string | null
}

export type NodeConnectionTest = {
  nodeId: string
  nodeName: string
  serviceRoot: string
  healthUrl: string
  modelsUrl: string
  chatCompletionsUrl: string
  responsesUrl: string
  success: boolean
  health: EndpointProbe
  openAi: EndpointProbe
}

export type NodeMaintenanceStatus = {
  nodeId: string
  nodeName: string
  nodeStatus: string
  enabled: boolean
  provider: string
  coordinationAvailable: boolean
  admissionBlocked: boolean
  activeRequests: number
  drained: boolean
}

export type NodeMaintenanceResponse = {
  status: NodeMaintenanceStatus
  coordinationPending?: boolean
  repairedCoordination?: boolean
  health?: EndpointProbe
  models?: EndpointProbe
  warmups?: EndpointProbe[]
}

export type AgentPairingInvitation = { enrollmentToken: string; expiresAtUtc: string }
export type PairedNodeStatus = { nodeId: string; mode: 'direct' | 'outbound'; lastHeartbeatAtUtc?: string | null; agentVersion?: string | null }

export type Node = {
  id: string
  name: string
  baseAddress: string
  hardwareMetricsBaseAddress?: string | null
  managementBaseAddress?: string | null
  hasManagementCredential?: boolean
  hasUpstreamCredential?: boolean
  enabled: boolean
  status: string
  weight: number
  maxConcurrency: number
  lastHealthCheckUtc?: string | null
  lastHealthyAtUtc?: string | null
  lastHealthLatencyMilliseconds?: number | null
  lastHealthError?: string | null
  consecutiveHealthSuccesses: number
  consecutiveHealthFailures: number
}

export type Model = {
  id: string
  publicName: string
  providerModelName: string
  supportsStreaming: boolean
  supportsTools: boolean
  surface: 'OpenAi' | 'SystemOne'
  enabled: boolean
}

export type Deployment = {
  id: string
  nodeId: string
  modelId: string
  enabled: boolean
  weight: number
  maxConcurrency?: number | null
  runtimeBaseAddress?: string | null
  catalogModelId?: string | null
  managedInstallationId?: string | null
  recommendedMaxConcurrency?: number | null
  benchmarkP95TtftMilliseconds?: number | null
  benchmarkP95DurationMilliseconds?: number | null
  sustainableOutputTokensPerSecond?: number | null
  benchmarkSource?: string | null
  benchmarkMeasuredAtUtc?: string | null
}

export type NodeCapacitySnapshot = {
  id: string
  name: string
  maxConcurrency: number
  activeRequests: number
  localActiveRequests?: number
  remaining: number
  capacityProvider?: string
  coordinationAvailable?: boolean
  admissionBlocked?: boolean
}

export type DeploymentCapacitySnapshot = {
  id: string
  nodeId: string
  modelId: string
  enabled: boolean
  maxConcurrency?: number | null
  effectiveMaxConcurrency: number
  activeRequests: number
  recommendedMaxConcurrency?: number | null
  benchmarkP95TtftMilliseconds?: number | null
  benchmarkP95DurationMilliseconds?: number | null
  sustainableOutputTokensPerSecond?: number | null
  benchmarkSource?: string | null
  benchmarkMeasuredAtUtc?: string | null
}

export type CapacitySnapshot = {
  nodes: NodeCapacitySnapshot[]
  deployments: DeploymentCapacitySnapshot[]
}

export type CapacityProfileInput = {
  recommendedMaxConcurrency: number
  p95TtftMilliseconds?: number | null
  p95DurationMilliseconds?: number | null
  sustainableOutputTokensPerSecond?: number | null
  benchmarkSource: string
  measuredAtUtc: string
}

export type ApiCredential = {
  id: string
  name: string
  keyPrefix: string
  enabled: boolean
  createdAtUtc: string
  expiresAtUtc?: string | null
  lastUsedAtUtc?: string | null
  usageGroupId?: string | null
  kind?: 'organization' | 'personal'
  enforceCallerGovernance?: boolean
  ownerTenantId?: string | null
  ownerObjectId?: string | null
  ownerPrincipalName?: string | null
  secretAvailable?: boolean
}

export type GovernanceCredential = ApiCredential & {
  usageGroupId?: string | null
}

export type CreatedApiCredential = ApiCredential & {
  secret: string
}

export type IdentityUserSummary = {
  tenantId: string
  objectId: string
  principalName?: string | null
  credentialCount: number
  activeCredentialCount: number
  lastUsedAtUtc?: string | null
  firstCredentialCreatedAtUtc: string
}

export type UserRateLimitPolicy = {
  id: string
  ownerTenantId: string
  ownerObjectId: string
  principalName?: string | null
  logicalModel?: string | null
  requestsPerWindow: number
  windowSeconds: number
  outputTokensPerWindow?: number | null
  maxOutputTokensPerRequest?: number | null
  enabled: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export type UsageGroup = {
  id: string
  name: string
  description?: string | null
  createdAtUtc: string
  updatedAtUtc: string
  credentialCount: number
  userCount: number
}

export type RateLimitPolicy = {
  id: string
  apiCredentialId: string
  credentialName?: string | null
  keyPrefix?: string | null
  logicalModel?: string | null
  requestsPerWindow: number
  windowSeconds: number
  outputTokensPerWindow?: number | null
  maxOutputTokensPerRequest?: number | null
  enabled: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export type UsageGroupSummary = {
  usageGroupId?: string | null
  name: string
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
  averageTtftMilliseconds?: number | null
  averageDurationMilliseconds?: number | null
}

export type UsageCredentialSummary = {
  apiCredentialId: string
  name: string
  keyPrefix?: string | null
  usageGroupId?: string | null
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
}

export type UsageModelSummary = {
  logicalModel: string
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
}

export type UsageReport = {
  windowDays: number
  sinceUtc: string
  windowGranularity: 'utc_day'
  rawRetentionDays: number
  rollupRetentionDays: number
  rawRequestCount: number
  rolledUpRequestCount: number
  historicalRollupsUsed: boolean
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
  capacityExhaustedRequests: number
  groups: UsageGroupSummary[]
  credentials: UsageCredentialSummary[]
  models: UsageModelSummary[]
}

export type RequestMetric = {
  id: number
  requestId: string
  startedAtUtc: string
  logicalModel: string
  surface: string
  deploymentId?: string | null
  nodeId?: string | null
  apiCredentialId?: string | null
  usageGroupId?: string | null
  statusCode: number
  durationMilliseconds: number
  attemptCount: number
  isStreaming: boolean
  upstreamHeaderMilliseconds?: number | null
  timeToFirstByteMilliseconds?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  errorCode?: string | null
}

export type ModelMetricSummary = {
  logicalModel: string
  requestCount: number
  errorCount: number
  averageDurationMilliseconds?: number | null
  averageTimeToFirstByteMilliseconds?: number | null
  outputTokens: number
}

export type NodeMetricSummary = {
  nodeId: string
  requestCount: number
  errorCount: number
  averageDurationMilliseconds?: number | null
  p95DurationMilliseconds?: number | null
  outputTokens: number
}

export type MetricsSummary = {
  windowHours: number
  sinceUtc: string
  requestCount: number
  successCount: number
  errorCount: number
  successRatePercent: number
  p50DurationMilliseconds?: number | null
  p95DurationMilliseconds?: number | null
  p50TimeToFirstByteMilliseconds?: number | null
  p95TimeToFirstByteMilliseconds?: number | null
  averageUpstreamHeaderMilliseconds?: number | null
  inputTokens: number
  outputTokens: number
  totalTokens: number
  tokenObservedRequests: number
  failoverRequests: number
  streamingRequests: number
  byModel: ModelMetricSummary[]
  byNode: NodeMetricSummary[]
}

export type AuditEvent = {
  id: number
  occurredAtUtc: string
  actor: string
  action: string
  entityType: string
  entityId: string
  sourceIp?: string | null
  detailsJson?: string | null
}


export type AdminSession = {
  canWrite: boolean
  roles: string[]
}

export type RevealedApiCredential = {
  id: string
  name: string
  keyPrefix: string
  secret: string
}

export type SystemOneStatus = {
  enabled: boolean
  baseAddress?: string | null
  upstreamEndpoint?: string | null
  publicEndpoint: string
  apiKeyConfigured: boolean
  timeoutSeconds: number
  configurationError?: string | null
  models?: string[]
  defaultModel?: string | null
}

export type AdminTestResult = {
  requestId: string
  success: boolean
  statusCode: number
  latencyMilliseconds: number
  requestBody?: string
  responseBody?: string
  responseContentType?: string | null
  upstreamEndpoint?: string
  logicalModel?: string
  providerModel?: string
  deploymentId?: string
  nodeId?: string
  nodeName?: string
  error?: string
  message?: string
}

export type ContentLogSummary = {
  id: number
  requestId: string
  startedAtUtc: string
  completedAtUtc: string
  surface: string
  method: string
  path: string
  logicalModel?: string | null
  apiCredentialId?: string | null
  statusCode: number
  requestContentType?: string | null
  responseContentType?: string | null
}

export type ContentLogPage = {
  items: ContentLogSummary[]
  total: number
  page: number
  pageSize: number
}

export type ContentLogDetail = ContentLogSummary & {
  requestBody: string
  responseBody: string
  deploymentId?: string | null
  nodeId?: string | null
  usageGroupId?: string | null
  attemptCount?: number | null
  isStreaming?: boolean | null
  timeToFirstByteMilliseconds?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  errorCode?: string | null
}

export type ContentLogSettings = {
  retentionDays: number
  updatedAtUtc: string
  minimumRetentionDays: number
  maximumRetentionDays: number
  cleanupIntervalHours: number
}

export type ContentLogCleanupResult = {
  startedAtUtc: string
  completedAtUtc: string
  retentionDays: number
  cutoffUtc: string
  deletedLogs: number
}


export type PlatformUserAccessSettings = {
  provisioningMode: 'automatic' | 'manual'
  updatedAtUtc: string
  configuredTenantId?: string | null
}

export type PlatformUser = {
  id: string
  tenantId: string
  objectId: string
  principalName?: string | null
  displayName?: string | null
  usageGroupId?: string | null
  usageGroupName?: string | null
  enabled: boolean
  provisioningSource: 'automatic' | 'admin' | 'migration' | string
  createdAtUtc: string
  lastSeenAtUtc?: string | null
  disabledAtUtc?: string | null
  credentialCount: number
  activeCredentialCount: number
  lastCredentialUsedAtUtc?: string | null
  requestCount30d: number
  errorCount30d: number
}


export type UsageGroupRateLimitPolicy = {
  id: string
  usageGroupId: string
  usageGroupName?: string | null
  logicalModel?: string | null
  requestsPerWindow: number
  windowSeconds: number
  outputTokensPerWindow?: number | null
  maxOutputTokensPerRequest?: number | null
  enabled: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export type UserUsageSummary = {
  tenantId: string
  objectId: string
  principalName?: string | null
  displayName?: string | null
  usageGroupId?: string | null
  requestCount: number
  errorCount: number
  inputTokens: number
  outputTokens: number
  totalTokens: number
  rateLimitedRequests: number
}


export type DeployableModel = {
  runtime?: 'vllm' | 'llama.cpp' | 'sglang'
  id: string
  displayName: string
  providerModelName: string
  family: string
  license: string
  sourceUrl: string
  parameterBillions: number
  contextTokens: number
  precision: string
  minimumGpuMemoryGiB: number
  recommendedGpuMemoryGiB: number
  minimumSystemMemoryGiB: number
  recommendedSystemMemoryGiB: number
  diskGiB: number
  minimumGpuCount: number
  recommendedGpuCount: number
  supportsStreaming: boolean
  supportsTools: boolean
  tags: string[]
  notes: string
}

export type ModelCompatibility = {
  status: 'unknown' | 'insufficient' | 'tight' | 'fits'
  summary: string
  reasons: string[]
  suggestedTensorParallelSize: number
}

export type GpuInventory = {
  name: string
  memoryTotalGiB: number
  memoryFreeGiB: number
  driverVersion?: string | null
  computeCapability?: string | null
}

export type HardwareInventory = {
  hostname: string
  operatingSystem?: string | null
  architecture?: string | null
  cpuLogicalCores: number
  systemMemoryTotalGiB: number
  systemMemoryAvailableGiB: number
  diskTotalGiB: number
  diskAvailableGiB: number
  gpus?: GpuInventory[] | null
  runtime?: string | null
  runtimeVersion?: string | null
}

export type ManagedModelState = {
  runtime?: string
  maxNumSeqs?: number | null
  maxModelLen?: number | null
  kvCacheDtype?: string | null
  cpuOffloadGiB?: number | null
  installationId: string
  catalogModelId?: string | null
  providerModelName?: string | null
  status: string
  runtimeBaseAddress?: string | null
  port?: number | null
  error?: string | null
}

export type DeploymentRuntimeMetricsSnapshot = {
  deploymentId: string
  nodeId: string
  available: boolean
  runtime?: string | null
  modelName?: string | null
  runningRequests: number
  waitingRequests: number
  cacheUsageRatio?: number | null
  promptTokensTotal?: number | null
  generationTokensTotal?: number | null
  collectedAtUtc?: string | null
  error?: string | null
}

export type ManagedInstallation = {
  runtime?: string
  id: string
  modelId: string
  logicalModel?: string | null
  providerModelName?: string | null
  catalogModelId?: string | null
  managedInstallationId?: string | null
  runtimeBaseAddress?: string | null
  enabled: boolean
  agentStatus: string
  runtimeMetrics?: DeploymentRuntimeMetricsSnapshot | null
  agentState?: ManagedModelState | null
}

export type ModelManagementOverview = {
  node: {
    id: string
    name: string
    managementBaseAddress?: string | null
    hasManagementCredential: boolean
  }
  agentAvailable: boolean
  agentError?: string | null
  hardware?: HardwareInventory | null
  catalog: Array<{ model: DeployableModel; compatibility: ModelCompatibility }>
  installations: ManagedInstallation[]
}

export type UpdateJobStatus = {
  id: string
  version: string
  requestedAtUtc: string
  scheduledForUtc: string
  status: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | string
  startedAtUtc?: string | null
  completedAtUtc?: string | null
  error?: string | null
  exitCode?: number | null
  upgradePath?: string[] | null
  currentStep?: string | null
}

export type AvailableProductRelease = {
  version: string
  title: string
  publishedAtUtc: string
  releaseUrl: string
  isNewer: boolean
  updateMode: 'standard' | 'custom' | string
  updateTitle: string
  updateDescription: string
  requiresHostRestart: boolean
  operatorCommand: string
}

export type ProductUpdatePolicy = {
  mode: 'manual' | 'asap' | 'nightly' | 'weekly' | 'monthly'
  timeZoneId: string
  localHour: number
  localMinute: number
  dayOfWeek: number
  dayOfMonth: number
  lastCheckedAtUtc?: string | null
  lastScheduledAtUtc?: string | null
  lastScheduledVersion?: string | null
  lastError?: string | null
  updatedAtUtc: string
}

export type ProductUpdateOverview = {
  currentVersion: string
  agentAvailable: boolean
  policy: ProductUpdatePolicy
  agent?: {
    installedVersion?: string | null
    activeJob?: UpdateJobStatus | null
    recentJobs: UpdateJobStatus[]
  } | null
  releases: AvailableProductRelease[]
}
