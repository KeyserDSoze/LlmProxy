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
  let userProvisioningMode: 'automatic' | 'manual' = 'manual'
  const usageGroups = [{ id: 'group-1', name: 'Development CRM', description: 'CRM team', createdAtUtc: '2026-10-03T06:00:00Z', updatedAtUtc: '2026-10-03T06:00:00Z', credentialCount: 1, userCount: 1 }]
  const platformUsers = [{
    id: 'user-1',
    tenantId: 'tenant-1',
    objectId: 'object-1',
    principalName: 'user@example.com',
    displayName: 'Example User',
    usageGroupId: 'group-1',
    usageGroupName: 'Development CRM',
    enabled: true,
    provisioningSource: 'admin',
    createdAtUtc: '2026-10-03T06:00:00Z',
    lastSeenAtUtc: '2026-10-03T06:20:00Z',
    disabledAtUtc: null as string | null,
    credentialCount: 1,
    activeCredentialCount: 1,
    lastCredentialUsedAtUtc: '2026-10-03T06:19:00Z',
    requestCount30d: 12,
    errorCount30d: 1
  }]

  const metricsSummary = {    windowHours: 24, sinceUtc: '2026-09-08T10:00:00Z', requestCount: 125, successCount: 124, errorCount: 1, successRatePercent: 99.2,
    p50DurationMilliseconds: 900, p95DurationMilliseconds: 1800, p50TimeToFirstByteMilliseconds: 120, p95TimeToFirstByteMilliseconds: 350,
    averageUpstreamHeaderMilliseconds: 40, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, tokenObservedRequests: 100, failoverRequests: 2, streamingRequests: 90,
    byModel: [{ logicalModel: 'agic-code-fast', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, averageTimeToFirstByteMilliseconds: 120, outputTokens: 500 }],
    byNode: [{ nodeId: 'node-1', requestCount: 125, errorCount: 1, averageDurationMilliseconds: 900, p95DurationMilliseconds: 1800, outputTokens: 500 }]
  }

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname

    if (request.method() === 'GET' && path === '/api/admin/session') return json(route, { canWrite: true, roles: ['LlmProxy.Admin'] })
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
    if (request.method() === 'GET' && path === '/api/admin/metrics/query') return json(route, { items: metrics, total: metrics.length, page: 1, pageSize: 20 })
    if (request.method() === 'GET' && path === '/api/admin/metrics/summary') return json(route, metricsSummary)
    if (request.method() === 'GET' && path === '/api/admin/audit') return json(route, audit)
    if (request.method() === 'GET' && path === '/api/admin/usage-groups') return json(route, usageGroups)
    if (request.method() === 'GET' && path === '/api/admin/users/settings') return json(route, { provisioningMode: userProvisioningMode, updatedAtUtc: '2026-10-03T06:00:00Z', configuredTenantId: 'tenant-1' })
    if (request.method() === 'PUT' && path === '/api/admin/users/settings') {
      userProvisioningMode = (request.postDataJSON() as { provisioningMode: 'automatic' | 'manual' }).provisioningMode
      return json(route, { provisioningMode: userProvisioningMode, updatedAtUtc: '2026-10-03T06:30:00Z', configuredTenantId: 'tenant-1' })
    }
    if (request.method() === 'GET' && path === '/api/admin/users') return json(route, platformUsers)
    if (request.method() === 'POST' && path === '/api/admin/users') {
      const input = request.postDataJSON() as { tenantId?: string | null; objectId: string; principalName?: string | null; displayName?: string | null }
      const created = { id: 'user-2', tenantId: input.tenantId ?? 'tenant-1', objectId: input.objectId, principalName: input.principalName ?? null, displayName: input.displayName ?? null, enabled: true, provisioningSource: 'admin', createdAtUtc: '2026-10-03T06:31:00Z', lastSeenAtUtc: null, disabledAtUtc: null, credentialCount: 0, activeCredentialCount: 0, lastCredentialUsedAtUtc: null, requestCount30d: 0, errorCount30d: 0 }
      platformUsers.push(created)
      return json(route, created)
    }
    const userGroup = path.match(/^\/api\/admin\/users\/([^/]+)\/usage-group$/)
    if (request.method() === 'PUT' && userGroup) {
      const user = platformUsers.find(item => item.id === userGroup[1])!
      const input = request.postDataJSON() as { usageGroupId: string | null }
      user.usageGroupId = input.usageGroupId
      user.usageGroupName = usageGroups.find(item => item.id === input.usageGroupId)?.name ?? null
      return route.fulfill({ status: 204, body: '' })
    }
    const userDisable = path.match(/^\/api\/admin\/users\/([^/]+)\/disable$/)
    if (request.method() === 'POST' && userDisable) {
      const user = platformUsers.find(item => item.id === userDisable[1])!
      user.enabled = false
      user.disabledAtUtc = '2026-10-03T06:32:00Z'
      user.activeCredentialCount = 0
      return route.fulfill({ status: 204, body: '' })
    }
    const userEnable = path.match(/^\/api\/admin\/users\/([^/]+)\/enable$/)
    if (request.method() === 'POST' && userEnable) {
      const user = platformUsers.find(item => item.id === userEnable[1])!
      user.enabled = true
      user.disabledAtUtc = null
      return route.fulfill({ status: 204, body: '' })
    }
    if (request.method() === 'GET' && path === '/api/admin/testing/systemone') return json(route, { enabled: true, baseAddress: 'http://classifier:8001', upstreamEndpoint: 'http://classifier:8001/v1/systemone', publicEndpoint: '/v1/systemone', apiKeyConfigured: true, timeoutSeconds: 30, configurationError: null })
    if (request.method() === 'POST' && path === '/api/admin/testing/chat') return json(route, { requestId: 'test-chat', success: true, statusCode: 200, latencyMilliseconds: 25, logicalModel: 'agic-code-fast', providerModel: 'bootstrap-model', nodeId: 'node-1', nodeName: 'inference-01', requestBody: request.postData() ?? '{}', responseBody: '{"choices":[{"message":{"content":"LlmProxy model test OK"}}]}' })
    if (request.method() === 'POST' && path === '/api/admin/testing/systemone') return json(route, { requestId: 'test-classifier', success: true, statusCode: 200, latencyMilliseconds: 13, requestBody: request.postData() ?? '{}', responseBody: '{"billing":true}' })
    if (request.method() === 'GET' && path === '/api/admin/content-logs/settings') return json(route, { retentionDays: 30, updatedAtUtc: '2026-09-09T10:00:00Z', minimumRetentionDays: 10, maximumRetentionDays: 180, cleanupIntervalHours: 4 })
    if (request.method() === 'GET' && path === '/api/admin/content-logs') return json(route, [{ id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', completedAtUtc: '2026-09-09T10:03:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200 }])
    if (request.method() === 'GET' && path === '/api/admin/content-logs/1') return json(route, { id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', completedAtUtc: '2026-09-09T10:03:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200, requestBody: '{"model":"agic-code-fast"}', responseBody: '{"ok":true}', nodeId: 'node-1', attemptCount: 1, timeToFirstByteMilliseconds: 49, errorCode: null })
    if (request.method() === 'PUT' && path === '/api/admin/content-logs/settings') return json(route, { retentionDays: (request.postDataJSON() as { retentionDays: number }).retentionDays, updatedAtUtc: '2026-09-09T10:06:00Z', minimumRetentionDays: 10, maximumRetentionDays: 180, cleanupIntervalHours: 4 })
    if (request.method() === 'POST' && path === '/api/admin/content-logs/retention/run') return json(route, { startedAtUtc: '2026-09-09T10:06:00Z', completedAtUtc: '2026-09-09T10:06:01Z', retentionDays: 30, cutoffUtc: '2026-08-10T10:06:00Z', deletedLogs: 2 })

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
  await page.getByRole('button', { name: 'Add inference node' }).click()
  await page.getByLabel('Name').fill('inference-02'); await page.getByLabel('Base address / service root').fill('http://localhost:3451/altropath'); await page.getByLabel('Weight').fill('3'); await page.getByLabel('Max concurrency').fill('8'); await page.getByRole('button', { name: 'Add node' }).click()
  const row = page.getByRole('row').filter({ hasText: 'inference-02' }); await expect(row).toBeVisible(); await row.getByRole('button', { name: 'Test' }).click(); await expect(page.getByText('✓ Connection test: inference-02')).toBeVisible()
})

