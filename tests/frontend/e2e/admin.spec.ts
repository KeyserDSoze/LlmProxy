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
    if (request.method() === 'GET' && path === '/api/admin/models') return json(route, [
      { id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'bootstrap-model', supportsStreaming: true, supportsTools: true, surface: 'OpenAi', enabled: true },
      { id: 'model-systemone', publicName: 'systemone-laya', providerModelName: 'convaiinnovations/laya', supportsStreaming: false, supportsTools: false, surface: 'SystemOne', enabled: true }
    ])
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
    if (request.method() === 'GET' && path === '/api/admin/testing/systemone') return json(route, { enabled: true, baseAddress: 'http://classifier:8001', upstreamEndpoint: 'http://classifier:8001/v1/systemone', publicEndpoint: '/v1/systemone', apiKeyConfigured: true, timeoutSeconds: 30, configurationError: null, models: ['systemone-laya'], defaultModel: 'systemone-laya' })
    if (request.method() === 'POST' && path === '/api/admin/testing/chat') return json(route, { requestId: 'test-chat', success: true, statusCode: 200, latencyMilliseconds: 25, logicalModel: 'agic-code-fast', providerModel: 'bootstrap-model', nodeId: 'node-1', nodeName: 'inference-01', requestBody: request.postData() ?? '{}', responseBody: '{"choices":[{"message":{"content":"LlmProxy model test OK"}}]}' })
    if (request.method() === 'POST' && path === '/api/admin/testing/systemone') return json(route, { requestId: 'test-classifier', success: true, statusCode: 200, latencyMilliseconds: 13, logicalModel: 'systemone-laya', providerModel: 'convaiinnovations/laya', deploymentId: 'deployment-systemone', nodeId: 'node-1', nodeName: 'inference-01', requestBody: request.postData() ?? '{}', responseBody: '{"billing":true}' })
    if (request.method() === 'GET' && path === '/api/admin/content-logs/settings') return json(route, { retentionDays: 30, updatedAtUtc: '2026-09-09T10:00:00Z', minimumRetentionDays: 10, maximumRetentionDays: 4015, cleanupIntervalHours: 4 })
    if (request.method() === 'GET' && path === '/api/admin/content-logs/query') return json(route, { items: [{ id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', completedAtUtc: '2026-09-09T10:03:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200 }], total: 1, page: 1, pageSize: 50 })
    if (request.method() === 'GET' && path === '/api/admin/content-logs') return json(route, [{ id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', completedAtUtc: '2026-09-09T10:03:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200 }])
    if (request.method() === 'GET' && path === '/api/admin/content-logs/1') return json(route, { id: 1, requestId: 'request-1', startedAtUtc: '2026-09-09T10:03:00Z', completedAtUtc: '2026-09-09T10:03:01Z', surface: 'chat_completions', method: 'POST', path: '/v1/chat/completions', logicalModel: 'agic-code-fast', apiCredentialId: null, statusCode: 200, requestBody: '{"model":"agic-code-fast"}', responseBody: '{"ok":true}', nodeId: 'node-1', attemptCount: 1, timeToFirstByteMilliseconds: 49, errorCode: null })
    if (request.method() === 'PUT' && path === '/api/admin/content-logs/settings') return json(route, { retentionDays: (request.postDataJSON() as { retentionDays: number }).retentionDays, updatedAtUtc: '2026-09-09T10:06:00Z', minimumRetentionDays: 10, maximumRetentionDays: 4015, cleanupIntervalHours: 4 })
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
  await page.getByRole('button', { name: 'Servers & hardware' }).click()
  await page.getByRole('button', { name: 'Add hardware node' }).click()
  await page.getByLabel('Name').fill('inference-02'); await page.getByLabel('Base address / service root').fill('http://localhost:3451/altropath'); await page.getByLabel('Weight').fill('3'); await page.getByLabel('Max concurrency').fill('8'); await page.getByRole('button', { name: 'Add node' }).click()
  const row = page.getByRole('row').filter({ hasText: 'inference-02' }); await expect(row).toBeVisible(); await row.getByRole('button', { name: 'Test' }).click(); await expect(page.getByText('✓ Connection test: inference-02')).toBeVisible()
})

