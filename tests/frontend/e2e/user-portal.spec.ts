import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('normal Entra user can manage personal API keys without loading admin APIs', async ({ page }) => {
  const credentials = [{
    id: 'credential-1',
    name: 'Project Alpha',
    keyPrefix: 'lp_alpha',
    enabled: true,
    createdAtUtc: '2026-09-17T06:00:00Z',
    lastUsedAtUtc: null
  }]

  let adminCalls = 0
  await page.route('**/api/admin/**', async route => {
    adminCalls += 1
    await json(route, { error: 'unexpected admin call' }, 500)
  })
  await page.route('**/api/me', route => json(route, {
    tenantId: 'tenant-1',
    objectId: 'object-1',
    principalName: 'user@example.com',
    displayName: 'Example User',
    roles: ['LlmProxy.User']
  }))
  await page.route('**/api/me/api-credentials', async route => {
    if (route.request().method() === 'POST') {
      const created = {
        id: 'credential-2',
        name: 'Development',
        keyPrefix: 'lp_new',
        enabled: true,
        createdAtUtc: '2026-09-17T07:00:00Z',
        lastUsedAtUtc: null,
        secret: 'lp_test_once'
      }
      credentials.unshift(created)
      await json(route, created)
      return
    }
    await json(route, credentials)
  })
  await page.route('**/api/me/rate-limits', route => json(route, [{
    id: 'user-rate-1',
    scope: 'user',
    scopeName: 'user@example.com',
    logicalModel: null,
    requestsPerWindow: 300,
    windowSeconds: 60,
    outputTokensPerWindow: 100000,
    maxOutputTokensPerRequest: 4096,
    enabled: true,
    updatedAtUtc: '2026-09-21T09:00:00Z'
  }]))

  await page.route('**/api/me/requests?take=50', route => json(route, [{
    requestId: 'request-1',
    startedAtUtc: '2026-09-17T08:00:00Z',
    logicalModel: 'agic-code-fast',
    surface: 'chat_completions',
    statusCode: 200,
    durationMilliseconds: 850,
    isStreaming: true,
    timeToFirstByteMilliseconds: 110,
    inputTokens: 10,
    outputTokens: 5,
    totalTokens: 15,
    errorCode: null,
    apiCredentialId: 'credential-1'
  }]))

  await page.route('**/api/me/content-logs**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/me/content-logs/1') {
      return json(route, {
        id: 1,
        requestId: 'request-1',
        startedAtUtc: '2026-09-17T08:00:00Z',
        completedAtUtc: '2026-09-17T08:00:01Z',
        surface: 'chat_completions',
        method: 'POST',
        path: '/v1/chat/completions',
        logicalModel: 'agic-code-fast',
        apiCredentialId: 'credential-1',
        statusCode: 200,
        requestBody: '{"model":"agic-code-fast","messages":[{"role":"user","content":"hello from user audit"}]}',
        responseBody: '{"choices":[{"message":{"content":"owned response"}}]}',
        attemptCount: 1,
        timeToFirstByteMilliseconds: 110,
        totalTokens: 15,
        errorCode: null
      })
    }
    return json(route, {
      items: [{
        id: 1,
        requestId: 'request-1',
        startedAtUtc: '2026-09-17T08:00:00Z',
        completedAtUtc: '2026-09-17T08:00:01Z',
        surface: 'chat_completions',
        method: 'POST',
        path: '/v1/chat/completions',
        logicalModel: 'agic-code-fast',
        apiCredentialId: 'credential-1',
        statusCode: 200
      }],
      total: 1,
      page: 1,
      pageSize: 20
    })
  })

  await page.route('**/api/me/usage?days=30', route => json(route, {
    windowDays: 30,
    sinceUtc: '2026-08-19T00:00:00Z',
    windowGranularity: 'utc_day',
    historicalRollupsUsed: false,
    requestCount: 12,
    errorCount: 1,
    inputTokens: 120,
    outputTokens: 80,
    totalTokens: 200,
    rateLimitedRequests: 0,
    credentials: [{
      apiCredentialId: 'credential-1',
      name: 'Project Alpha',
      keyPrefix: 'lp_alpha',
      requestCount: 12,
      errorCount: 1,
      inputTokens: 120,
      outputTokens: 80,
      totalTokens: 200,
      rateLimitedRequests: 0
    }]
  }))

  await page.goto('/admin/me')
  await expect(page.getByRole('heading', { name: 'My dashboard' })).toBeVisible()
  await expect(page.getByText('Example User')).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Project Alpha', exact: true })).toBeVisible()
  await expect(page.getByText('30d requests').locator('..').getByText('12', { exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'My limits' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'My recent calls' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'My request audit' })).toBeVisible()
  await expect(page.getByText('agic-code-fast', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('300', { exact: true }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Inspect' }).click()
  await expect(page.getByRole('heading', { name: 'My request detail' })).toBeVisible()
  await expect(page.getByText(/hello from user audit/)).toBeVisible()
  await expect(page.getByText(/owned response/)).toBeVisible()

  await page.getByPlaceholder('Project Alpha / Development').fill('Development')
  await page.getByRole('button', { name: 'Generate API key' }).click()
  await expect(page.getByText('lp_test_once')).toBeVisible()
  expect(adminCalls).toBe(0)
})
