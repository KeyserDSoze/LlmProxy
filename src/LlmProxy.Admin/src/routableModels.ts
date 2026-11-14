import type { Deployment, Model, Node } from './types'

/** Server states that cannot take new requests right now. */
const unavailableNodeStates = new Set(['unhealthy', 'draining'])

/**
 * Models a request can actually reach: the model is enabled and at least one of its deployments is switched on, on a server
 * that is enabled and neither unhealthy nor draining. The Playground offers only these; why another model does not route
 * is the job of the model's own page and of the route diagnosis.
 */
export function routableModels(models: Model[], deployments: Deployment[], nodes: Node[], surface: 'OpenAi' | 'SystemOne'): Model[] {
  const nodeById = new Map(nodes.map(node => [node.id, node]))
  return models.filter(model => {
    if (!model.enabled || (model.surface ?? 'OpenAi') !== surface) return false
    return deployments.some(deployment => {
      if (deployment.modelId !== model.id || !deployment.enabled) return false
      const node = nodeById.get(deployment.nodeId)
      return !!node && node.enabled && !unavailableNodeStates.has(String(node.status).toLowerCase())
    })
  })
}