test('infrastructure exposes telemetry, physical capacity and explicit capacity profiles', async ({ page }) => {
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Servers & hardware' }).click()
  await expect(page.getByRole('heading', { name: 'Servers, agents & hardware', exact: true })).toBeVisible()
  await page.getByRole('tab', { name: /Capacity & telemetry/ }).click()
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
  await expect(page.getByRole('heading', { name: 'Physical concurrency' })).toBeVisible()
  await expect(page.getByText('429 capacity_exhausted')).toBeVisible()

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
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Request metrics' }).click()
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
  await expect(page.getByLabel('System One logical model')).toHaveValue('systemone-laya')
  await page.getByRole('button', { name: 'Run classifier test' }).click()
  await expect(page.getByRole('strong').filter({ hasText: /^systemone-laya$/ })).toBeVisible()

  await page.getByRole('button', { name: 'Request audit' }).click()
  await expect(page.getByRole('heading', { name: 'Request / response audit' })).toBeVisible()
  await page.getByRole('button', { name: 'Inspect' }).click()
  await expect(page.getByRole('heading', { name: 'Request detail' })).toBeVisible()
  await expect(page.getByText('agic-code-fast', { exact: true }).nth(1)).toBeVisible()
  await expect(page.getByText(/automatic cleanup every 4h/)).toBeVisible()
})


test('admin controls automatic versus manual end-user provisioning and can disable users', async ({ page }) => {
  await installAdminApi(page); await page.goto('/')
  await page.getByRole('button', { name: 'Users & access' }).click()
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
  await installAdminApi(page); await page.goto('/'); await page.getByRole('button', { name: 'Administrative audit' }).click(); await expect(page.getByRole('heading', { name: 'Administrative audit trail', exact: true, level: 1 })).toBeVisible(); await expect(page.getByText('admin@agic.it')).toBeVisible()
})

test('authentication failures surface the Entra ID sign-in action', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 401, body: '' })); await page.goto('/'); await expect(page.getByText('Authentication is required.')).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/auth/login')
})

test('authorization failures do not invite an authentication loop', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 403, body: '' })); await page.goto('/'); await expect(page.getByText(/Access denied/)).toBeVisible(); await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveCount(0)
})

test('admin navigation preserves top-level pages and infrastructure tabs across refresh and history', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Servers & hardware' }).click()
  await expect(page).toHaveURL(/\/admin\/infrastructure$/)
  await page.getByRole('tab', { name: /Hardware inventory & runtimes/ }).click()
  await expect(page).toHaveURL(/\/admin\/infrastructure\?section=inventory&node=node-1$/)
  await page.getByRole('tab', { name: /Deploy models/ }).click()
  await expect(page).toHaveURL(/\/admin\/infrastructure\?section=inventory&node=node-1&tab=deploy$/)
  await page.reload()
  await expect(page.getByRole('tab', { name: /Deploy models/ })).toHaveAttribute('aria-selected', 'true')
  await page.getByRole('button', { name: 'Routing', exact: true }).click()
  await expect(page).toHaveURL(/\/admin\/routing$/)
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Smart-routing tuning' })).toBeVisible()
  await page.goBack()
  await expect(page.getByRole('tab', { name: /Deploy models/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page).toHaveURL(/\/admin\/infrastructure\?section=inventory&node=node-1&tab=deploy$/)
})


test('selected hardware is a stable deep link across F5 and browser back', async ({ page }) => {
  await installAdminApi(page)
  const otherNode = {
    id: 'node-2', name: 'spark-02', baseAddress: 'http://127.0.0.1:1',
    hardwareMetricsBaseAddress: null, weight: 1, maxConcurrency: 4, enabled: true,
    status: 'Unhealthy', lastHealthCheckUtc: '2026-10-11T08:00:00Z',
    lastHealthyAtUtc: null, lastHealthLatencyMilliseconds: null,
    lastHealthError: 'No runtime', consecutiveHealthSuccesses: 0, consecutiveHealthFailures: 0
  }
  const originalNode = {
    ...otherNode, id: 'node-1', name: 'spark-01'
  }
  await page.route('**/api/admin/nodes', route => json(route, [originalNode, otherNode]))
  await page.route('**/api/admin/model-management/nodes/*/overview', route => {
    const nodeId = new URL(route.request().url()).pathname.split('/').at(-2)!
    return json(route, {
      node: { id: nodeId, managementBaseAddress: null },
      agentAvailable: false, inventoryStale: true, agentError: 'Agent offline',
      hardware: null, installations: [], catalog: []
    })
  })
  await page.route('**/api/admin/model-management/nodes/*/install-jobs',
    route => json(route, []))

  await page.goto('/admin/infrastructure?section=inventory&tab=deploy')
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-1')
  await expect(page).toHaveURL(/node=node-1/)
  await page.getByLabel('Managed hardware node').selectOption('node-2')
  await expect(page).toHaveURL(/section=inventory&tab=deploy&node=node-2$/)
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-2')
  await page.reload()
  await expect(page.getByRole('tab', { name: /Deploy models/ }))
    .toHaveAttribute('aria-selected', 'true')
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-2')

  await page.getByRole('button', { name: 'Routing', exact: true }).click()
  await expect(page).toHaveURL(/\/admin\/routing$/)
  await page.goBack()
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-2')
  await page.goBack()
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-1')
})

test('invalid hardware deep link does not silently install on another machine', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/admin/infrastructure?section=inventory&tab=deploy&node=deleted-node')
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('')
  await expect(page.getByText(/hardware selected in this link no longer exists/)).toBeVisible()
  await expect(page).toHaveURL(/node=deleted-node$/)
})


