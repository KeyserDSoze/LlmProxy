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
  await expect(page.getByRole('heading', { name: 'My API Keys' })).toBeVisible()
  await expect(page.getByText('Example User')).toBeVisible()
  await expect(page.getByText('Project Alpha', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('30d requests').locator('..').getByText('12', { exact: true })).toBeVisible()

  await page.getByPlaceholder('Project Alpha / Development').fill('Development')
  await page.getByRole('button', { name: 'Generate API key' }).click()
  await expect(page.getByText('lp_test_once')).toBeVisible()
  expect(adminCalls).toBe(0)
})
