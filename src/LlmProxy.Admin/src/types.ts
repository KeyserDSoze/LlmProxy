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

export type Node = {
  id: string
  name: string
  baseAddress: string
  hardwareMetricsBaseAddress?: string | null
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
  enabled: boolean
}

export type Deployment = {
  id: string
  nodeId: string
  modelId: string
  enabled: boolean
  weight: number
  maxConcurrency?: number | null
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
  remaining: number
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
}

export type CreatedApiCredential = ApiCredential & {
  secret: string
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