test('running AirLLM with disabled routing exposes health-gated recovery without restart', async ({ page }) => {
  await installAdminApi(page)
  let activated = false
  let activationCalls = 0
  let restartCalls = 0
  await page.route('**/api/admin/model-management/deployments/*/activate', route => {
    activationCalls++
    activated = true
    return json(route, { deployment: { id: 'deployment-airllm', enabled: true } })
  })
  await page.route('**/api/admin/model-management/deployments/*/start', route => {
    restartCalls++
    return json(route, {})
  })
  await page.route('**/api/admin/model-management/nodes/*/overview', route =>
    json(route, {
      node: { id: 'node-1', name: 'spark-test', managementBaseAddress: 'http://agent', hasManagementCredential: false },
      agentAvailable: true,
      hardware: { hostname: 'spark-test', cpuLogicalCores: 20, systemMemoryTotalGiB: 120,
        systemMemoryAvailableGiB: 45, diskTotalGiB: 1600, diskAvailableGiB: 1200,
        gpus: [], readiness: { dockerDaemonReady: true } },
      catalog: [],
      installations: [{
        id: 'deployment-airllm', modelId: 'model-airllm', logicalModel: 'qwen3-8b-airllm',
        providerModelName: 'Qwen/Qwen3-8B', catalogModelId: 'qwen3-8b-airllm',
        managedInstallationId: 'qwen3-8b-airllm-abc123', enabled: activated,
        agentStatus: 'running', runtimeBaseAddress: 'http://agent-xxx.llmproxy.invalid/runtime/18000',
        agentState: { status: 'running', runtime: 'airllm', maxNumSeqs: 4 }
      }]
    }))
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => json(route, []))
  await page.goto('/admin/infrastructure?section=inventory&tab=deploy&node=node-1')
  await expect(page.getByText('Container running, health and routing not yet verified.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Start', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Check health & activate' }).click()
  await expect(page.getByText('Runtime /health verified; existing model enabled for routing.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Check health & activate' })).toHaveCount(0)
  expect(activationCalls).toBe(1)
  expect(restartCalls).toBe(0)
})


