import { expect, Page, Route, test } from '@playwright/test'

type NodeRecord = {
  id: string
  name: string
  baseAddress: string
  weight: number
  maxConcurrency: number
  enabled: boolean
  status: string
  lastHealthCheckUtc: string
  lastHealthyAtUtc: string | null
  lastHealthLatencyMilliseconds: number | null
  lastHealthError: string | null
  consecutiveHealthSuccesses: number
  consecutiveHealthFailures: number
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function installAdminApi(page: Page) {
  const nodes: NodeRecord[] = [{
    id: 'node-1', name: 'dgx-01', baseAddress: 'http://10.0.0.21:8000/vllm', weight: 1, maxConcurrency: 4, enabled: true, status: 'Healthy',
    lastHealthCheckUtc: '2026-09-09T10:00:00Z', lastHealthyAtUtc: '2026-09-09T10:00:00Z', lastHealthLatencyMilliseconds: 9, lastHealthError: null,
    consecutiveHealthSuccesses: 4, consecutiveHealthFailures: 0
  }]
  let routingStrategy = 'WeightedLeastLoaded'
  let tuning = {
    warmupSamples: 3, ttftTargetMilliseconds: 2000, ttftPenaltyWeight: 0.25, failurePenaltyWeight: 1.5,
    externalLoadPenaltyWeight: 0.4, queuePenaltyWeight: 0.75, kvCacheThreshold: 0.7, kvCachePenaltyWeight: 0.6,
    degradedNodePenalty: 0.35, unknownNodePenalty: 0.1, updatedAtUtc: '2026-09-09T10:04:00Z'
  }
  const audit = [{ id: 1, occurredAtUtc: '2026-09-09T10:05:00Z', actor: 'admin@agic.it', action: 'routing.update', entityType: 'routing_policy', entityId: '1', sourceIp: '10.0.0.5', detailsJson: '{}' }]
  const metrics = [{ id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', logicalModel: 'agic-code-fast', surface: 'chat_completions', deploymentId: 'deployment-1', nodeId: 'node-1', apiCredentialId: null, statusCode: 200, durationMilliseconds: 1047, attemptCount: 2, isStreaming: true, upstreamHeaderMilliseconds: 38, timeToFirstByteMilliseconds: 49, inputTokens: 17, outputTokens: 6, totalTokens: 23, errorCode: null }]
  const metricsSummary = {
    windowHours: 24, sinceUtc: '2026-09-08T10:00:00Z', requestCount: 125, successCount: 124, errorCount: 1, successRatePercent: 99.2,
    p50DurationMilliseconds: 900, p95DurationMilliseconds: 1800, p50TimeToFirstByteMilliseconds: 120, p95TimeToFirstByteMilliseconds: 350,
    averageUpstreamHeaderMilliseconds: 40, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, tokenObservedRequests: 100, failoverRequests: 2, streamingRequests: 90,
    byModel: [{ logicalModel: 'agic-code-fast', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, averageTimeToFirstByteMilliseconds: 120, outputTokens: 500 }],
    byNode: [{ nodeId: 'node-1', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, p95DurationMilliseconds: 1800, outputTokens: 500 }]
  }

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname

    if (request.method() === 'GET' && path === '/api/admin/overview') return json(route, { nodes: { total: nodes.length, healthy: nodes.filter(item => item.status === 'Healthy').length, degraded: 0, unhealthy: 0, draining: 0 }, models: 1, deployments: 1, activeRequests: 0, requestsToday: 12 })
    if (request.method() === 'GET' && path === '/api/admin/routing') return json(route, { strategy: routingStrategy, supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    if (request.method() === 'GET' && path === '/api/admin/routing/tuning') return json(route, tuning)
    if (request.method() === 'GET' && path === '/api/admin/routing/performance') return json(route, [{ deploymentId: 'deployment-1', sampleCount: 12, ewmaTimeToFirstByteMilliseconds: 140, ewmaDurationMilliseconds: 980, infrastructureFailureScore: 0.04, lastObservedAtUtc: '2026-09-09T10:04:00Z' }])
    if (request.method() === 'GET' && path === '/api/admin/routing/runtime') return json(route, [{ nodeId: 'node-1', available: true, modelName: 'bootstrap-model', runningRequests: 2, waitingRequests: 1, kvCacheUsageRatio: 0.55, promptTokensTotal: 1234, generationTokensTotal: 567, collectedAtUtc: '2026-09-09T10:04:00Z', lastAttemptAtUtc: '2026-09-09T10:04:00Z', error: null }])

    if (request.method() === 'PUT' && path === '/api/admin/routing') {
      routingStrategy = (request.postDataJSON() as { strategy: string }).strategy
      return json(route, { strategy: routingStrategy, supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    }
    if (request.method() === 'PUT' && path === '/api/admin/routing/tuning') {
      tuning = { ...(request.postDataJSON() as typeof tuning), updatedAtUtc: '2026-09-09T10:06:00Z' }
      return json(route, tuning)
    }

    if (request.method() === 'GET' && path === '/api/admin/nodes') return json(route, nodes)
    if (request.method() === 'GET' && path === '/api/admin/models') return json(route, [{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'bootstrap-model', supportsStreaming: true, supportsTools: true, enabled: true }])
    if (request.method() === 'GET' && path === '/api/admin/deployments') return json(route, [{ id: 'deployment-1', nodeId: 'node-1', modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4 }])
    if (request.method() === 'GET' && path === '/api/admin/api-credentials') return json(route, [])
    if (request.method() === 'GET' && path === '/api/admin/metrics') return json(route, metrics)
    if (request.method() === 'GET' && path === '/api/admin/metrics/summary') return json(route, metricsSummary)
    if (request.method() === 'GET' && path === '/api/admin/audit') return json(route, audit)

    if (request.method() === 'POST' && path === '/api/admin/nodes') {
      const input = request.postDataJSON() as Pick<NodeRecord, 'name' | 'baseAddress' | 'weight' | 'maxConcurrency'>
      const created: NodeRecord = { id: `node-${nodes.length + 1}`, ...input, enabled: true, status: 'Unknown', lastHealthCheckUtc: '2026-09-09T10:00:00Z', lastHealthyAtUtc: null, lastHealthLatencyMilliseconds: null, lastHealthError: null, consecutiveHealthSuccesses: 0, consecutiveHealthFailures: 0 }
      nodes.push(created)
      return json(route, created, 201)
    }

    const testMatch = path.match(/^\/api\/admin\/nodes\/([^/]+)\/test-connection$/)
    if (request.method() === 'POST' && testMatch) {
      const node = nodes.find(item => item.id === testMatch[1])!
      const root = node.baseAddress.replace(/\/$/, '')
      return json(route, { nodeId: node.id, nodeName: node.name, serviceRoot: root, healthUrl: `${root}/health`, modelsUrl: `${root}/v1/models`, chatCompletionsUrl: `${root}/v1/chat/completions`, responsesUrl: `${root}/v1/responses`, success: true, health: { url: `${root}/health`, success: true, statusCode: 200, latencyMilliseconds: 9 }, openAi: { url: `${root}/v1/models`, success: true, statusCode: 200, latencyMilliseconds: 11 } })
    }

    return json(route, { error: `Unhandled test route ${request.method()} ${path}` }, 500)
  })
}

test('admin can inspect health, observability, add a path-prefixed node and test it', async ({ page }) => {
  await installAdminApi(page); await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Gateway dashboard' })).toBeVisible()
  await expect(page.getByText('dgx-01')).toBeVisible(); await expect(page.getByText('9 ms')).toBeVisible(); await expect(page.getByText('99.2%')).toBeVisible()
  await page.getByRole('button', { name: 'DGX Nodes' }).click()
  await page.getByLabel('Name').fill('dgx-02'); await page.getByLabel('Base address / service root').fill('http://localhost:3451/altropath'); await page.getByLabel('Weight').fill('3'); await page.getByLabel('Max concurrency').fill('8'); await page.getByRole('button', { name: 'Add node' }).click()
  await expect(page.getByText('dgx-02')).toBeVisible(); const row = page.getByRole('row').filter({ hasText: 'dgx-02' }); await row.getByRole('button', { name: 'Test' }).click(); await expect(page.getByText('✓ Connection test: dgx-02')).toBeVisible()
})

test('inference observability exposes latency, token and failover telemetry', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Request Metrics' }).click()
  await expect(page.getByRole('heading', { name: 'Inference observability', exact: true })).toBeVisible(); await expect(page.getByText('Chat Completions · SSE')).toBeVisible(); await expect(page.getByText('2 · failover')).toBeVisible()
})

test('routing strategy, smart tuning and live vLLM pressure are editable', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Routing' }).click()
  await expect(page.getByRole('heading', { name: 'Smart-routing tuning' })).toBeVisible(); await expect(page.getByRole('heading', { name: 'Live vLLM capacity' })).toBeVisible(); await expect(page.getByText('55.0%')).toBeVisible()
  await page.getByLabel('TTFT target').fill('1500'); await page.getByRole('button', { name: 'Apply smart-routing tuning' }).click(); await expect(page.getByText('Tuning updated live.')).toBeVisible()
  await page.getByLabel('Routing strategy').selectOption('WeightedRoundRobin'); await page.getByRole('button', { name: 'Apply routing strategy' }).click(); await expect(page.getByText('Routing policy updated live.')).toBeVisible()
})

test('audit trail is visible to administrators', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Audit Trail' }).click(); await expect(page.getByRole('heading', { name: 'Audit trail', exact: true })).toBeVisible(); await expect(page.getByText('admin@agic.it')).toBeVisible()
})

test('authentication failures surface the Entra ID sign-in action', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 401, body: '' })); await page.goto('/'); await expect(page.getByText('Authentication is required.')).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/auth/login')
})
