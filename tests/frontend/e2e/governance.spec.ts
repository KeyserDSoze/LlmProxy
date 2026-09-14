import { expect, Route, test } from '@playwright/test'

async function json(route: Route, body: unknown, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

test('admin can review grouped usage and create a caller rate limit', async ({ page }) => {
  const groups = [{ id: 'group-1', name: 'Development CRM', description: 'CRM team', credentialCount: 1, createdAtUtc: '2026-09-14T10:00:00Z', updatedAtUtc: '2026-09-14T10:00:00Z' }]
  const credentials = [{ id: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', enabled: true, usageGroupId: 'group-1', createdAtUtc: '2026-09-14T10:00:00Z' }]
  const policies: unknown[] = []

  await page.route('**/api/admin/**', async route => {
    const request = route.request()
    const url = new URL(request.url())
    const path = url.pathname

    if (request.method() === 'GET' && path === '/api/admin/usage-groups') return json(route, groups)
    if (request.method() === 'GET' && path === '/api/admin/governance/credentials') return json(route, credentials)
    if (request.method() === 'GET' && path === '/api/admin/rate-limits') return json(route, policies)
    if (request.method() === 'GET' && path === '/api/admin/models') return json(route, [{ id: 'model-1', publicName: 'agic-code-fast', providerModelName: 'provider', supportsStreaming: true, supportsTools: true, enabled: true }])
    if (request.method() === 'GET' && path === '/api/admin/usage/summary') return json(route, {
      windowDays: 30, sinceUtc: '2026-08-15T10:00:00Z', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500,
      rateLimitedRequests: 3, capacityExhaustedRequests: 1,
      groups: [{ usageGroupId: 'group-1', name: 'Development CRM', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3, averageTtftMilliseconds: 210, averageDurationMilliseconds: 1200 }],
      credentials: [{ apiCredentialId: 'credential-1', name: 'Copilot CRM', keyPrefix: 'lp_abcd', usageGroupId: 'group-1', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }],
      models: [{ logicalModel: 'agic-code-fast', requestCount: 42, errorCount: 2, inputTokens: 1000, outputTokens: 500, totalTokens: 1500, rateLimitedRequests: 3 }]
    })
    if (request.method() === 'POST' && path === '/api/admin/rate-limits') {
      const input = request.postDataJSON() as { apiCredentialId: string; logicalModel: string | null; requestsPerWindow: number; windowSeconds: number; enabled: boolean }
      const created = { id: 'rate-1', credentialName: 'Copilot CRM', keyPrefix: 'lp_abcd', ...input, createdAtUtc: '2026-09-14T10:00:00Z', updatedAtUtc: '2026-09-14T10:00:00Z' }
      policies.push(created)
      return json(route, created, 201)
    }
    return json(route, { error: `${request.method()} ${path}` }, 500)
  })

  await page.goto('/admin/governance')
  await expect(page.getByRole('heading', { name: 'Usage & Governance', exact: true }).first()).toBeVisible()
  await expect(page.getByText('Development CRM').first()).toBeVisible()
  await expect(page.getByText('Copilot CRM').first()).toBeVisible()
  await expect(page.getByText('agic-code-fast').first()).toBeVisible()
  await expect(page.getByText('1,500')).toBeVisible()

  await page.getByLabel('Requests per window').fill('2')
  await page.getByLabel('Window seconds').fill('60')
  await page.getByRole('button', { name: 'Add rate limit' }).click()
  await expect(page.getByText('Rate-limit policy created and applied live.')).toBeVisible()
  await expect(page.getByText('2 / 60s')).toBeVisible()
})