test('AirLLM installation advertises real runtime capabilities and client limits', async ({ page }) => {
  await installAdminApi(page)
  const feature = (model: string, serving: string, explanation: string) =>
    ({ model, serving, explanation })
  const capabilities = {
    providerModelName: 'Qwen/Qwen3-8B', runtime: 'airllm',
    source: 'Qwen3 published model card + local adapter contract',
    modelCardUrl: 'https://huggingface.co/Qwen/Qwen3-8B',
    chat: feature('supported', 'supported', 'Text Chat API'),
    toolCalling: feature('supported', 'unsupported', 'AirLLM does not parse tools'),
    streaming: feature('not-applicable', 'unsupported', 'No SSE'),
    vision: feature('unsupported', 'unsupported', 'Text only'),
    thinking: feature('supported', 'partial', 'Raw think text; no structured reasoning'),
    nativeContextTokens: 32768, extendedContextTokens: 131072,
    servingContextTokens: 8192, maximumOutputTokens: 256,
    notes: 'Experimental one-worker AirLLM'
  }
  await page.route('**/api/admin/model-management/capabilities?*', route => json(route, capabilities))
  await page.route('**/api/admin/model-management/nodes/*/overview', route =>
    json(route, {
      node: { id: 'node-1', name: 'spark-test', managementBaseAddress: 'http://agent', hasManagementCredential: false },
      agentAvailable: true, agentError: null,
      hardware: { hostname: 'spark-test', cpuLogicalCores: 20, systemMemoryTotalGiB: 120,
        systemMemoryAvailableGiB: 45, diskTotalGiB: 1600, diskAvailableGiB: 1200,
        gpus: [], readiness: { dockerDaemonReady: true } },
      catalog: [{
        model: { id: 'qwen3-8b-airllm', displayName: 'Qwen3 8B — AirLLM',
          providerModelName: 'Qwen/Qwen3-8B', runtime: 'airllm',
          license: 'Apache-2.0', parameterBillions: 8, precision: 'BF16',
          contextTokens: 8192, minimumGpuMemoryGiB: 4, recommendedGpuMemoryGiB: 8,
          minimumGpuCount: 1, minimumSystemMemoryGiB: 24, diskGiB: 54,
          sourceUrl: 'https://huggingface.co/Qwen/Qwen3-8B', notes: 'Experimental' },
        compatibility: { status: 'fits', summary: 'Fits', suggestedTensorParallelSize: 1, reasons: [] },
        capabilities
      }],
      installations: [{
        id: 'deployment-airllm', modelId: 'model-airllm',
        logicalModel: 'qwen3-8b-airllm', providerModelName: 'Qwen/Qwen3-8B',
        catalogModelId: 'qwen3-8b-airllm', managedInstallationId: 'qwen3-airllm-test',
        agentStatus: 'running', runtime: 'airllm', enabled: true,
        agentState: { status: 'running', maxNumSeqs: 4, maxModelLen: 8192 },
        capabilities
      }]
    }))
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => json(route, []))
  await page.goto('/admin/infrastructure?section=inventory&tab=deploy&node=node-1')

  await page.getByRole('button', { name: 'Capabilities & client setup' }).click()
  const dialog = page.getByRole('dialog', { name: /Capabilities · qwen3-8b-airllm/ })
  await expect(dialog.getByText('Tool calling')).toBeVisible()
  await expect(dialog.getByText('AirLLM does not parse tools')).toBeVisible()
  await expect(dialog.locator('pre')).toContainText('"streaming": false')
  await expect(dialog.locator('pre')).toContainText('"maximumInputTokens": 8192')
  await expect(dialog.locator('pre')).toContainText('"maximumOutputTokens": 256')
  await dialog.getByRole('button', { name: /Close Capabilities/ }).click()

  await page.getByRole('button', { name: 'Install', exact: true }).click()
  await expect(page.getByText('Automatically detected capabilities & client token limits')).toBeVisible()
})


