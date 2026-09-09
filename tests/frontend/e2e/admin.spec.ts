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
}

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({
    status,
    contentType: 'application/json',
    body: JSON.stringify(body)
  })
}

async function installAdminApi(page: Page) {
  const nodes: NodeRecord[] = [{
    id: 'node-1',
    name: 'dgx-01',
    baseAddress: 'http://10.0.0.21:8000/vllm',
    weight: 1,
    maxConcurrency: 4,
    enabled: true,
    status: 'Healthy',
    lastHealthCheckUtc: '2026-09-09T10:00:00Z'
  }]
  let routingStrategy = 'WeightedLeastLoaded'

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname

    if (request.method() === 'GET' && path === '/api/admin/overview') {
      return json(route, {
        nodes: { total: nodes.length, healthy: nodes.filter(item => item.status === 'Healthy').length, unhealthy: 0, draining: 0 },
        models: 1,
        deployments: 1,
        activeRequests: 0,
        requestsToday: 12
      })
    }

    if (request.method() === 'GET' && path === '/api/admin/routing') {
      return json(route, { strategy: routingStrategy, supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    }

    if (request.method() === 'PUT' && path === '/api/admin/routing') {
      routingStrategy = (request.postDataJSON() as { strategy: string }).strategy
      return json(route, { strategy: routingStrategy, supportedStrategies: ['WeightedLeastLoaded', 'RoundRobin', 'WeightedRoundRobin'] })
    }

    if (request.method() === 'GET' && path === '/api/admin/nodes') return json(route, nodes)
    if (request.method() === 'GET' && path === '/api/admin/models') return json(route, [])
    if (request.method() === 'GET' && path === '/api/admin/deployments') return json(route, [])
    if (request.method() === 'GET' && path === '/api/admin/api-credentials') return json(route, [])
    if (request.method() === 'GET' && path === '/api/admin/metrics') return json(route, [])

    if (request.method() === 'POST' && path === '/api/admin/nodes') {
      const input = request.postDataJSON() as Pick<NodeRecord, 'name' | 'baseAddress' | 'weight' | 'maxConcurrency'>
      const created: NodeRecord = {
        id: `node-${nodes.length + 1}`,
        ...input,
        enabled: true,
        status: 'Unknown',
        lastHealthCheckUtc: '2026-09-09T10:00:00Z'
      }
      nodes.push(created)
      return json(route, created, 201)
    }

    const testMatch = path.match(/^\/api\/admin\/nodes\/([^/]+)\/test-connection$/)
    if (request.method() === 'POST' && testMatch) {
      const node = nodes.find(item => item.id === testMatch[1])!
      const root = node.baseAddress.replace(/\/$/, '')
      return json(route, {
        nodeId: node.id,
        nodeName: node.name,
        serviceRoot: root,
        healthUrl: `${root}/health`,
        modelsUrl: `${root}/v1/models`,
        chatCompletionsUrl: `${root}/v1/chat/completions`,
        responsesUrl: `${root}/v1/responses`,
        success: true,
        health: { url: `${root}/health`, success: true, statusCode: 200, latencyMilliseconds: 9 },
        openAi: { url: `${root}/v1/models`, success: true, statusCode: 200, latencyMilliseconds: 11 }
      })
    }

    return json(route, { error: `Unhandled test route ${request.method()} ${path}` }, 500)
  })
}

test('admin can inspect the fleet, add a path-prefixed node and test it', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/')

  await expect(page.getByRole('heading', { name: 'Gateway dashboard' })).toBeVisible()
  await expect(page.getByText('dgx-01')).toBeVisible()

  await page.getByRole('button', { name: 'DGX Nodes' }).click()
  await page.getByLabel('Name').fill('dgx-02')
  await page.getByLabel('Base address / service root').fill('http://localhost:3451/altropath')
  await page.getByLabel('Weight').fill('3')
  await page.getByLabel('Max concurrency').fill('8')
  await page.getByRole('button', { name: 'Add node' }).click()

  await expect(page.getByText('dgx-02')).toBeVisible()
  await expect(page.getByText('http://localhost:3451/altropath')).toBeVisible()

  const row = page.getByRole('row').filter({ hasText: 'dgx-02' })
  await row.getByRole('button', { name: 'Test' }).click()
  await expect(page.getByText('✓ Connection test: dgx-02')).toBeVisible()
  await expect(page.getByText(/altropath\/v1\/chat\/completions/)).toBeVisible()
})

test('routing strategy can be changed live from the admin UI', async ({ page }) => {
  await installAdminApi(page)
  await page.goto('/')

  await page.getByRole('button', { name: 'Routing' }).click()
  await page.getByLabel('Routing strategy').selectOption('WeightedRoundRobin')
  await page.getByRole('button', { name: 'Apply routing strategy' }).click()

  await expect(page.getByText('Routing policy updated live.')).toBeVisible()
  await expect(page.getByText('Weighted round robin').first()).toBeVisible()
})

test('authentication failures surface the Entra ID sign-in action', async ({ page }) => {
  await page.route('**/api/admin/**', route => route.fulfill({ status: 401, body: '' }))

  await page.goto('/')

  await expect(page.getByText('Authentication is required.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/auth/login')
})