test('hardware view exposes telemetry, physical capacity and explicit capacity profiles', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Hardware', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Hardware', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Hardware telemetry', exact: true })).toBeVisible()
  await expect(page.getByText('60.0% avg · 80.0% max')).toBeVisible()
  await expect(page.getByText('4.0 GiB used · 25.0%')).toBeVisible()
  await expect(page.getByRole('cell', { name: '67 °C' })).toBeVisible()
  await expect(page.getByText('261 W')).toBeVisible()
  await expect(page.getByText('Routing isolation')).toBeVisible()

  await page.getByRole('button', { name: 'Configure telemetry endpoint' }).click()
  await page.getByLabel('Hardware metrics service root').fill('http://10.0.0.21:9400/new-dcgm/')
  await page.getByRole('button', { name: 'Save endpoint' }).click()

  await page.getByRole('tab', { name: /Physical capacity/ }).click()
  await expect(page.getByRole('heading', { name: 'Physical node capacity' })).toBeVisible()
  await expect(page.getByText('HTTP 429 · Retry-After: 1')).toBeVisible()

  await page.getByRole('tab', { name: /Benchmark profiles/ }).click()
  await expect(page.getByRole('heading', { name: 'Benchmark capacity profiles' })).toBeVisible()
  await page.getByRole('button', { name: 'Manage capacity recommendation' }).click()
  await page.getByLabel('Benchmark source').fill('benchmark-results/run-001.json')
  await page.getByLabel('Capacity P95 TTFT').fill('420')
  await page.getByRole('button', { name: 'Save recommendation' }).click()
  await expect(page.getByText('Capacity recommendation saved. Active production limits were not changed.')).toBeVisible()
  await page.getByRole('button', { name: 'Manage capacity recommendation' }).click()
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


test('admin playground tests models and System One and full-body logs are inspectable', async ({ page }) => {
  await installAdminApi(page); await page.goto('/')
  await page.getByRole('button', { name: 'Playground' }).click()
  await expect(page.getByRole('heading', { name: 'Model chat test' })).toBeVisible()
  await page.getByRole('button', { name: 'Run chat test' }).click()
  await expect(page.getByText('HTTP 200')).toBeVisible()
  await page.getByRole('tab', { name: 'System One classifier' }).click()
  await expect(page.getByRole('heading', { name: 'System One classifier' })).toBeVisible()

  await page.getByRole('button', { name: 'Content Logs' }).click()
  await expect(page.getByRole('heading', { name: 'Live request / response log' })).toBeVisible()
  await page.getByRole('button', { name: 'Inspect' }).click()
  await expect(page.getByRole('heading', { name: 'Request detail' })).toBeVisible()
  await expect(page.getByText('agic-code-fast', { exact: true }).nth(1)).toBeVisible()
  await expect(page.getByText(/automatic every 4h/)).toBeVisible()
})


test('admin controls automatic versus manual end-user provisioning and can disable users', async ({ page }) => {
  await installAdminApi(page); await page.goto('/')
  await page.getByRole('button', { name: 'Users & Access' }).click()
  await expect(page.getByText('Example User')).toBeVisible()
  await page.getByRole('tab', { name: 'Provisioning & identity' }).click()
  await expect(page.getByRole('heading', { name: 'User provisioning policy' })).toBeVisible()
  await page.getByLabel('Provisioning mode').selectOption('automatic')
  await page.getByRole('button', { name: 'Save provisioning mode' }).click()
  await expect(page.getByText(/Automatic provisioning enabled/)).toBeVisible()
  await page.getByRole('tab', { name: /Users/ }).click()
  await page.getByRole('button', { name: 'Disable user' }).click()
  await expect(page.getByRole('button', { name: 'Enable user' })).toBeVisible()
  await expect(page.getByText(/all currently active personal API keys were revoked/)).toBeVisible()
})

test('audit trail is visible to administrators', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Audit Trail' }).click(); await expect(page.getByRole('heading', { name: 'Audit trail', exact: true })).toBeVisible(); await expect(page.getByText('admin@agic.it')).toBeVisible()
})

test('authentication failures surface the Entra ID sign-in action', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 401, body: '' })); await page.goto('/'); await expect(page.getByText('Authentication is required.')).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/auth/login')
})

test('authorization failures do not invite an authentication loop', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 403, body: '' })); await page.goto('/'); await expect(page.getByText(/Access denied/)).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveCount(0)
})
