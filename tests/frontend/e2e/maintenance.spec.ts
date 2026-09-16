import { expect, Page, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function installMaintenanceApi(page: Page) {
  const node = {
    id: 'node-maintenance',
    name: 'dgx-maintenance',
    baseAddress: 'http://10.0.0.31:8000/vllm',
    hardwareMetricsBaseAddress: null,
    weight: 1,
    maxConcurrency: 4,
    enabled: true,
    status: 'Healthy',
    lastHealthCheckUtc: '2026-09-15T20:00:00Z',
    lastHealthyAtUtc: '2026-09-15T20:00:00Z',
    lastHealthLatencyMilliseconds: 8,
    lastHealthError: null,
    consecutiveHealthSuccesses: 3,
    consecutiveHealthFailures: 0
  }
  let legacyDrainCalled = false
  let resumeCalled = false

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    const method = request.method()

    if (method === 'GET' && path === '/api/admin/overview') return json(route, { nodes: { total: 1, healthy: node.status === 'Healthy' ? 1 : 0, degraded: 0, unhealthy: 0, draining: node.status === 'Draining' ? 1 : 0 }, models: 1, deployments: 1, activeRequests: 0, requestsToday: 0 })
    if (method === 'GET' && path === '/api/admin/routing') return json(route, { strategy: 'WeightedLeastLoaded', supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    if (method === 'GET' && path === '/api/admin/routing/tuning') return json(route, { warmupSamples: 3, ttftTargetMilliseconds: 2000, ttftPenaltyWeight: 0.25, failurePenaltyWeight: 1.5, externalLoadPenaltyWeight: 0.4, queuePenaltyWeight: 0.75, kvCacheThreshold: 0.7, kvCachePenaltyWeight: 0.6, degradedNodePenalty: 0.35, unknownNodePenalty: 0.1 })
    if (method === 'GET' && path === '/api/admin/routing/performance') return json(route, [])
    if (method === 'GET' && path === '/api/admin/routing/runtime') return json(route, [])
    if (method === 'GET' && path === '/api/admin/hardware') return json(route, [])
    if (method === 'GET' && path === '/api/admin/nodes') return json(route, [node])
    if (method === 'GET' && path === '/api/admin/models') return json(route, [{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'bootstrap-model', supportsStreaming: true, supportsTools: true, enabled: true }])
    if (method === 'GET' && path === '/api/admin/deployments') return json(route, [{ id: 'deployment-1', nodeId: node.id, modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4 }])
    if (method === 'GET' && path === '/api/admin/api-credentials') return json(route, [])
    if (method === 'GET' && path === '/api/admin/metrics') return json(route, [])
    if (method === 'GET' && path === '/api/admin/metrics/summary') return json(route, { windowHours: 24, sinceUtc: '', requestCount: 0, successCount: 0, errorCount: 0, successRatePercent: 0, p50DurationMilliseconds: null, p95DurationMilliseconds: null, p50TimeToFirstByteMilliseconds: null, p95TimeToFirstByteMilliseconds: null, averageUpstreamHeaderMilliseconds: null, inputTokens: 0, outputTokens: 0, totalTokens: 0, tokenObservedRequests: 0, failoverRequests: 0, streamingRequests: 0, byModel: [], byNode: [] })
    if (method === 'GET' && path === '/api/admin/audit') return json(route, [])

    if (method === 'POST' && path === `/api/admin/nodes/${node.id}/drain`) {
      legacyDrainCalled = true
      return json(route, { error: 'legacy drain must not be called' }, 500)
    }

    if (method === 'GET' && path === `/api/admin/nodes/${node.id}/maintenance`) {
      return json(route, {
        nodeId: node.id,
        nodeName: node.name,
        nodeStatus: node.status,
        enabled: node.enabled,
        provider: 'redis',
        coordinationAvailable: true,
        admissionBlocked: node.status === 'Draining',
        activeRequests: 0,
        drained: node.status === 'Draining'
      })
    }

    if (method === 'POST' && path === `/api/admin/nodes/${node.id}/maintenance/drain`) {
      node.status = 'Draining'
      return json(route, {
        status: { nodeId: node.id, nodeName: node.name, nodeStatus: 'Draining', enabled: true, provider: 'redis', coordinationAvailable: true, admissionBlocked: true, activeRequests: 0, drained: true },
        coordinationPending: false
      }, 202)
    }

    if (method === 'POST' && path === `/api/admin/nodes/${node.id}/maintenance/resume`) {
      resumeCalled = true
      node.status = 'Healthy'
      return json(route, {
        status: { nodeId: node.id, nodeName: node.name, nodeStatus: 'Healthy', enabled: true, provider: 'redis', coordinationAvailable: true, admissionBlocked: false, activeRequests: 0, drained: false },
        health: { url: `${node.baseAddress}/health`, success: true, statusCode: 200, latencyMilliseconds: 4 },
        models: { url: `${node.baseAddress}/v1/models`, success: true, statusCode: 200, latencyMilliseconds: 5 },
        warmups: [{ url: `${node.baseAddress}/v1/chat/completions`, success: true, statusCode: 200, latencyMilliseconds: 8 }]
      })
    }

    if (method === 'POST' && path === `/api/admin/nodes/${node.id}/enable`) {
      return json(route, { error: 'legacy enable must not be used to exit Draining' }, 500)
    }

    if (method === 'POST' && path === `/api/admin/nodes/${node.id}/disable`) return route.fulfill({ status: 204, body: '' })
    return json(route, { error: `Unhandled maintenance test route ${method} ${path}` }, 500)
  })

  return {
    wasLegacyDrainCalled: () => legacyDrainCalled,
    wasResumeCalled: () => resumeCalled
  }
}

test('DGX node controls use safe maintenance drain and validated resume', async ({ page }) => {
  const state = await installMaintenanceApi(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'DGX Nodes' }).click()

  const row = page.getByRole('row').filter({ hasText: 'dgx-maintenance' })
  await row.getByRole('button', { name: 'Drain' }).click()
  await expect(row.getByText('Draining', { exact: true })).toBeVisible()
  expect(state.wasLegacyDrainCalled()).toBe(false)

  await row.getByRole('button', { name: 'Enable' }).click()
  await expect.poll(state.wasResumeCalled).toBe(true)
  await expect(row.getByText('Healthy', { exact: true })).toBeVisible()
  expect(state.wasLegacyDrainCalled()).toBe(false)
})
