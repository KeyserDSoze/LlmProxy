import type { Deployment, Model, Node } from './types'

/** Server states that cannot take new requests right now. */
const unavailableNodeStates = new Set(['unhealthy', 'draining'])

/**
 * A model a request can actually reach: it is enabled and at least one of its deployments is switched on, on a server
 * that is enabled and neither unhealthy nor draining. Same rule as the gateway's `GET /v1/models`.
 */
export function isRoutable(model: Model, deployments: Deployment[], nodes: Node[]): boolean {
  if (!model.enabled) return false
  const nodeById = new Map(nodes.map(node => [node.id, node]))
  return deployments.some(deployment => {
    if (deployment.modelId !== model.id || !deployment.enabled) return false
    const node = nodeById.get(deployment.nodeId)
    return !!node && node.enabled && !unavailableNodeStates.has(String(node.status).toLowerCase())
  })
}

/** The routable models of one surface; the Playground offers only these. */
export function routableModels(models: Model[], deployments: Deployment[], nodes: Node[], surface: 'OpenAi' | 'SystemOne'): Model[] {
  return models.filter(model => (model.surface ?? 'OpenAi') === surface && isRoutable(model, deployments, nodes))
}
