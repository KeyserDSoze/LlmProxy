import { expect, Page, Route, test } from '@playwright/test'

type NodeRecord = {
  id: string
  name: string
  baseAddress: string
  hardwareMetricsBaseAddress?: string | null
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

type DeploymentRecord = {
  id: string
  nodeId: string
  modelId: string
  enabled: boolean
  weight: number
  maxConcurrency: number | null
  recommendedMaxConcurrency: number | null
  benchmarkP95TtftMilliseconds: number | null
  benchmarkP95DurationMilliseconds: number | null
  sustainableOutputTokensPerSecond: number | null
  benchmarkSource: string | null
  benchmarkMeasuredAtUtc: string | null
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

async function installAdminApi(page: Page) {
  const nodes: NodeRecord[] = [{
    id: 'node-1', name: 'inference-01', baseAddress: 'http://10.0.0.21:8000/vllm', hardwareMetricsBaseAddress: 'http://10.0.0.21:9400/dcgm', weight: 1, maxConcurrency: 4, enabled: true, status: 'Healthy',
    lastHealthCheckUtc: '2026-09-09T10:00:00Z', lastHealthyAtUtc: '2026-09-09T10:00:00Z', lastHealthLatencyMilliseconds: 9, lastHealthError: null,
    consecutiveHealthSuccesses: 4, consecutiveHealthFailures: 0
  }]
  const deployments: DeploymentRecord[] = [{
    id: 'deployment-1', nodeId: 'node-1', modelId: 'model-1', enabled: true, weight: 1, maxConcurrency: 4,
    recommendedMaxConcurrency: null, benchmarkP95TtftMilliseconds: null, benchmarkP95DurationMilliseconds: null,
    sustainableOutputTokensPerSecond: null, benchmarkSource: null, benchmarkMeasuredAtUtc: null
  }]
  let routingStrategy = 'WeightedLeastLoaded'
  let tuning = {
    warmupSamples: 3, ttftTargetMilliseconds: 2000, ttftPenaltyWeight: 0.25, failurePenaltyWeight: 1.5,
    externalLoadPenaltyWeight: 0.4, queuePenaltyWeight: 0.75, kvCacheThreshold: 0.7, kvCachePenaltyWeight: 0.6,
    degradedNodePenalty: 0.35, unknownNodePenalty: 0.1, updatedAtUtc: '2026-09-09T10:04:00Z'
  }
  const hardware = [{
    nodeId: 'node-1', available: true, gpuCount: 2, averageGpuUtilizationPercent: 60, maxGpuUtilizationPercent: 80,
    framebufferUsedMiB: 4096, framebufferFreeMiB: 12288, framebufferUsageRatio: 0.25,
    maxTemperatureCelsius: 67, totalPowerUsageWatts: 261,
    collectedAtUtc: '2026-09-09T10:04:00Z', lastAttemptAtUtc: '2026-09-09T10:04:00Z', error: null
  }]
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

    if (request.method() === 'GET' && path === '/api/admin/identity/me') return json(route, { tenantId: 'tenant-1', objectId: 'admin-1', principalName: 'admin@example.com', displayName: 'Admin', roles: ['LlmProxy.Admin'], isAdmin: true })
    if (request.method() === 'GET' && path === '/api/admin/identity/users') return json(route, [{ tenantId: 'tenant-1', objectId: 'user-1', principalName: 'user@example.com', credentialCount: 2, activeCredentialCount: 1, lastUsedAtUtc: '2026-09-09T10:00:00Z', firstCredentialCreatedAtUtc: '2026-09-01T10:00:00Z' }])
    if (request.method() === 'GET' && path === '/api/admin/overview') return json(route, { nodes: { total: nodes.length, healthy: nodes.filter(item => item.status === 'Healthy').length, degraded: 0, unhealthy: 0, draining: 0 }, models: 1, deployments: deployments.length, activeRequests: 0, requestsToday: 12 })
    if (request.method() === 'GET' && path === '/api/admin/routing') return json(route, { strategy: routingStrategy, supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    if (request.method() === 'GET' && path === '/api/admin/routing/tuning') return json(route, tuning)
    if (request.method() === 'GET' && path === '/api/admin/routing/performance') return json(route, [{ deploymentId: 'deployment-1', sampleCount: 12, ewmaTimeToFirstByteMilliseconds: 140, ewmaDurationMilliseconds: 980, infrastructureFailureScore: 0.04, lastObservedAtUtc: '2026-09-09T10:04:00Z' }])
    if (request.method() === 'GET' && path === '/api/admin/routing/runtime') return json(route, [{ nodeId: 'node-1', available: true, modelName: 'bootstrap-model', runningRequests: 2, waitingRequests: 1, kvCacheUsageRatio: 0.55, promptTokensTotal: 1234, generationTokensTotal: 567, collectedAtUtc: '2026-09-09T10:04:00Z', lastAttemptAtUtc: '2026-09-09T10:04:00Z', error: null }])
    if (request.method() === 'GET' && path === '/api/admin/hardware') return json(route, hardware)
    if (request.method() === 'GET' && path === '/api/admin/capacity') return json(route, {
      nodes: nodes.map(node => ({ id: node.id, name: node.name, maxConcurrency: node.maxConcurrency, activeRequests: 0, remaining: node.maxConcurrency })),
      deployments: deployments.map(item => ({ ...item, effectiveMaxConcurrency: item.maxConcurrency ?? nodes.find(node => node.id === item.nodeId)!.maxConcurrency, activeRequests: 0 }))
    })

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
    if (request.method() === 'GET' && path === '/api/admin/deployments') return json(route, deployments)
    if (request.method() === 'GET' && path === '/api/admin/api-credentials') return json(route, [])
    if (request.method() === 'GET' && path === '/api/admin/metrics') return json(route, metrics)
    if (request.method() === 'GET' && path === '/api/admin/metrics/summary') return json(route, metricsSummary)
    if (request.method() === 'GET' && path === '/api/admin/audit') return json(route, audit)

    if (request.method() === 'POST' && path === '/api/admin/nodes') {
      const input = request.postDataJSON() as Pick<NodeRecord, 'name' | 'baseAddress' | 'weight' | 'maxConcurrency'>
      const created: NodeRecord = { id: `node-${nodes.length + 1}`, ...input, hardwareMetricsBaseAddress: null, enabled: true, status: 'Unknown', lastHealthCheckUtc: '2026-09-09T10:00:00Z', lastHealthyAtUtc: null, lastHealthLatencyMilliseconds: null, lastHealthError: null, consecutiveHealthSuccesses: 0, consecutiveHealthFailures: 0 }
      nodes.push(created)
      return json(route, created, 201)
    }

    const hardwareMatch = path.match(/^\/api\/admin\/nodes\/([^/]+)\/hardware-metrics$/)
    if (request.method() === 'PUT' && hardwareMatch) {
      const node = nodes.find(item => item.id === hardwareMatch[1])!
      const input = request.postDataJSON() as { baseAddress: string | null }
      node.hardwareMetricsBaseAddress = input.baseAddress?.replace(/\/$/, '') ?? null
      return json(route, { id: node.id, hardwareMetricsBaseAddress: node.hardwareMetricsBaseAddress })
    }

    const capacityMatch = path.match(/^\/api\/admin\/deployments\/([^/]+)\/capacity-profile$/)
    if (request.method() === 'PUT' && capacityMatch) {
      const deployment = deployments.find(item => item.id === capacityMatch[1])!
      const input = request.postDataJSON() as {
        recommendedMaxConcurrency: number
        p95TtftMilliseconds: number | null
        p95DurationMilliseconds: number | null
        sustainableOutputTokensPerSecond: number | null
        benchmarkSource: string
        measuredAtUtc: string
      }
      deployment.recommendedMaxConcurrency = input.recommendedMaxConcurrency
      deployment.benchmarkP95TtftMilliseconds = input.p95TtftMilliseconds
      deployment.benchmarkP95DurationMilliseconds = input.p95DurationMilliseconds
      deployment.sustainableOutputTokensPerSecond = input.sustainableOutputTokensPerSecond
      deployment.benchmarkSource = input.benchmarkSource
      deployment.benchmarkMeasuredAtUtc = input.measuredAtUtc
      return json(route, deployment)
    }
    if (request.method() === 'DELETE' && capacityMatch) {
      const deployment = deployments.find(item => item.id === capacityMatch[1])!
      deployment.recommendedMaxConcurrency = null
      deployment.benchmarkP95TtftMilliseconds = null
      deployment.benchmarkP95DurationMilliseconds = null
      deployment.sustainableOutputTokensPerSecond = null
      deployment.benchmarkSource = null
      deployment.benchmarkMeasuredAtUtc = null
      return route.fulfill({ status: 204, body: '' })
    }

    const applyCapacityMatch = path.match(/^\/api\/admin\/deployments\/([^/]+)\/capacity-profile\/apply$/)
    if (request.method() === 'POST' && applyCapacityMatch) {
      const deployment = deployments.find(item => item.id === applyCapacityMatch[1])!
      deployment.maxConcurrency = deployment.recommendedMaxConcurrency
      return json(route, deployment)
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
  await expect(page.getByText('inference-01')).toBeVisible(); await expect(page.getByText('9 ms')).toBeVisible(); await expect(page.getByText('99.2%')).toBeVisible()
  await page.getByRole('button', { name: 'Inference Nodes' }).click()
  await page.getByLabel('Name').fill('inference-02'); await page.getByLabel('Base address / service root').fill('http://localhost:3451/altropath'); await page.getByLabel('Weight').fill('3'); await page.getByLabel('Max concurrency').fill('8'); await page.getByRole('button', { name: 'Add node' }).click()
  const row = page.getByRole('row').filter({ hasText: 'inference-02' }); await expect(row).toBeVisible(); await row.getByRole('button', { name: 'Test' }).click(); await expect(page.getByText('✓ Connection test: inference-02')).toBeVisible()
})

test('hardware view exposes telemetry, physical capacity and explicit capacity profiles', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Hardware' }).click()
  await expect(page.getByRole('heading', { name: 'Hardware', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Hardware telemetry' })).toBeVisible()
  await expect(page.getByText('60.0% avg · 80.0% max')).toBeVisible()
  await expect(page.getByText('4.0 GiB used · 25.0%')).toBeVisible()
  await expect(page.getByRole('cell', { name: '67 °C' })).toBeVisible()
  await expect(page.getByText('261 W')).toBeVisible()
  await expect(page.getByText('Routing isolation')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Physical node capacity' })).toBeVisible()
  await expect(page.getByText('HTTP 429 · Retry-After: 1')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Benchmark capacity profiles' })).toBeVisible()

  await page.getByLabel('Hardware metrics service root').fill('http://10.0.0.21:9400/new-dcgm/')
  await page.getByRole('button', { name: 'Save endpoint' }).click()
  await expect(page.getByText('Hardware telemetry configuration updated.')).toBeVisible()

  await page.getByLabel('Benchmark source').fill('benchmark-results/run-001.json')
  await page.getByLabel('Capacity P95 TTFT').fill('420')
  await page.getByRole('button', { name: 'Save recommendation' }).click()
  await expect(page.getByText('Capacity recommendation saved. Active production limits were not changed.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Apply recommended' })).toBeEnabled()
  await page.getByRole('button', { name: 'Apply recommended' }).click()
  await expect(page.getByText('Recommended deployment capacity applied explicitly.')).toBeVisible()
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

test('administrator navigation exposes user management and release notes at the end', async ({ page }) => {
  await installAdminApi(page); await page.goto('/')
  await expect(page.getByRole('button', { name: 'User Management' })).toBeVisible()
  await page.getByRole('button', { name: 'User Management' }).click()
  await expect(page.getByRole('heading', { name: 'User inventory' })).toBeVisible()
  await expect(page.getByText('user@example.com')).toBeVisible()
  await expect(page.getByRole('button', { name: /Release Notes/ })).toBeVisible()
})

test('authentication failures surface the Entra ID sign-in action', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 401, body: '' })); await page.goto('/'); await expect(page.getByText('Authentication is required.')).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/auth/login')
})

test('authorization failures do not invite an authentication loop', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 403, body: '' })); await page.goto('/'); await expect(page.getByText(/Access denied/)).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveCount(0)
})
