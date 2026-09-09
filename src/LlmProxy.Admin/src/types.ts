export type Overview = {
  nodes: { total: number; healthy: number; unhealthy: number; draining: number }
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
  enabled: boolean
  status: string
  weight: number
  maxConcurrency: number
  lastHealthCheckUtc?: string | null
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
  deploymentId?: string | null
  nodeId?: string | null
  apiCredentialId?: string | null
  statusCode: number
  durationMilliseconds: number
  errorCode?: string | null
}