test('live HF discovery and saved AirLLM token limits preserve downloaded model files', async ({ page }) => {
  await installAdminApi(page)
  const deployments = [{
    id: 'deployment-airllm', modelId: 'model-airllm',
    logicalModel: 'qwen3-8b-airllm', providerModelName: 'Qwen/Qwen3-8B',
    catalogModelId: 'qwen3-8b-airllm', managedInstallationId: 'qwen3-airllm-abcdef',
    agentStatus: 'stopped', runtime: 'airllm', enabled: false,
    agentState: { status: 'stopped', runtime: 'airllm', maxNumSeqs: 4, maxModelLen: 8192,
      maxOutputTokens: 256, sourceRevision: 'aaaaabbbbbccccccdddddeeeeefffff0000011111' }
  }]
  const hubModels = [{
    id: 'Qwen/Qwen3-8B', revision: 'a'.repeat(40),
    lastModified: '2026-10-01T10:00:00Z', downloads: 2000, likes: 300,
    gated: false, private: false, license: 'apache-2.0',
    url: 'https://huggingface.co/Qwen/Qwen3-8B'
  }]
  let savedLimits: unknown = null
  await page.route('**/api/admin/model-management/hugging-face?*', route =>
    json(route, { source: 'Hugging Face Hub', verifiedForLocalRuntime: false,
      discoveredAtUtc: '2026-10-11T10:00:00Z', models: hubModels }))
  await page.route('**/api/admin/model-management/nodes/*/upstream-updates', route =>
    json(route, [{
      installationId: 'qwen3-airllm-abcdef', providerModelName: 'Qwen/Qwen3-8B',
      sourceRevision: 'aaaaabbbbbccccccdddddeeeeefffff0000011111',
      update: { updatedUpstream: true, message: 'A changed repository commit exists.' }
    }]))
  await page.route('**/api/admin/model-management/deployments/*/configuration', async route => {
    savedLimits = route.request().postDataJSON()
    await json(route, {})
  })
  await page.route('**/api/admin/model-management/nodes/*/overview', route =>
    json(route, {
      node: { id: 'node-1', name: 'spark-test', managementBaseAddress: 'http://agent' },
      agentAvailable: true,
      hardware: { hostname: 'spark-test', cpuLogicalCores: 20,
        systemMemoryTotalGiB: 120, systemMemoryAvailableGiB: 50,
        diskTotalGiB: 1600, diskAvailableGiB: 1000, gpus: [],
        readiness: { dockerDaemonReady: true } },
      catalog: [{
        model: {
          id: 'qwen3-8b-airllm', providerModelName: 'Qwen/Qwen3-8B',
          displayName: 'Qwen3 8B AirLLM', runtime: 'airllm', contextTokens: 32768,
          license: 'Apache-2.0', sourceUrl: 'https://huggingface.co/Qwen/Qwen3-8B',
          parameterBillions: 8, precision: 'BF16', minimumGpuMemoryGiB: 4,
          recommendedGpuMemoryGiB: 8, minimumGpuCount: 1,
          minimumSystemMemoryGiB: 24, diskGiB: 54, notes: 'Experimental'
        },
        compatibility: { status: 'fits', summary: 'Fits', suggestedTensorParallelSize: 1 }
      }],
      installations: deployments
    }))
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => json(route, []))
  await page.route('**/api/admin/model-management/capabilities?*', route => json(route, {
    providerModelName: 'Qwen/Qwen3-8B', runtime: 'airllm', maximumOutputTokens: 1024
  }))
  await page.goto('/admin/infrastructure?section=inventory&tab=deploy&node=node-1')
  await expect(page.getByRole('heading', { name: 'Hugging Face model discovery' })).toBeVisible()
  await expect(page.getByText('Qwen/Qwen3-8B', { exact: true }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Check Hugging Face updates' }).click()
  await expect(page.getByText('A changed repository commit exists.')).toBeVisible()
  await page.getByRole('button', { name: 'Edit limits' }).click()
  const edit = page.getByRole('dialog', { name: /Edit runtime limits/ })
  await edit.getByLabel('Edit runtime context').fill('16384')
  await edit.getByLabel('Edit maximum output tokens').fill('1024')
  await edit.getByLabel('Edit runtime sequences').fill('2')
  await edit.getByRole('button', { name: 'Save limits' }).click()
  await expect(page.getByText(/Runtime limits updated without deleting model weights/)).toBeVisible()
  expect(savedLimits).toEqual({ maxModelLen: 16384, maxNumSeqs: 2, maxOutputTokens: 1024 })
  await page.getByRole('button', { name: 'Install', exact: true }).click()
  const install = page.getByRole('dialog', { name: 'Install inference profile' })
  await expect(install.getByText(/Selected defaults:/)).toBeVisible()
  await expect(install.getByLabel('Runtime maximum context')).toHaveValue('8192')
  await expect(install.getByLabel('Install maximum output tokens')).toHaveValue('256')
})


test('task-based navigation exposes direct model installation and System One deep links', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/admin/dashboard')
  await expect(page.getByRole('heading', { name: 'What would you like to do?' })).toBeVisible()
  await expect(page.getByText('Models & inference', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Browse models' }).click()
  await expect(page).toHaveURL(/\/admin\/catalog/)
  await expect(page.getByRole('tab', { name: /Deploy models/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-1')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Install & manage models', exact: true, level: 1 })).toBeVisible()
  await expect(page.getByRole('tab', { name: /Deploy models/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByLabel('Managed hardware node')).toHaveValue('node-1')

  await page.getByRole('button', { name: 'System One classifiers' }).click()
  await expect(page).toHaveURL(/\/admin\/system-one$/)
  await expect(page.getByRole('heading', { name: 'Connect a System One runtime' })).toBeVisible()
  await expect(page.getByText(/Node Agent does not yet install Laya automatically/)).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Connect a System One runtime' })).toBeVisible()
  await page.goBack()
  await expect(page).toHaveURL(/\/admin\/catalog/)
  await expect(page.getByRole('tab', { name: /Deploy models/ })).toHaveAttribute('aria-selected', 'true')
})

test('guided System One connection creates only a classifier alias and a specified runtime route', async ({ page }) => {
  await installAdminApi(page)
  const modelsCreated: unknown[] = []
  const routesCreated: unknown[] = []
  await page.route('**/api/admin/models', route => {
    if (route.request().method() !== 'POST') return route.fallback()
    const body = route.request().postDataJSON()
    modelsCreated.push(body)
    return json(route, { ...body, id: 'new-systemone-model', enabled: true }, 201)
  })
  await page.route('**/api/admin/deployments', route => {
    if (route.request().method() !== 'POST') return route.fallback()
    const body = route.request().postDataJSON()
    routesCreated.push(body)
    return json(route, { ...body, id: 'new-classifier-deployment', enabled: true }, 201)
  })
  await page.goto('/admin/system-one')
  await page.getByLabel('System One logical name').fill('laya-on-spark')
  await page.getByLabel('System One provider name').fill('convaiinnovations/laya')
  await page.getByLabel('System One target node').selectOption('node-1')
  await page.getByLabel('System One service root').fill('http://10.0.0.21:8500')
  await page.getByLabel('System One concurrency').fill('2')
  await page.getByRole('button', { name: 'Connect and publish classifier' }).click()
  await expect(page.getByRole('status')).toContainText('route registered: "laya-on-spark"')
  expect(modelsCreated).toEqual([{
    publicName: 'laya-on-spark', providerModelName: 'convaiinnovations/laya',
    surface: 'SystemOne', supportsStreaming: false, supportsTools: false
  }])
  expect(routesCreated).toEqual([{
    modelId: 'new-systemone-model', nodeId: 'node-1',
    runtimeBaseAddress: 'http://10.0.0.21:8500', weight: 1, maxConcurrency: 2
  }])
})

test('System One does not offer unverified deploy-to-all routing or missing service roots', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/admin/models')
  const classifier = page.getByRole('row').filter({ hasText: 'systemone-laya' })
  await expect(classifier.getByRole('button', { name: 'Deploy to all active' })).toHaveCount(0)
  await expect(classifier.getByRole('link', { name: 'Connect classifier' })).toHaveAttribute('href', '/admin/system-one')
  await classifier.getByRole('button', { name: 'Deploy…' }).click()
  const form = page.getByRole('dialog', { name: /Deploy · systemone-laya/ })
  await form.getByRole('combobox').selectOption('node-1')
  await expect(form.getByRole('button', { name: 'Create deployment' })).toBeDisabled()
  await form.getByLabel('Runtime service root').fill('http://10.0.0.21:8500')
  await expect(form.getByRole('button', { name: 'Create deployment' })).toBeEnabled()
})


test('responsive Admin navigation fits a phone, remains keyboard accessible and preserves the route', async ({ page }) => {
  await installAdminApi(page)
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/admin/dashboard')
  await expect(page.getByRole('heading', { name: 'Gateway dashboard' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Open navigation' })).toBeVisible()
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.getByRole('button', { name: 'Open navigation' }).click()
  await expect(page.getByRole('button', { name: 'Open navigation' })).toHaveAttribute('aria-expanded', 'true')
  const navigation = page.getByRole('navigation', { name: 'Admin navigation' })
  await navigation.getByRole('button', { name: 'System One classifiers' }).click()
  await expect(page).toHaveURL(/\/admin\/system-one$/)
  await expect(page.getByRole('heading', { name: 'Connect a System One runtime' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Open navigation' })).toHaveAttribute('aria-expanded', 'false')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Connect a System One runtime' })).toBeVisible()
  await page.getByRole('button', { name: 'Open navigation' }).click()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Open navigation' })).toHaveAttribute('aria-expanded', 'false')
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('Italian and light appearance survive reload and update browser language, with dark/theme switching', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/admin/dashboard')
  await page.getByRole('combobox', { name: 'Theme' }).last().selectOption('light')
  await expect.poll(() => page.evaluate(() => document.documentElement.dataset.theme)).toBe('light')
  await page.getByRole('combobox', { name: 'Language' }).last().selectOption('it')
  await expect(page.getByRole('heading', { name: 'Panoramica gateway' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Cosa vuoi fare?' })).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.lang)).toBe('it')
  await page.getByRole('button', { name: 'Classificatori System One' }).click()
  await expect(page.getByRole('heading', { name: 'Collega un runtime System One' })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Collega un runtime System One' })).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.dataset.theme)).toBe('light')
  await page.getByRole('combobox', { name: 'Tema' }).last().selectOption('dark')
  await expect.poll(() => page.evaluate(() => document.documentElement.dataset.theme)).toBe('dark')
  await page.getByRole('combobox', { name: 'Lingua' }).last().selectOption('en')
  await expect(page.getByRole('heading', { name: 'Connect a System One runtime' })).toBeVisible()
})

test('compact model-management and Admin tables do not force whole-page horizontal scrolling', async ({ page }) => {
  await installAdminApi(page)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.goto('/admin/models')
  await expect(page.getByRole('heading', { name: 'Models & deployments', exact: true, level: 1 })).toBeVisible()
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.goto('/admin/infrastructure?section=capacity')
  await expect(page.getByRole('tab', { name: /Capacity & telemetry/ })).toHaveAttribute('aria-selected', 'true')
  await expect.poll(() => page.evaluate(() =>
    document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})


test('Italian model discovery, deployment lifecycle and fleet pages use one persisted locale', async ({ page }) => {
  await installAdminApi(page)
  await page.route('**/api/admin/model-management/hugging-face?*', route => json(route, {
    source: 'Hugging Face Hub', discoveredAtUtc: '2026-10-11T18:00:00Z',
    verifiedForLocalRuntime: false, models: []
  }))
  await page.goto('/admin/catalog?tab=deploy&node=node-1')
  await page.getByRole('combobox', { name: 'Language' }).last().selectOption('it')
  await expect(page.getByRole('heading', { name: 'Catalogo modelli installabili' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Download e installazioni' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Modelli installati su questo hardware' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Scopri modelli Hugging Face' })).toBeVisible()
  await expect(page.getByRole('textbox', { name: 'Cerca modelli Hugging Face' })).toBeVisible()
  await page.goto('/admin/infrastructure?section=fleet')
  await expect(page.getByRole('heading', { name: 'Server, Agent e hardware' })).toBeVisible()
  await expect(page.getByRole('tab', { name: /Server e accesso/ })).toHaveAttribute('aria-selected', 'true')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Server, Agent e hardware' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Persone e accessi' })).toHaveCount(0)
  await expect(page.getByText('Concorrenza fisica')).toBeVisible()
  await expect.poll(() => page.evaluate(() => document.documentElement.lang)).toBe('it')
})


test('image and audio models are published as isolated API surfaces, never as Chat installations', async ({ page }) => {
  await installAdminApi(page)
  const created: Record<string, unknown>[] = []
  await page.route('**/api/admin/models', async route => {
    if (route.request().method() !== 'POST') return route.fallback()
    const body = route.request().postDataJSON() as Record<string, unknown>
    created.push(body)
    return json(route, { id: 'multimodal-created-' + created.length, ...body, enabled: true }, 201)
  })
  await page.goto('/admin/models')
  for (const [surface, model] of [
    ['ImageGeneration', 'image-demo'],
    ['SpeechSynthesis', 'voice-demo'],
    ['AudioTranscription', 'whisper-demo']
  ]) {
    await page.getByRole('button', { name: 'Publish logical model' }).click()
    const dialog = page.getByRole('dialog', { name: 'Publish logical model' })
    await dialog.getByLabel('Model surface').selectOption(surface)
    await expect(dialog.getByText(/does not download or launch an image\/audio runtime/)).toBeVisible()
    await dialog.getByPlaceholder('agic-code-fast').fill(model)
    await dialog.getByPlaceholder('Qwen/...').fill('org/' + model)
    await dialog.getByRole('button', { name: 'Publish model' }).click()
  }
  expect(created).toEqual([
    { publicName: 'image-demo', providerModelName: 'org/image-demo', surface: 'ImageGeneration',
      supportsStreaming: false, supportsTools: false },
    { publicName: 'voice-demo', providerModelName: 'org/voice-demo', surface: 'SpeechSynthesis',
      supportsStreaming: false, supportsTools: false },
    { publicName: 'whisper-demo', providerModelName: 'org/whisper-demo', surface: 'AudioTranscription',
      supportsStreaming: false, supportsTools: false }
  ])
})


test('curated CPU Whisper uses audio-only runtime settings during installation', async ({ page }) => {
  await installAdminApi(page)
  let queued: Record<string, unknown> | null = null
  await page.route('**/api/admin/model-management/nodes/*/overview', route =>
    json(route, {
      node: { id: 'node-1', name: 'spark-test', managementBaseAddress: 'http://agent' },
      agentAvailable: true,
      hardware: {
        hostname: 'spark-test', cpuLogicalCores: 20, systemMemoryTotalGiB: 120,
        systemMemoryAvailableGiB: 80, diskTotalGiB: 1200, diskAvailableGiB: 800,
        gpus: [], readiness: { dockerDaemonReady: true }
      },
      catalog: [{
        model: {
          id: 'whisper-tiny-cpu', displayName: 'Whisper Tiny — Speech-to-text (CPU)',
          providerModelName: 'Systran/faster-whisper-tiny',
          family: 'Whisper', runtime: 'faster-whisper', surface: 'AudioTranscription',
          license: 'MIT', sourceUrl: 'https://huggingface.co/Systran/faster-whisper-tiny',
          parameterBillions: 0.039, contextTokens: 448, precision: 'int8 CPU',
          minimumGpuMemoryGiB: 0, recommendedGpuMemoryGiB: 0, minimumGpuCount: 0,
          minimumSystemMemoryGiB: 2, diskGiB: 1, notes: 'CPU-only transcription',
          supportsTools: false, supportsStreaming: false
        },
        compatibility: { status: 'fits', summary: 'CPU has sufficient RAM', suggestedTensorParallelSize: 1, reasons: [] },
      }],
      installations: []
    }))
  await page.route('**/api/admin/model-management/nodes/*/install-jobs', route => {
    if (route.request().method() !== 'POST') return json(route, [])
    queued = route.request().postDataJSON() as Record<string, unknown>
    return json(route, { id: 'job-whisper', status: 'queued', stage: 'queued', percent: null,
      detail: 'Queued', request: { catalogModelId: 'whisper-tiny-cpu', runtime: 'faster-whisper' } })
  })
  await page.route('**/api/admin/model-management/capabilities?*', route =>
    json(route, {
      providerModelName: 'Systran/faster-whisper-tiny', runtime: 'faster-whisper',
      source: 'Whisper', modelCardUrl: 'https://huggingface.co/Systran/faster-whisper-tiny',
      chat: { model: 'unsupported', serving: 'unsupported', explanation: 'Audio only' },
      toolCalling: { model: 'unsupported', serving: 'unsupported', explanation: 'Audio only' },
      streaming: { model: 'unsupported', serving: 'unsupported', explanation: 'Audio only' },
      vision: { model: 'unsupported', serving: 'unsupported', explanation: 'Audio only' },
      thinking: { model: 'unsupported', serving: 'unsupported', explanation: 'Audio only' },
      audioTranscription: { model: 'supported', serving: 'supported', explanation: 'STT' },
      notes: 'CPU only'
    }))
  await page.goto('/admin/catalog?tab=deploy&node=node-1')
  const whisper = page.getByRole('row').filter({ hasText: 'Whisper Tiny — Speech-to-text (CPU)' })
  await expect(whisper.getByText('CPU-only')).toBeVisible()
  await expect(whisper.getByText('Audio ≤25 MiB')).toBeVisible()
  await whisper.getByRole('button', { name: 'Install' }).click()
  const modal = page.getByRole('dialog', { name: 'Install inference profile' })
  await expect(modal.getByRole('combobox', { name: 'Motore di inferenza' })).toHaveCount(0)
  await expect(modal.getByRole('combobox', { name: 'Install inference engine' })).toHaveValue('faster-whisper')
  await expect(modal.getByText('Speech-to-text / English translation (CPU)')).toBeVisible()
  await expect(modal.getByLabel('Runtime maximum context')).toHaveCount(0)
  await expect(modal.getByLabel('Tensor parallel size')).toHaveCount(0)
  await expect(modal.getByLabel('Install maximum output tokens')).toHaveCount(0)
  await modal.getByRole('button', { name: 'Download & install' }).click()
  await expect(page.getByText(/Background download scheduled/)).toBeVisible()
  expect(queued).toMatchObject({
    runtime: 'faster-whisper',
    maxNumSeqs: 1,
    maxModelLen: null,
    maxOutputTokens: null,
    gpuDevices: null,
    extraArguments: [],
    tensorParallelSize: 1
  })
})
