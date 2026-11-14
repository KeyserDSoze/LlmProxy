import { describe, expect, it } from 'vitest'
import { routableModels } from '../../../src/LlmProxy.Admin/src/routableModels'
import type { Deployment, Model, Node } from '../../../src/LlmProxy.Admin/src/types'

const model = (id: string, patch: Partial<Model> = {}) => ({ id, publicName: id, providerModelName: 'p/' + id, supportsStreaming: true, supportsTools: true, surface: 'OpenAi', enabled: true, ...patch }) as Model
const node = (id: string, patch: Partial<Node> = {}) => ({ id, name: id, enabled: true, status: 'Healthy', ...patch }) as Node
const deployment = (id: string, modelId: string, nodeId: string, enabled = true) => ({ id, modelId, nodeId, enabled, weight: 1 }) as Deployment

describe('routableModels', () => {
  const nodes = [node('n1'), node('n2', { status: 'Unhealthy' }), node('n3', { enabled: false }), node('n4', { status: 'Draining' })]

  it('offers only models a request can reach', () => {
    const models = [model('running'), model('switched-off'), model('no-deployment'), model('on-unhealthy-server'), model('on-disabled-server'), model('on-draining-server'), model('disabled', { enabled: false })]
    const deployments = [
      deployment('d1', 'running', 'n1'), deployment('d2', 'switched-off', 'n1', false), deployment('d3', 'on-unhealthy-server', 'n2'),
      deployment('d4', 'on-disabled-server', 'n3'), deployment('d5', 'on-draining-server', 'n4'), deployment('d6', 'disabled', 'n1')
    ]

    expect(routableModels(models, deployments, nodes, 'OpenAi').map(item => item.id)).toEqual(['running'])
  })

  it('keeps a model with one good deployment among several', () => {
    const deployments = [deployment('d1', 'm', 'n2'), deployment('d2', 'm', 'n1')]
    expect(routableModels([model('m')], deployments, nodes, 'OpenAi')).toHaveLength(1)
  })

  it('separates text models from System One classifiers', () => {
    const models = [model('chat'), model('classifier', { surface: 'SystemOne' })]
    const deployments = [deployment('d1', 'chat', 'n1'), deployment('d2', 'classifier', 'n1')]

    expect(routableModels(models, deployments, nodes, 'OpenAi').map(item => item.id)).toEqual(['chat'])
    expect(routableModels(models, deployments, nodes, 'SystemOne').map(item => item.id)).toEqual(['classifier'])
  })
})
