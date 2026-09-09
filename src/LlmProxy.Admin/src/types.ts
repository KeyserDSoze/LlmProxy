export type Overview = {
  nodes: { total: number; healthy: number; unhealthy: number; draining: number }
  models: number
  deployments: number
  activeRequests: number
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
